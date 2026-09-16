using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using OpenShelf.Core;

namespace OpenShelf.Infrastructure;

public sealed record WindowsSpeechBrokerOptions(
    string? BundleRoot = null,
    bool SearchApplicationDirectory = true,
    bool SearchArtifactDirectory = true);

public sealed class WindowsSpeechEngine : ISpeechEngine, ISpeechDiagnostics
{
    private readonly object _sync = new();
    private readonly List<ClientRegistration> _clients;
    private readonly Dictionary<string, VoiceRoute> _voiceRoutes =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ProviderProbe> _providerProbes = new();

    private ImmutableArray<SpeechVoice> _voices = ImmutableArray<SpeechVoice>.Empty;
    private SpeechState _state = SpeechState.Stopped;
    private CancellationTokenSource? _playbackCancellation;
    private Task? _playbackTask;
    private IWindowsSpeechBrokerClient? _activeClient;
    private IReadOnlyList<string> _segments = Array.Empty<string>();
    private int _currentIndex;
    private int? _requestedIndex;
    private bool _disposed;

    public WindowsSpeechEngine(WindowsSpeechBrokerOptions? options = null)
        : this(CreateDefaultClients(options ?? new WindowsSpeechBrokerOptions()))
    {
    }

    internal WindowsSpeechEngine(IEnumerable<ClientRegistration> clients)
    {
        _clients = clients.ToList();
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

    public string? LastError { get; private set; }

    public event EventHandler<SpeechProgress>? ProgressChanged;
    public event EventHandler<SpeechState>? StateChanged;

    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        RefreshVoicesAsync(cancellationToken);

    public async Task RefreshVoicesAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await StopAsync().ConfigureAwait(false);

        var discovered = new List<DiscoveredVoice>();
        var probes = new List<ProviderProbe>();
        foreach (var registration in _clients)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var voices = await registration.Client
                    .ListVoicesAsync(cancellationToken)
                    .ConfigureAwait(false);
                probes.Add(new ProviderProbe(
                    registration.Client.Name,
                    true,
                    voices.Count,
                    null));
                discovered.AddRange(
                    voices.Select(voice => new DiscoveredVoice(registration, voice)));
            }
            catch (Exception exception) when (
                exception is IOException
                    or InvalidOperationException
                    or TimeoutException
                    or FileNotFoundException
                    or System.ComponentModel.Win32Exception)
            {
                var message = SafeMessage(exception);
                probes.Add(new ProviderProbe(
                    registration.Client.Name,
                    false,
                    0,
                    message));
                LastError = message;
            }
        }

        var routes = new Dictionary<string, VoiceRoute>(StringComparer.OrdinalIgnoreCase);
        var mapped = discovered
            .Select(MapVoice)
            .OrderBy(item => VoicePreference(item.Voice))
            .ThenBy(item => item.Voice.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        var deduplicated = new List<SpeechVoice>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in mapped)
        {
            var key = $"{NormalizeForDeduplication(item.Voice.DisplayName)}|{item.Voice.Language}";
            if (!item.Voice.IsAvailable || !seen.Add(key))
            {
                continue;
            }

            deduplicated.Add(item.Voice);
            routes[item.Voice.Id] = item.Route;
        }

        lock (_sync)
        {
            _voices = deduplicated.ToImmutableArray();
            _voiceRoutes.Clear();
            foreach (var (id, route) in routes)
            {
                _voiceRoutes[id] = route;
            }

            _providerProbes.Clear();
            _providerProbes.AddRange(probes);
        }

        ChangeState(_voices.Length == 0 ? SpeechState.Unavailable : SpeechState.Stopped);
    }

    public async Task SpeakAsync(
        IReadOnlyList<string> sentenceSegments,
        SpeechOptions options,
        int startIndex,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(sentenceSegments);
        await StopAsync().ConfigureAwait(false);
        if (sentenceSegments.Count == 0)
        {
            ChangeState(SpeechState.Stopped);
            return;
        }

        var route = ResolveRoute(options.VoiceId)
            ?? throw new InvalidOperationException(
                "The selected Windows voice is no longer available.");
        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var boundedStart = Math.Clamp(startIndex, 0, sentenceSegments.Count - 1);
        Task playback;
        lock (_sync)
        {
            _segments = sentenceSegments.ToArray();
            _currentIndex = boundedStart;
            _requestedIndex = null;
            _activeClient = route.Client;
            _playbackCancellation = linked;
            playback = RunPlaybackAsync(
                route,
                options.Clamped,
                boundedStart,
                linked.Token);
            _playbackTask = playback;
        }

        try
        {
            await playback.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (
            exception is IOException
                or InvalidOperationException
                or TimeoutException)
        {
            LastError = SafeMessage(exception);
            throw;
        }
        finally
        {
            var shouldStop = false;
            lock (_sync)
            {
                if (ReferenceEquals(_playbackCancellation, linked))
                {
                    _playbackCancellation = null;
                    _playbackTask = null;
                    _activeClient = null;
                    shouldStop = _state != SpeechState.Unavailable;
                }
            }

            linked.Dispose();
            if (shouldStop)
            {
                ChangeState(SpeechState.Stopped);
            }
        }
    }

    public async Task PreviewAsync(
        SpeechOptions options,
        string sampleText,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await StopAsync().ConfigureAwait(false);
        var route = ResolveRoute(options.VoiceId)
            ?? throw new InvalidOperationException(
                "The selected Windows voice is no longer available.");
        var bounded = options.Clamped;
        ChangeState(SpeechState.Speaking);
        try
        {
            await route.Client.SpeakAsync(
                    route.BrokerVoiceId,
                    sampleText,
                    bounded.WordsPerMinute / 175d,
                    bounded.Volume,
                    bounded.Pitch,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            ChangeState(SpeechState.Stopped);
        }
    }

    public async Task PauseAsync()
    {
        IWindowsSpeechBrokerClient? client;
        lock (_sync)
        {
            if (_state != SpeechState.Speaking || _playbackTask is null)
            {
                return;
            }

            client = _activeClient;
        }

        if (client is not null)
        {
            await client.PauseAsync(CancellationToken.None).ConfigureAwait(false);
        }

        ChangeState(SpeechState.Paused);
    }

    public async Task ResumeAsync()
    {
        IWindowsSpeechBrokerClient? client;
        lock (_sync)
        {
            if (_state != SpeechState.Paused || _playbackTask is null)
            {
                return;
            }

            client = _activeClient;
        }

        if (client is not null)
        {
            await client.ResumeAsync(CancellationToken.None).ConfigureAwait(false);
        }

        ChangeState(SpeechState.Speaking);
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cancellation;
        Task? playback;
        IWindowsSpeechBrokerClient? client;
        lock (_sync)
        {
            cancellation = _playbackCancellation;
            playback = _playbackTask;
            client = _activeClient;
        }

        cancellation?.Cancel();
        if (client is not null)
        {
            try
            {
                await client.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is IOException
                    or InvalidOperationException
                    or TimeoutException)
            {
                LastError = SafeMessage(exception);
            }
        }

        if (playback is not null)
        {
            try
            {
                await playback.ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is OperationCanceledException
                    or IOException
                    or InvalidOperationException
                    or TimeoutException)
            {
                if (exception is not OperationCanceledException)
                {
                    LastError = SafeMessage(exception);
                }
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
        foreach (var client in _clients)
        {
            await client.Client.DisposeAsync().ConfigureAwait(false);
        }
    }

    public string CreateSpeechDiagnostics()
    {
        ImmutableArray<SpeechVoice> voices;
        ProviderProbe[] probes;
        lock (_sync)
        {
            voices = _voices;
            probes = _providerProbes.ToArray();
        }

        var lines = new List<string>
        {
            "Windows speech providers:",
            $"  State: {State}",
            $"  Usable voices: {voices.Length}"
        };
        foreach (var probe in probes)
        {
            lines.Add(
                probe.Available
                    ? $"  {probe.Name}: ready ({probe.VoiceCount} discovered)"
                    : $"  {probe.Name}: unavailable ({probe.Error})");
        }

        foreach (var voice in voices)
        {
            lines.Add(
                $"  Voice: {voice.DisplayName} [{voice.Language}, {voice.Architecture}]"
                + (voice.IsDefault ? " (Windows default)" : string.Empty));
        }

        lines.Add(
            voices.Any(IsPeter)
                ? "  Peter: ready; using the installed licensed SAPI voice."
                : "  Peter: not exposed through Windows SAPI. If Peter works only in Communicator 5, the licensed voice package may be private to Communicator or may need its supported SAPI installation repaired.");
        if (!string.IsNullOrWhiteSpace(LastError))
        {
            lines.Add($"  Last error: {LastError}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private async Task RunPlaybackAsync(
        VoiceRoute route,
        SpeechOptions options,
        int startIndex,
        CancellationToken cancellationToken)
    {
        var index = startIndex;
        ChangeState(SpeechState.Speaking);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? segment;
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

                segment = _segments[index];
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
            await route.Client.SpeakAsync(
                    route.BrokerVoiceId,
                    segment,
                    options.WordsPerMinute / 175d,
                    options.Volume,
                    options.Pitch,
                    cancellationToken)
                .ConfigureAwait(false);

            lock (_sync)
            {
                if (_requestedIndex is { } requested)
                {
                    index = Math.Clamp(requested, 0, _segments.Count - 1);
                    _requestedIndex = null;
                }
                else
                {
                    index++;
                }
            }
        }
    }

    private async Task RequestMoveAsync(int delta)
    {
        IWindowsSpeechBrokerClient? client;
        lock (_sync)
        {
            if (_playbackTask is null || _segments.Count == 0)
            {
                return;
            }

            _requestedIndex = Math.Clamp(
                _currentIndex + delta,
                0,
                _segments.Count - 1);
            client = _activeClient;
        }

        if (client is not null)
        {
            await client.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private VoiceRoute? ResolveRoute(string? voiceId)
    {
        lock (_sync)
        {
            if (!string.IsNullOrWhiteSpace(voiceId)
                && _voiceRoutes.TryGetValue(voiceId, out var route))
            {
                return route;
            }

            var first = _voices.FirstOrDefault();
            return first is null ? null : _voiceRoutes[first.Id];
        }
    }

    private static MappedVoice MapVoice(DiscoveredVoice discovered)
    {
        var broker = discovered.Voice;
        var providerLabel = string.IsNullOrWhiteSpace(broker.Provider)
            ? discovered.Registration.Provider == SpeechProvider.WindowsModern
                ? "Microsoft"
                : "Windows SAPI"
            : broker.Provider;
        var friendlyName = CleanVoiceName(broker.DisplayName, providerLabel);
        var displayName = $"{providerLabel} — {friendlyName}";
        var source = discovered.Registration.Provider == SpeechProvider.WindowsModern
            ? "modern"
            : discovered.Registration.Architecture == SpeechArchitecture.X86
                ? "sapi-x86"
                : "sapi-x64";
        var hash = Convert.ToHexString(
                SHA256.HashData(
                    Encoding.UTF8.GetBytes($"{source}|{broker.Id}")))
            .ToLowerInvariant();
        var id = $"windows:{source}:{hash}";
        var voice = new SpeechVoice(
            id,
            displayName,
            broker.Language,
            discovered.Registration.Provider,
            discovered.Registration.Architecture,
            broker.IsAvailable,
            broker.UnavailableReason,
            SupportsRate: true,
            SupportsVolume: true,
            SupportsPitch: true,
            broker.IsDefault);
        return new MappedVoice(
            voice,
            new VoiceRoute(discovered.Registration.Client, broker.Id));
    }

    private static string CleanVoiceName(string name, string provider)
    {
        var cleaned = name.Trim();
        if (cleaned.StartsWith(provider + " ", StringComparison.OrdinalIgnoreCase))
        {
            cleaned = cleaned[(provider.Length + 1)..];
        }

        if (cleaned.StartsWith("Microsoft ", StringComparison.OrdinalIgnoreCase))
        {
            cleaned = cleaned["Microsoft ".Length..];
        }

        cleaned = cleaned.Replace(" Desktop", string.Empty, StringComparison.OrdinalIgnoreCase);
        var languageSuffix = cleaned.IndexOf(" - English (", StringComparison.OrdinalIgnoreCase);
        if (languageSuffix >= 0)
        {
            cleaned = cleaned[..languageSuffix];
        }

        return string.IsNullOrWhiteSpace(cleaned) ? name.Trim() : cleaned.Trim();
    }

    private static int VoicePreference(SpeechVoice voice)
    {
        if (IsPeter(voice))
        {
            return 0;
        }

        if (voice.IsDefault)
        {
            return 1;
        }

        if (voice.Provider == SpeechProvider.WindowsModern)
        {
            return 2;
        }

        if (voice.Language.StartsWith("en-GB", StringComparison.OrdinalIgnoreCase))
        {
            return 3;
        }

        return voice.Architecture == SpeechArchitecture.X64 ? 4 : 5;
    }

    private static bool IsPeter(SpeechVoice voice) =>
        voice.DisplayName.Contains("Peter", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeForDeduplication(string displayName) =>
        displayName
            .Replace("Microsoft — ", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("Acapela — ", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("Windows SAPI — ", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace(" Desktop", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim();

    private static IEnumerable<ClientRegistration> CreateDefaultClients(
        WindowsSpeechBrokerOptions options)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Array.Empty<ClientRegistration>();
        }

        var clients = new List<ClientRegistration>();
        var x64Path = WindowsSpeechBrokerLocator.Find("win-x64", options);
        if (x64Path is not null)
        {
            clients.Add(new ClientRegistration(
                new WindowsSpeechBrokerClient(
                    "Modern Microsoft voices (64-bit)",
                    x64Path,
                    "modern"),
                SpeechProvider.WindowsModern,
                SpeechArchitecture.X64));
            clients.Add(new ClientRegistration(
                new WindowsSpeechBrokerClient(
                    "Windows SAPI voices (64-bit)",
                    x64Path,
                    "sapi"),
                SpeechProvider.WindowsSapi,
                SpeechArchitecture.X64));
        }

        var x86Path = WindowsSpeechBrokerLocator.Find("win-x86", options);
        if (x86Path is not null)
        {
            clients.Add(new ClientRegistration(
                new WindowsSpeechBrokerClient(
                    "Communicator-compatible SAPI voices (32-bit)",
                    x86Path,
                    "sapi"),
                SpeechProvider.WindowsSapi,
                SpeechArchitecture.X86));
        }

        return clients;
    }

    private static string SafeMessage(Exception exception) =>
        exception.Message.Replace('\r', ' ').Replace('\n', ' ').Trim();

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

    private void InvokeHandlers<T>(EventHandler<T>? handlers, T value)
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
                // A UI subscriber must not break playback.
            }
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    internal sealed record ClientRegistration(
        IWindowsSpeechBrokerClient Client,
        SpeechProvider Provider,
        SpeechArchitecture Architecture);

    private sealed record VoiceRoute(
        IWindowsSpeechBrokerClient Client,
        string BrokerVoiceId);

    private sealed record DiscoveredVoice(
        ClientRegistration Registration,
        WindowsBrokerVoice Voice);

    private sealed record MappedVoice(SpeechVoice Voice, VoiceRoute Route);

    private sealed record ProviderProbe(
        string Name,
        bool Available,
        int VoiceCount,
        string? Error);
}

internal static class WindowsSpeechBrokerLocator
{
    private const string FileName = "OpenShelf.SpeechBroker.exe";

    public static string? Find(string runtime, WindowsSpeechBrokerOptions options)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(options.BundleRoot))
        {
            candidates.Add(Path.Combine(options.BundleRoot, runtime, FileName));
        }

        if (options.SearchApplicationDirectory)
        {
            candidates.Add(Path.Combine(
                AppContext.BaseDirectory,
                "tools",
                "windows-speech",
                runtime,
                FileName));
        }

        if (options.SearchArtifactDirectory)
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            for (var depth = 0; directory is not null && depth < 8; depth++)
            {
                candidates.Add(Path.Combine(
                    directory.FullName,
                    "artifacts",
                    "speech-brokers",
                    runtime,
                    FileName));
                directory = directory.Parent;
            }
        }

        return candidates
            .Select(Path.GetFullPath)
            .FirstOrDefault(File.Exists);
    }
}
