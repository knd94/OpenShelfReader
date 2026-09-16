using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using OpenShelf.Core;

namespace OpenShelf.Infrastructure;

public sealed class EspeakSpeechEngine : ISpeechEngine, ISpeechDiagnostics
{
    private const uint SemFailCriticalErrors = 0x0001;
    private const uint SemNoGpFaultErrorBox = 0x0002;
    private static readonly object ProcessStartSync = new();

    private readonly object _sync = new();
    private readonly EspeakDiscoveryOptions _discoveryOptions;

    private ImmutableArray<SpeechVoice> _voices = ImmutableArray<SpeechVoice>.Empty;
    private SpeechState _state = SpeechState.Stopped;
    private EspeakRuntime? _runtime;
    private CancellationTokenSource? _playbackCancellation;
    private Task? _playbackTask;
    private Process? _currentProcess;
    private IReadOnlyList<string> _segments = Array.Empty<string>();
    private int _currentIndex;
    private int? _requestedIndex;
    private bool _paused;
    private TaskCompletionSource<bool>? _resumeSignal;
    private bool _disposed;

    public EspeakSpeechEngine(EspeakDiscoveryOptions? discoveryOptions = null)
    {
        _discoveryOptions = discoveryOptions ?? new EspeakDiscoveryOptions();
    }

    public SpeechState State
    {
        get
        {
            lock (_sync)
            {
                return _state;
            }
        }
    }

    public ImmutableArray<SpeechVoice> Voices
    {
        get
        {
            lock (_sync)
            {
                return _voices;
            }
        }
    }

    public string? InitializationError { get; private set; }

    public event EventHandler<SpeechProgress>? ProgressChanged;
    public event EventHandler<SpeechState>? StateChanged;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        InitializationError = null;
        var runtime = await Task.Run(
                () => EspeakExecutableLocator.Find(_discoveryOptions),
                cancellationToken)
            .ConfigureAwait(false);
        if (runtime is null)
        {
            InitializationError = "The eSpeak executable or data directory was not found.";
            lock (_sync)
            {
                _runtime = null;
                _voices = ImmutableArray<SpeechVoice>.Empty;
            }

            ChangeState(SpeechState.Unavailable);
            return;
        }

        try
        {
            var voices = await QueryVoicesAsync(runtime, cancellationToken)
                .ConfigureAwait(false);
            lock (_sync)
            {
                _runtime = runtime;
                _voices = voices;
            }

            ChangeState(SpeechState.Stopped);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            InitializationError = "eSpeak voice discovery timed out.";
            lock (_sync)
            {
                _runtime = null;
                _voices = ImmutableArray<SpeechVoice>.Empty;
            }

            ChangeState(SpeechState.Unavailable);
        }
        catch (Exception exception) when (
            exception is IOException
                or InvalidOperationException
                or Win32Exception)
        {
            InitializationError = $"{exception.GetType().Name}: {exception.Message}";
            lock (_sync)
            {
                _runtime = null;
                _voices = ImmutableArray<SpeechVoice>.Empty;
            }

            ChangeState(SpeechState.Unavailable);
        }
    }

    public Task RefreshVoicesAsync(CancellationToken cancellationToken = default) =>
        InitializeAsync(cancellationToken);

    public async Task SpeakAsync(
        IReadOnlyList<string> sentenceSegments,
        SpeechOptions options,
        int startIndex,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(sentenceSegments);
        await StopAsync().ConfigureAwait(false);

        EspeakRuntime? runtime;
        lock (_sync)
        {
            runtime = _runtime;
        }

        if (runtime is null)
        {
            ChangeState(SpeechState.Unavailable);
            return;
        }

        if (sentenceSegments.Count == 0)
        {
            ChangeState(SpeechState.Stopped);
            return;
        }

        var boundedStartIndex = Math.Clamp(startIndex, 0, sentenceSegments.Count - 1);
        var boundedOptions = options.Clamped;
        var linkedCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task playbackTask;

        lock (_sync)
        {
            _segments = sentenceSegments.ToArray();
            _currentIndex = boundedStartIndex;
            _requestedIndex = null;
            _paused = false;
            _resumeSignal = null;
            _playbackCancellation = linkedCancellation;
            playbackTask = RunPlaybackAsync(
                runtime,
                boundedOptions,
                boundedStartIndex,
                linkedCancellation.Token);
            _playbackTask = playbackTask;
        }

        try
        {
            await playbackTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
        {
            // Stop and caller cancellation both end playback without escaping the engine.
        }
        finally
        {
            var shouldStop = false;
            lock (_sync)
            {
                if (ReferenceEquals(_playbackCancellation, linkedCancellation))
                {
                    _playbackCancellation = null;
                    _playbackTask = null;
                    _currentProcess = null;
                    _paused = false;
                    _resumeSignal = null;
                    shouldStop = _state != SpeechState.Unavailable;
                }
            }

            linkedCancellation.Dispose();
            if (shouldStop)
            {
                ChangeState(SpeechState.Stopped);
            }
        }
    }

    public Task PreviewAsync(
        SpeechOptions options,
        string sampleText,
        CancellationToken cancellationToken) =>
        SpeakAsync([sampleText], options, startIndex: 0, cancellationToken);

    public Task PauseAsync()
    {
        var changed = false;
        lock (_sync)
        {
            if (_state == SpeechState.Speaking && _playbackTask is not null)
            {
                _paused = true;
                _resumeSignal ??= CreateSignal();
                KillProcess(_currentProcess);
                changed = true;
            }
        }

        if (changed)
        {
            ChangeState(SpeechState.Paused);
        }

        return Task.CompletedTask;
    }

    public Task ResumeAsync()
    {
        TaskCompletionSource<bool>? signal = null;
        var changed = false;
        lock (_sync)
        {
            if (_state == SpeechState.Paused && _playbackTask is not null)
            {
                _paused = false;
                signal = _resumeSignal;
                _resumeSignal = null;
                changed = true;
            }
        }

        signal?.TrySetResult(true);
        if (changed)
        {
            ChangeState(SpeechState.Speaking);
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cancellation;
        Task? playbackTask;
        TaskCompletionSource<bool>? resumeSignal;
        Process? process;

        lock (_sync)
        {
            cancellation = _playbackCancellation;
            playbackTask = _playbackTask;
            resumeSignal = _resumeSignal;
            process = _currentProcess;
        }

        if (cancellation is null)
        {
            if (State != SpeechState.Unavailable)
            {
                ChangeState(SpeechState.Stopped);
            }

            return;
        }

        cancellation.Cancel();
        resumeSignal?.TrySetResult(true);
        KillProcess(process);
        if (playbackTask is not null)
        {
            try
            {
                await playbackTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected when stopping.
            }
        }

        if (State != SpeechState.Unavailable)
        {
            ChangeState(SpeechState.Stopped);
        }
    }

    public Task PreviousAsync() => RequestMoveAsync(-1);

    public Task NextAsync() => RequestMoveAsync(1);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await StopAsync().ConfigureAwait(false);
        _disposed = true;
    }

    public string CreateSpeechDiagnostics()
    {
        var lines = new List<string>
        {
            "Built-in eSpeak NG:",
            $"  State: {State}",
            $"  Voices: {Voices.Length}",
            $"  Runtime: {(_runtime?.IsBundled == true ? "bundled" : _runtime is null ? "not found" : "system")}" 
        };
        if (!string.IsNullOrWhiteSpace(InitializationError))
        {
            lines.Add($"  Initialization error: {InitializationError}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private async Task RunPlaybackAsync(
        EspeakRuntime runtime,
        SpeechOptions options,
        int startIndex,
        CancellationToken cancellationToken)
    {
        var index = startIndex;
        ChangeState(SpeechState.Speaking);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Task? pauseTask = null;
            string? segment = null;
            int segmentCount;
            lock (_sync)
            {
                if (_requestedIndex is { } requested)
                {
                    index = Math.Clamp(requested, 0, _segments.Count - 1);
                    _requestedIndex = null;
                }

                _currentIndex = index;
                segmentCount = _segments.Count;
                if (index < 0 || index >= segmentCount)
                {
                    break;
                }

                if (_paused)
                {
                    _resumeSignal ??= CreateSignal();
                    pauseTask = _resumeSignal.Task;
                }
                else
                {
                    segment = _segments[index];
                }
            }

            if (pauseTask is not null)
            {
                await pauseTask.WaitAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (string.IsNullOrWhiteSpace(segment))
            {
                index++;
                continue;
            }

            RaiseProgress(new SpeechProgress(
                index,
                segmentCount,
                segment,
                CharacterOffset: 0,
                CharacterLength: segment.Length));

            try
            {
                await SpeakSegmentAsync(
                        runtime,
                        segment,
                        options,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is IOException
                    or InvalidOperationException
                    or Win32Exception)
            {
                lock (_sync)
                {
                    _runtime = null;
                    _voices = ImmutableArray<SpeechVoice>.Empty;
                }

                ChangeState(SpeechState.Unavailable);
                return;
            }

            lock (_sync)
            {
                if (_requestedIndex is { } requested)
                {
                    index = Math.Clamp(requested, 0, _segments.Count - 1);
                    _requestedIndex = null;
                }
                else if (!_paused)
                {
                    index++;
                }
            }
        }
    }

    private async Task SpeakSegmentAsync(
        EspeakRuntime runtime,
        string text,
        SpeechOptions options,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = CreateStartInfo(runtime)
        };
        process.StartInfo.ArgumentList.Add("-s");
        process.StartInfo.ArgumentList.Add(
            options.WordsPerMinute.ToString(CultureInfo.InvariantCulture));
        process.StartInfo.ArgumentList.Add("-a");
        process.StartInfo.ArgumentList.Add(
            options.Volume.ToString(CultureInfo.InvariantCulture));
        process.StartInfo.ArgumentList.Add("-p");
        process.StartInfo.ArgumentList.Add(
            Math.Clamp(
                    (int)Math.Round(50 * options.Pitch),
                    0,
                    99)
                .ToString(CultureInfo.InvariantCulture));
        process.StartInfo.ArgumentList.Add("-b");
        process.StartInfo.ArgumentList.Add("1");
        var voiceId = NormalizeVoiceId(options.VoiceId);
        if (!string.IsNullOrWhiteSpace(voiceId))
        {
            process.StartInfo.ArgumentList.Add("-v");
            process.StartInfo.ArgumentList.Add(voiceId);
        }

        process.StartInfo.ArgumentList.Add("--stdin");
        if (!StartProcess(process))
        {
            throw new InvalidOperationException("eSpeak could not be started.");
        }

        lock (_sync)
        {
            _currentProcess = process;
        }

        using var registration = cancellationToken.Register(
            static state => KillProcess((Process?)state),
            process);
        try
        {
            await process.StandardInput
                .WriteAsync(text.AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            process.StandardInput.Close();
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                bool wasInterrupted;
                lock (_sync)
                {
                    wasInterrupted = _paused || _requestedIndex.HasValue;
                }

                if (!wasInterrupted)
                {
                    throw new InvalidOperationException(
                        $"eSpeak playback exited with code {process.ExitCode}.");
                }
            }
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_currentProcess, process))
                {
                    _currentProcess = null;
                }
            }
        }
    }

    private Task RequestMoveAsync(int delta)
    {
        lock (_sync)
        {
            if (_playbackTask is null || _segments.Count == 0)
            {
                return Task.CompletedTask;
            }

            _requestedIndex = Math.Clamp(
                _currentIndex + delta,
                0,
                _segments.Count - 1);
            KillProcess(_currentProcess);
        }

        return Task.CompletedTask;
    }

    private static async Task<ImmutableArray<SpeechVoice>> QueryVoicesAsync(
        EspeakRuntime runtime,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var process = new Process
        {
            StartInfo = CreateStartInfo(runtime)
        };
        process.StartInfo.ArgumentList.Add("--voices");
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.StandardOutputEncoding = Encoding.UTF8;
        process.StartInfo.StandardErrorEncoding = Encoding.UTF8;

        if (!StartProcess(process))
        {
            throw new InvalidOperationException("eSpeak could not be started.");
        }

        using var registration = timeout.Token.Register(
            static state => KillProcess((Process?)state),
            process);
        var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        var output = await outputTask.ConfigureAwait(false);
        _ = await errorTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"eSpeak voice discovery exited with code {process.ExitCode}.");
        }

        return ParseVoices(output);
    }

    private static ImmutableArray<SpeechVoice> ParseVoices(string output)
    {
        var builder = ImmutableArray.CreateBuilder<SpeechVoice>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in output.Split(
                     ['\r', '\n'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var fields = line.Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (fields.Length < 5 || !int.TryParse(fields[0], out _))
            {
                continue;
            }

            var language = fields[1];
            var voiceName = fields[3];
            var voiceFile = fields[4];
            if (voiceFile.StartsWith("mb\\", StringComparison.OrdinalIgnoreCase)
                || voiceFile.StartsWith("mb/", StringComparison.OrdinalIgnoreCase)
                || voiceName.Contains("mbrola", StringComparison.OrdinalIgnoreCase))
            {
                // MBROLA catalogue entries require a separate runtime and voice
                // databases. Exposing them with only eSpeak NG bundled can make
                // the native process dereference an unavailable voice.
                continue;
            }

            var id = language;
            if (seen.Add(id))
            {
                builder.Add(new SpeechVoice(
                    $"builtin:espeak:{id}",
                    $"Built-in — {voiceName.Replace('_', ' ')}",
                    language,
                    SpeechProvider.BuiltIn,
                    SpeechArchitecture.Current));
            }
        }

        var currentLocale = CultureInfo.CurrentUICulture.Name;
        var currentLanguage = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        return builder
            .OrderBy(voice => VoicePreference(voice, currentLocale, currentLanguage))
            .ThenBy(voice => voice.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToImmutableArray();
    }

    private static string? NormalizeVoiceId(string? voiceId)
    {
        const string prefix = "builtin:espeak:";
        return voiceId?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true
            ? voiceId[prefix.Length..]
            : voiceId;
    }

    private static ProcessStartInfo CreateStartInfo(EspeakRuntime runtime)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = runtime.ExecutablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            StandardInputEncoding = Encoding.UTF8,
            WorkingDirectory =
                Path.GetDirectoryName(runtime.ExecutablePath) ?? AppContext.BaseDirectory
        };
        if (runtime.DataParentDirectory is not null)
        {
            startInfo.Environment["ESPEAK_DATA_PATH"] = runtime.DataParentDirectory;
            startInfo.ArgumentList.Add(
                $"--path={runtime.DataParentDirectory}");
        }

        return startInfo;
    }

    private static int VoicePreference(
        SpeechVoice voice,
        string currentLocale,
        string currentLanguage)
    {
        if (string.Equals(
                voice.Language,
                currentLocale,
                StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (string.Equals(
                voice.Language,
                currentLanguage,
                StringComparison.OrdinalIgnoreCase)
            || voice.Language.StartsWith(
                currentLanguage + "-",
                StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (voice.Language.StartsWith("en-", StringComparison.OrdinalIgnoreCase)
            || string.Equals(voice.Language, "en", StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        return 3;
    }

    private static bool StartProcess(Process process)
    {
        if (!OperatingSystem.IsWindows())
        {
            return process.Start();
        }

        // Report native synthesizer failures through an exit code instead of
        // allowing Windows Error Reporting to place a modal crash dialog over
        // the reader. A child inherits this mode when it is created.
        lock (ProcessStartSync)
        {
            var previousMode = NativeMethods.SetErrorMode(
                SemFailCriticalErrors | SemNoGpFaultErrorBox);
            try
            {
                return process.Start();
            }
            finally
            {
                _ = NativeMethods.SetErrorMode(previousMode);
            }
        }
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll")]
        internal static extern uint SetErrorMode(uint mode);
    }

    private void ChangeState(SpeechState state)
    {
        EventHandler<SpeechState>? handlers;
        lock (_sync)
        {
            if (_state == state)
            {
                return;
            }

            _state = state;
            handlers = StateChanged;
        }

        InvokeHandlers(handlers, state);
    }

    private void RaiseProgress(SpeechProgress progress)
    {
        EventHandler<SpeechProgress>? handlers;
        lock (_sync)
        {
            handlers = ProgressChanged;
        }

        InvokeHandlers(handlers, progress);
    }

    private void InvokeHandlers<T>(
        EventHandler<T>? handlers,
        T value)
    {
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<T> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, value);
            }
            catch
            {
                // A UI subscriber must not break speech playback.
            }
        }
    }

    private static TaskCompletionSource<bool> CreateSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void KillProcess(Process? process)
    {
        if (process is null)
        {
            return;
        }

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
            // The process already exited or cannot be controlled on this platform.
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
