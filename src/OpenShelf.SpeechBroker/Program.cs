using System.Globalization;
using System.Runtime.InteropServices;
using System.Security;
using System.Speech.Synthesis;
using System.Text.Json;
using System.Text.Json.Serialization;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.SpeechSynthesis;

namespace OpenShelf.SpeechBroker;

internal static class Program
{
    private const uint SemFailCriticalErrors = 0x0001;
    private const uint SemNoGpFaultErrorBox = 0x0002;

    [STAThread]
    public static async Task<int> Main(string[] args)
    {
        _ = SetErrorMode(SemFailCriticalErrors | SemNoGpFaultErrorBox);
        Console.InputEncoding = new System.Text.UTF8Encoding(
            encoderShouldEmitUTF8Identifier: false);
        Console.OutputEncoding = new System.Text.UTF8Encoding(
            encoderShouldEmitUTF8Identifier: false);
        var providerName = ReadOption(args, "--provider") ?? "sapi";

        try
        {
            await using IVoiceProvider provider = providerName.ToLowerInvariant() switch
            {
                "modern" => new ModernWindowsVoiceProvider(),
                "sapi" => new SapiVoiceProvider(),
                _ => throw new ArgumentException($"Unknown provider '{providerName}'.")
            };
            var host = new BrokerHost(provider);
            return await host.RunAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"{exception.GetType().Name}: {Sanitize(exception.Message)}");
            return 2;
        }
    }

    private static string? ReadOption(string[] args, string name)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }

        return null;
    }

    internal static string Sanitize(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? "Unknown speech error."
            : value.Replace('\r', ' ').Replace('\n', ' ').Trim();

    [DllImport("kernel32.dll")]
    private static extern uint SetErrorMode(uint mode);
}

internal sealed class BrokerHost
{
    private const int MaximumRequestLength = 256 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(
        JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IVoiceProvider _provider;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public BrokerHost(IVoiceProvider provider)
    {
        _provider = provider;
        _provider.Completed += Provider_Completed;
    }

    public async Task<int> RunAsync()
    {
        while (await Console.In.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            line = TrimEncodingPreamble(line);
            if (line.Length > MaximumRequestLength)
            {
                await WriteAsync(BrokerResponse.Error(null, "Request is too large."))
                    .ConfigureAwait(false);
                continue;
            }

            BrokerRequest? request;
            try
            {
                request = JsonSerializer.Deserialize<BrokerRequest>(line, JsonOptions);
            }
            catch (JsonException exception)
            {
                await WriteAsync(BrokerResponse.Error(null, exception.Message))
                    .ConfigureAwait(false);
                continue;
            }

            if (request is null || string.IsNullOrWhiteSpace(request.Command))
            {
                await WriteAsync(BrokerResponse.Error(request?.RequestId, "Command is required."))
                    .ConfigureAwait(false);
                continue;
            }

            try
            {
                switch (request.Command.ToLowerInvariant())
                {
                    case "list":
                        var voices = await _provider.ListVoicesAsync().ConfigureAwait(false);
                        await WriteAsync(new BrokerResponse(
                                request.RequestId,
                                "voices",
                                null,
                                voices))
                            .ConfigureAwait(false);
                        break;
                    case "speak":
                        if (string.IsNullOrWhiteSpace(request.Text))
                        {
                            await WriteAsync(BrokerResponse.Error(
                                    request.RequestId,
                                    "Text is required."))
                                .ConfigureAwait(false);
                            break;
                        }

                        await WriteAsync(new BrokerResponse(
                                request.RequestId,
                                "started",
                                null,
                                null))
                            .ConfigureAwait(false);
                        _ = StartSpeakSafelyAsync(request);
                        break;
                    case "pause":
                        _provider.Pause();
                        await WriteAsync(BrokerResponse.Acknowledged(request.RequestId))
                            .ConfigureAwait(false);
                        break;
                    case "resume":
                        _provider.Resume();
                        await WriteAsync(BrokerResponse.Acknowledged(request.RequestId))
                            .ConfigureAwait(false);
                        break;
                    case "stop":
                        _provider.Stop();
                        await WriteAsync(BrokerResponse.Acknowledged(request.RequestId))
                            .ConfigureAwait(false);
                        break;
                    case "shutdown":
                        _provider.Stop();
                        await WriteAsync(BrokerResponse.Acknowledged(request.RequestId))
                            .ConfigureAwait(false);
                        return 0;
                    default:
                        await WriteAsync(BrokerResponse.Error(
                                request.RequestId,
                                $"Unknown command '{request.Command}'."))
                            .ConfigureAwait(false);
                        break;
                }
            }
            catch (Exception exception)
            {
                await WriteAsync(BrokerResponse.Error(
                        request.RequestId,
                        Program.Sanitize(exception.Message)))
                    .ConfigureAwait(false);
            }
        }

        return 0;
    }

    private static string TrimEncodingPreamble(string value)
    {
        var trimmed = value.TrimStart('\uFEFF');
        return trimmed.StartsWith("ï»¿", StringComparison.Ordinal)
            ? trimmed[3..]
            : trimmed;
    }

    private async Task StartSpeakSafelyAsync(BrokerRequest request)
    {
        try
        {
            await _provider.StartSpeakAsync(request).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await WriteAsync(BrokerResponse.Error(
                    request.RequestId,
                    Program.Sanitize(exception.Message)))
                .ConfigureAwait(false);
        }
    }

    private void Provider_Completed(object? sender, ProviderCompletion completion)
    {
        _ = WriteAsync(new BrokerResponse(
            completion.RequestId,
            completion.Cancelled ? "cancelled" : completion.Error is null ? "completed" : "error",
            completion.Error,
            null));
    }

    private async Task WriteAsync(BrokerResponse response)
    {
        var json = JsonSerializer.Serialize(response, JsonOptions);
        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await Console.Out.WriteLineAsync(json).ConfigureAwait(false);
            await Console.Out.FlushAsync().ConfigureAwait(false);
        }
        catch (IOException)
        {
            // The parent process has exited.
        }
        finally
        {
            _writeGate.Release();
        }
    }
}

internal interface IVoiceProvider : IAsyncDisposable
{
    event EventHandler<ProviderCompletion>? Completed;
    Task<IReadOnlyList<BrokerVoice>> ListVoicesAsync();
    Task StartSpeakAsync(BrokerRequest request);
    void Pause();
    void Resume();
    void Stop();
}

internal sealed class SapiVoiceProvider : IVoiceProvider
{
    private readonly object _sync = new();
    private readonly System.Speech.Synthesis.SpeechSynthesizer _synthesizer = new();
    private Dictionary<string, string> _voiceNames = new(StringComparer.OrdinalIgnoreCase);
    private string? _activeRequestId;

    public SapiVoiceProvider()
    {
        _synthesizer.SpeakCompleted += Synthesizer_SpeakCompleted;
    }

    public event EventHandler<ProviderCompletion>? Completed;

    public Task<IReadOnlyList<BrokerVoice>> ListVoicesAsync()
    {
        var voices = new List<BrokerVoice>();
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var defaultName = _synthesizer.Voice.Name;
        foreach (var installed in _synthesizer.GetInstalledVoices())
        {
            var info = installed.VoiceInfo;
            var available = installed.Enabled;
            string? unavailableReason = installed.Enabled
                ? null
                : "Windows reports that this voice is disabled.";
            if (available)
            {
                available = ProbeVoice(info.Name, out unavailableReason);
            }

            names[info.Id] = info.Name;
            voices.Add(new BrokerVoice(
                info.Id,
                info.Name,
                info.Culture.Name,
                DetectProvider(info.Name, info.Description),
                RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
                available,
                unavailableReason,
                string.Equals(info.Name, defaultName, StringComparison.OrdinalIgnoreCase)));
        }

        lock (_sync)
        {
            _voiceNames = names;
        }

        return Task.FromResult<IReadOnlyList<BrokerVoice>>(voices);
    }

    public Task StartSpeakAsync(BrokerRequest request)
    {
        Stop();
        string? voiceName = null;
        lock (_sync)
        {
            if (!string.IsNullOrWhiteSpace(request.VoiceId))
            {
                _voiceNames.TryGetValue(request.VoiceId, out voiceName);
            }

            _activeRequestId = request.RequestId;
        }

        if (!string.IsNullOrWhiteSpace(voiceName))
        {
            _synthesizer.SelectVoice(voiceName);
        }

        _synthesizer.Volume = Math.Clamp(request.Volume, 0, 100);
        _synthesizer.Rate = Math.Clamp(
            (int)Math.Round((Math.Clamp(request.Rate, 0.5, 2) - 1) * 6),
            -10,
            10);
        var culture = SecurityElement.Escape(_synthesizer.Voice.Culture.Name) ?? "en-US";
        var escapedText = SecurityElement.Escape(request.Text) ?? string.Empty;
        var pitchPercent = Math.Clamp(
            (int)Math.Round((Math.Clamp(request.Pitch, 0.5, 2) - 1) * 50),
            -50,
            50);
        var pitch = pitchPercent >= 0 ? $"+{pitchPercent}%" : $"{pitchPercent}%";
        var ssml = $"<speak version='1.0' xml:lang='{culture}'><prosody pitch='{pitch}'>{escapedText}</prosody></speak>";
        _synthesizer.SpeakSsmlAsync(ssml);
        return Task.CompletedTask;
    }

    public void Pause()
    {
        if (_synthesizer.State == SynthesizerState.Speaking)
        {
            _synthesizer.Pause();
        }
    }

    public void Resume()
    {
        if (_synthesizer.State == SynthesizerState.Paused)
        {
            _synthesizer.Resume();
        }
    }

    public void Stop() => _synthesizer.SpeakAsyncCancelAll();

    public ValueTask DisposeAsync()
    {
        _synthesizer.SpeakCompleted -= Synthesizer_SpeakCompleted;
        _synthesizer.Dispose();
        return ValueTask.CompletedTask;
    }

    private void Synthesizer_SpeakCompleted(object? sender, SpeakCompletedEventArgs e)
    {
        string? requestId;
        lock (_sync)
        {
            requestId = _activeRequestId;
            _activeRequestId = null;
        }

        if (requestId is not null)
        {
            Completed?.Invoke(
                this,
                new ProviderCompletion(
                    requestId,
                    e.Cancelled,
                    e.Error is null ? null : Program.Sanitize(e.Error.Message)));
        }
    }

    private static bool ProbeVoice(string voiceName, out string? error)
    {
        try
        {
            using var probe = new System.Speech.Synthesis.SpeechSynthesizer();
            using var output = new MemoryStream();
            probe.SelectVoice(voiceName);
            probe.SetOutputToWaveStream(output);
            probe.Speak("OpenShelf voice check.");
            error = output.Length > 44
                ? null
                : "The voice produced no audio during its licence check.";
            return error is null;
        }
        catch (Exception exception)
        {
            error = Program.Sanitize(exception.Message);
            return false;
        }
    }

    private static string DetectProvider(string name, string description)
    {
        var combined = name + " " + description;
        if (combined.Contains("Microsoft", StringComparison.OrdinalIgnoreCase))
        {
            return "Microsoft";
        }

        if (combined.Contains("Acapela", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Peter", StringComparison.OrdinalIgnoreCase))
        {
            return "Acapela";
        }

        return "Windows SAPI";
    }
}

internal sealed class ModernWindowsVoiceProvider : IVoiceProvider
{
    private readonly object _sync = new();
    private readonly Windows.Media.SpeechSynthesis.SpeechSynthesizer _synthesizer = new();
    private readonly MediaPlayer _player = new();
    private SpeechSynthesisStream? _stream;
    private string? _activeRequestId;

    public ModernWindowsVoiceProvider()
    {
        _player.CommandManager.IsEnabled = false;
        _player.MediaEnded += Player_MediaEnded;
        _player.MediaFailed += Player_MediaFailed;
    }

    public event EventHandler<ProviderCompletion>? Completed;

    public Task<IReadOnlyList<BrokerVoice>> ListVoicesAsync()
    {
        var defaultId = Windows.Media.SpeechSynthesis.SpeechSynthesizer.DefaultVoice.Id;
        IReadOnlyList<BrokerVoice> voices =
            Windows.Media.SpeechSynthesis.SpeechSynthesizer.AllVoices
                .Select(voice => new BrokerVoice(
                    voice.Id,
                    voice.DisplayName,
                    voice.Language,
                    "Microsoft",
                    RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
                    true,
                    null,
                    string.Equals(voice.Id, defaultId, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
        return Task.FromResult(voices);
    }

    public async Task StartSpeakAsync(BrokerRequest request)
    {
        Stop();
        var voice = Windows.Media.SpeechSynthesis.SpeechSynthesizer.AllVoices
            .FirstOrDefault(item => string.Equals(
                item.Id,
                request.VoiceId,
                StringComparison.OrdinalIgnoreCase));
        if (voice is not null)
        {
            _synthesizer.Voice = voice;
        }

        _synthesizer.Options.SpeakingRate = Math.Clamp(request.Rate, 0.5, 2);
        _synthesizer.Options.AudioVolume = Math.Clamp(request.Volume / 100d, 0, 1);
        _synthesizer.Options.AudioPitch = Math.Clamp(request.Pitch, 0.5, 2);
        var stream = await _synthesizer
            .SynthesizeTextToStreamAsync(request.Text)
            .AsTask()
            .ConfigureAwait(false);
        lock (_sync)
        {
            _stream?.Dispose();
            _stream = stream;
            _activeRequestId = request.RequestId;
        }

        _player.Source = MediaSource.CreateFromStream(stream, stream.ContentType);
        _player.Play();
    }

    public void Pause() => _player.Pause();

    public void Resume() => _player.Play();

    public void Stop()
    {
        string? requestId;
        lock (_sync)
        {
            requestId = _activeRequestId;
            _activeRequestId = null;
        }

        _player.Pause();
        _player.Source = null;
        DisposeStream();
        if (requestId is not null)
        {
            Completed?.Invoke(this, new ProviderCompletion(requestId, true, null));
        }
    }

    public ValueTask DisposeAsync()
    {
        _player.MediaEnded -= Player_MediaEnded;
        _player.MediaFailed -= Player_MediaFailed;
        Stop();
        _player.Dispose();
        _synthesizer.Dispose();
        return ValueTask.CompletedTask;
    }

    private void Player_MediaEnded(MediaPlayer sender, object args) => Complete(null);

    private void Player_MediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args) =>
        Complete(Program.Sanitize(args.ErrorMessage));

    private void Complete(string? error)
    {
        string? requestId;
        lock (_sync)
        {
            requestId = _activeRequestId;
            _activeRequestId = null;
        }

        _player.Source = null;
        DisposeStream();
        if (requestId is not null)
        {
            Completed?.Invoke(this, new ProviderCompletion(requestId, false, error));
        }
    }

    private void DisposeStream()
    {
        SpeechSynthesisStream? stream;
        lock (_sync)
        {
            stream = _stream;
            _stream = null;
        }

        stream?.Dispose();
    }
}

internal sealed record BrokerRequest(
    string? RequestId,
    string Command,
    string? VoiceId = null,
    string? Text = null,
    double Rate = 1,
    int Volume = 100,
    double Pitch = 1);

internal sealed record BrokerVoice(
    string Id,
    string DisplayName,
    string Language,
    string Provider,
    string Architecture,
    bool IsAvailable,
    string? UnavailableReason,
    bool IsDefault);

internal sealed record BrokerResponse(
    string? RequestId,
    string Type,
    string? Message,
    IReadOnlyList<BrokerVoice>? Voices)
{
    public static BrokerResponse Error(string? requestId, string message) =>
        new(requestId, "error", Program.Sanitize(message), null);

    public static BrokerResponse Acknowledged(string? requestId) =>
        new(requestId, "acknowledged", null, null);
}

internal sealed record ProviderCompletion(
    string RequestId,
    bool Cancelled,
    string? Error);
