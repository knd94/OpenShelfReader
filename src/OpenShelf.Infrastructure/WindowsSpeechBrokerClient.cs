using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenShelf.Infrastructure;

internal interface IWindowsSpeechBrokerClient : IAsyncDisposable
{
    string Name { get; }
    bool IsRunning { get; }
    string? LastError { get; }
    Task<IReadOnlyList<WindowsBrokerVoice>> ListVoicesAsync(
        CancellationToken cancellationToken);
    Task SpeakAsync(
        string voiceId,
        string text,
        double rate,
        int volume,
        double pitch,
        CancellationToken cancellationToken);
    Task PauseAsync(CancellationToken cancellationToken);
    Task ResumeAsync(CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
}

internal sealed class WindowsSpeechBrokerClient : IWindowsSpeechBrokerClient
{
    private const uint SemFailCriticalErrors = 0x0001;
    private const uint SemNoGpFaultErrorBox = 0x0002;
    private static readonly object ProcessStartSync = new();
    private static readonly JsonSerializerOptions JsonOptions = new(
        JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false);

    private readonly string _executablePath;
    private readonly string _provider;
    private readonly TimeSpan _requestTimeout;
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<string, PendingRequest> _pending = new();
    private readonly object _sync = new();
    private readonly Queue<string> _recentErrors = new();

    private Process? _process;
    private Task? _outputTask;
    private Task? _errorTask;
    private StreamWriter? _input;
    private bool _disposed;

    public WindowsSpeechBrokerClient(
        string name,
        string executablePath,
        string provider,
        TimeSpan? requestTimeout = null)
    {
        Name = name;
        _executablePath = executablePath;
        _provider = provider;
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(20);
    }

    public string Name { get; }

    public bool IsRunning
    {
        get
        {
            lock (_sync)
            {
                return _process is { HasExited: false };
            }
        }
    }

    public string? LastError { get; private set; }

    public async Task<IReadOnlyList<WindowsBrokerVoice>> ListVoicesAsync(
        CancellationToken cancellationToken)
    {
        var response = await SendAsync(
                new WindowsBrokerRequest(null, "list"),
                terminalTypes: ["voices", "error"],
                cancellationToken)
            .ConfigureAwait(false);
        ThrowIfError(response);
        return response.Voices ?? Array.Empty<WindowsBrokerVoice>();
    }

    public async Task SpeakAsync(
        string voiceId,
        string text,
        double rate,
        int volume,
        double pitch,
        CancellationToken cancellationToken)
    {
        var response = await SendAsync(
                new WindowsBrokerRequest(
                    null,
                    "speak",
                    voiceId,
                    text,
                    rate,
                    volume,
                    pitch),
                terminalTypes: ["completed", "cancelled", "error"],
                cancellationToken,
                requestTimeout: TimeSpan.FromMinutes(3))
            .ConfigureAwait(false);
        ThrowIfError(response);
    }

    public Task PauseAsync(CancellationToken cancellationToken) =>
        SendControlAsync("pause", cancellationToken);

    public Task ResumeAsync(CancellationToken cancellationToken) =>
        SendControlAsync("resume", cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) =>
        SendControlAsync("stop", cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Process? process;
        lock (_sync)
        {
            process = _process;
        }

        if (process is { HasExited: false })
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await SendAsync(
                        new WindowsBrokerRequest(null, "shutdown"),
                        terminalTypes: ["acknowledged", "error"],
                        timeout.Token)
                    .ConfigureAwait(false);
            }
            catch
            {
                // A broker that cannot acknowledge shutdown is terminated below.
            }
        }

        TerminateProcess("Speech broker was disposed.");
        _startGate.Dispose();
        _writeGate.Dispose();
    }

    private async Task SendControlAsync(
        string command,
        CancellationToken cancellationToken)
    {
        var response = await SendAsync(
                new WindowsBrokerRequest(null, command),
                terminalTypes: ["acknowledged", "error"],
                cancellationToken,
                requestTimeout: TimeSpan.FromSeconds(5))
            .ConfigureAwait(false);
        ThrowIfError(response);
    }

    private async Task<WindowsBrokerResponse> SendAsync(
        WindowsBrokerRequest request,
        IReadOnlyCollection<string> terminalTypes,
        CancellationToken cancellationToken,
        TimeSpan? requestTimeout = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        var requestId = Guid.NewGuid().ToString("N");
        var identifiedRequest = request with { RequestId = requestId };
        var pending = new PendingRequest(terminalTypes);
        if (!_pending.TryAdd(requestId, pending))
        {
            throw new InvalidOperationException("A speech request ID collision occurred.");
        }

        try
        {
            var json = JsonSerializer.Serialize(identifiedRequest, JsonOptions);
            if (json.Length > 256 * 1024)
            {
                throw new InvalidOperationException("The speech request is too large.");
            }

            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var input = _input
                    ?? throw new InvalidOperationException("Speech broker input is unavailable.");
                await input.WriteLineAsync(json.AsMemory(), cancellationToken)
                    .ConfigureAwait(false);
                await input.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _writeGate.Release();
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            timeout.CancelAfter(requestTimeout ?? _requestTimeout);
            try
            {
                return await pending.Completion.Task
                    .WaitAsync(timeout.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                LastError = $"{Name} did not respond in time.";
                TerminateProcess(LastError);
                throw new TimeoutException(LastError);
            }
        }
        finally
        {
            _pending.TryRemove(requestId, out _);
        }
    }

    private async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (IsRunning)
        {
            return;
        }

        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsRunning)
            {
                return;
            }

            if (!File.Exists(_executablePath))
            {
                throw new FileNotFoundException(
                    $"The {Name} executable was not found.",
                    _executablePath);
            }

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = _executablePath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardInputEncoding = Utf8NoBom,
                    StandardOutputEncoding = Utf8NoBom,
                    StandardErrorEncoding = Utf8NoBom,
                    WorkingDirectory = Path.GetDirectoryName(_executablePath)
                        ?? AppContext.BaseDirectory
                },
                EnableRaisingEvents = true
            };
            process.StartInfo.ArgumentList.Add("--provider");
            process.StartInfo.ArgumentList.Add(_provider);
            process.Exited += Process_Exited;
            if (!StartProcess(process))
            {
                process.Dispose();
                throw new InvalidOperationException($"{Name} could not be started.");
            }

            lock (_sync)
            {
                _process = process;
                _input = process.StandardInput;
                _outputTask = ReadOutputAsync(process);
                _errorTask = ReadErrorsAsync(process);
                LastError = null;
            }
        }
        catch (Exception exception) when (
            exception is IOException
                or InvalidOperationException
                or Win32Exception
                or FileNotFoundException)
        {
            LastError = ProgramSafeMessage(exception);
            throw;
        }
        finally
        {
            _startGate.Release();
        }
    }

    private async Task ReadOutputAsync(Process process)
    {
        try
        {
            while (await process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                line = line.TrimStart('\uFEFF');
                WindowsBrokerResponse? response;
                try
                {
                    response = JsonSerializer.Deserialize<WindowsBrokerResponse>(
                        line,
                        JsonOptions);
                }
                catch (JsonException exception)
                {
                    LastError = $"{Name} returned malformed data: {ProgramSafeMessage(exception)}";
                    continue;
                }

                if (response?.RequestId is null
                    || !_pending.TryGetValue(response.RequestId, out var pending))
                {
                    continue;
                }

                if (pending.TerminalTypes.Contains(response.Type))
                {
                    pending.Completion.TrySetResult(response);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            LastError = $"{Name} output ended: {ProgramSafeMessage(exception)}";
        }
        finally
        {
            if (!_disposed)
            {
                FailPending(LastError ?? $"{Name} stopped unexpectedly.");
            }
        }
    }

    private async Task ReadErrorsAsync(Process process)
    {
        try
        {
            while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                var safeLine = line.Replace('\r', ' ').Replace('\n', ' ').Trim();
                if (safeLine.Length == 0)
                {
                    continue;
                }

                lock (_sync)
                {
                    _recentErrors.Enqueue(safeLine);
                    while (_recentErrors.Count > 5)
                    {
                        _recentErrors.Dequeue();
                    }
                }
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            // Process exit is handled by the output reader and Exited event.
        }
    }

    private void Process_Exited(object? sender, EventArgs e)
    {
        string detail;
        lock (_sync)
        {
            detail = _recentErrors.Count == 0
                ? "No diagnostic message was returned."
                : string.Join(" | ", _recentErrors);
        }

        LastError = $"{Name} exited unexpectedly. {detail}";
        FailPending(LastError);
    }

    private void TerminateProcess(string reason)
    {
        Process? process;
        lock (_sync)
        {
            process = _process;
            _process = null;
            _input = null;
            _outputTask = null;
            _errorTask = null;
        }

        if (process is not null)
        {
            process.Exited -= Process_Exited;
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception exception) when (
                exception is InvalidOperationException
                    or Win32Exception
                    or NotSupportedException)
            {
                // It already exited or cannot be controlled.
            }
            finally
            {
                process.Dispose();
            }
        }

        FailPending(reason);
    }

    private void FailPending(string message)
    {
        foreach (var pending in _pending.Values)
        {
            pending.Completion.TrySetException(
                new InvalidOperationException(message));
        }
    }

    private static void ThrowIfError(WindowsBrokerResponse response)
    {
        if (string.Equals(response.Type, "error", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(response.Message)
                    ? "The Windows speech provider reported an error."
                    : response.Message);
        }
    }

    private static bool StartProcess(Process process)
    {
        lock (ProcessStartSync)
        {
            var previous = SetErrorMode(SemFailCriticalErrors | SemNoGpFaultErrorBox);
            try
            {
                return process.Start();
            }
            finally
            {
                _ = SetErrorMode(previous);
            }
        }
    }

    private static string ProgramSafeMessage(Exception exception) =>
        exception.Message.Replace('\r', ' ').Replace('\n', ' ').Trim();

    [DllImport("kernel32.dll")]
    private static extern uint SetErrorMode(uint mode);

    private sealed record PendingRequest(IReadOnlyCollection<string> TerminalTypes)
    {
        public TaskCompletionSource<WindowsBrokerResponse> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

internal sealed record WindowsBrokerRequest(
    string? RequestId,
    string Command,
    string? VoiceId = null,
    string? Text = null,
    double Rate = 1,
    int Volume = 100,
    double Pitch = 1);

internal sealed record WindowsBrokerVoice(
    string Id,
    string DisplayName,
    string Language,
    string Provider,
    string Architecture,
    bool IsAvailable,
    string? UnavailableReason,
    bool IsDefault);

internal sealed record WindowsBrokerResponse(
    string? RequestId,
    string Type,
    string? Message,
    IReadOnlyList<WindowsBrokerVoice>? Voices);
