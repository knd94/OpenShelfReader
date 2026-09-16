using System.Collections.Immutable;
using OpenShelf.Core;

namespace OpenShelf.Infrastructure;

public sealed class CompositeSpeechEngine : ISpeechEngine, ISpeechDiagnostics
{
    private readonly object _sync = new();
    private readonly IReadOnlyList<ISpeechEngine> _engines;
    private ImmutableArray<SpeechVoice> _voices = ImmutableArray<SpeechVoice>.Empty;
    private ISpeechEngine? _activeEngine;
    private SpeechState _state = SpeechState.Stopped;
    private bool _disposed;

    public CompositeSpeechEngine(IEnumerable<ISpeechEngine> engines)
    {
        _engines = engines.ToArray();
        if (_engines.Count == 0)
        {
            throw new ArgumentException(
                "At least one speech engine is required.",
                nameof(engines));
        }

        foreach (var engine in _engines)
        {
            engine.ProgressChanged += Engine_ProgressChanged;
            engine.StateChanged += Engine_StateChanged;
        }
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

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        foreach (var engine in _engines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await engine.InitializeAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is IOException
                    or InvalidOperationException
                    or TimeoutException
                    or System.ComponentModel.Win32Exception)
            {
                LastError = SafeMessage(exception);
            }
        }

        RebuildVoices();
    }

    public async Task RefreshVoicesAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await StopAsync().ConfigureAwait(false);
        foreach (var engine in _engines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await engine.RefreshVoicesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is IOException
                    or InvalidOperationException
                    or TimeoutException
                    or System.ComponentModel.Win32Exception)
            {
                LastError = SafeMessage(exception);
            }
        }

        RebuildVoices();
    }

    public async Task SpeakAsync(
        IReadOnlyList<string> sentenceSegments,
        SpeechOptions options,
        int startIndex,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var selected = ResolveEngine(options.VoiceId);
        if (selected is null)
        {
            ChangeState(SpeechState.Unavailable);
            return;
        }

        lock (_sync)
        {
            _activeEngine = selected;
        }

        try
        {
            await selected
                .SpeakAsync(sentenceSegments, options, startIndex, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException
                or InvalidOperationException
                or TimeoutException
                or System.ComponentModel.Win32Exception)
        {
            LastError = SafeMessage(exception);
            var fallback = _engines.FirstOrDefault(
                engine => !ReferenceEquals(engine, selected) && engine.Voices.Length > 0);
            if (fallback is null)
            {
                ChangeState(SpeechState.Unavailable);
                return;
            }

            var fallbackVoice = fallback.Voices.First();
            lock (_sync)
            {
                _activeEngine = fallback;
            }

            await fallback
                .SpeakAsync(
                    sentenceSegments,
                    options with { VoiceId = fallbackVoice.Id },
                    startIndex,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            lock (_sync)
            {
                _activeEngine = null;
            }

            if (Voices.Length > 0)
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
        var selected = ResolveEngine(options.VoiceId)
            ?? throw new InvalidOperationException("No speech voice is available.");
        lock (_sync)
        {
            _activeEngine = selected;
        }

        try
        {
            await selected.PreviewAsync(options, sampleText, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            lock (_sync)
            {
                _activeEngine = null;
            }
        }
    }

    public Task PauseAsync() => ActiveEngineOrDefault()?.PauseAsync() ?? Task.CompletedTask;

    public Task ResumeAsync() => ActiveEngineOrDefault()?.ResumeAsync() ?? Task.CompletedTask;

    public async Task StopAsync()
    {
        foreach (var engine in _engines)
        {
            try
            {
                await engine.StopAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is IOException
                    or InvalidOperationException
                    or TimeoutException)
            {
                LastError = SafeMessage(exception);
            }
        }

        if (Voices.Length > 0)
        {
            ChangeState(SpeechState.Stopped);
        }
    }

    public Task PreviousAsync() =>
        ActiveEngineOrDefault()?.PreviousAsync() ?? Task.CompletedTask;

    public Task NextAsync() =>
        ActiveEngineOrDefault()?.NextAsync() ?? Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var engine in _engines)
        {
            engine.ProgressChanged -= Engine_ProgressChanged;
            engine.StateChanged -= Engine_StateChanged;
            await engine.DisposeAsync().ConfigureAwait(false);
        }
    }

    public string CreateSpeechDiagnostics()
    {
        var lines = new List<string>
        {
            "Speech:",
            $"  Combined state: {State}",
            $"  Selectable voices: {Voices.Length}"
        };
        foreach (var diagnostics in _engines.OfType<ISpeechDiagnostics>())
        {
            lines.Add(diagnostics.CreateSpeechDiagnostics());
        }

        if (!string.IsNullOrWhiteSpace(LastError))
        {
            lines.Add($"  Last fallback reason: {LastError}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private void RebuildVoices()
    {
        var voices = _engines
            .SelectMany(engine => engine.Voices)
            .Where(voice => voice.IsAvailable)
            .OrderBy(VoicePreference)
            .ThenBy(voice => voice.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToImmutableArray();
        lock (_sync)
        {
            _voices = voices;
        }

        ChangeState(voices.Length == 0 ? SpeechState.Unavailable : SpeechState.Stopped);
    }

    private ISpeechEngine? ResolveEngine(string? voiceId)
    {
        if (!string.IsNullOrWhiteSpace(voiceId))
        {
            var selected = _engines.FirstOrDefault(
                engine => engine.Voices.Any(
                    voice => string.Equals(
                        voice.Id,
                        voiceId,
                        StringComparison.OrdinalIgnoreCase)));
            if (selected is not null)
            {
                return selected;
            }
        }

        return _engines.FirstOrDefault(engine => engine.Voices.Length > 0);
    }

    private ISpeechEngine? ActiveEngineOrDefault()
    {
        lock (_sync)
        {
            return _activeEngine
                ?? _engines.FirstOrDefault(engine => engine.Voices.Length > 0);
        }
    }

    private static int VoicePreference(SpeechVoice voice)
    {
        if (voice.DisplayName.Contains("Peter", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (voice.IsDefault)
        {
            return 1;
        }

        return voice.Provider switch
        {
            SpeechProvider.WindowsModern => 2,
            SpeechProvider.WindowsSapi => 3,
            _ => 4
        };
    }

    private void Engine_ProgressChanged(object? sender, SpeechProgress progress)
    {
        lock (_sync)
        {
            if (_activeEngine is not null && !ReferenceEquals(sender, _activeEngine))
            {
                return;
            }
        }

        ProgressChanged?.Invoke(this, progress);
    }

    private void Engine_StateChanged(object? sender, SpeechState state)
    {
        lock (_sync)
        {
            if (_activeEngine is not null && !ReferenceEquals(sender, _activeEngine))
            {
                return;
            }
        }

        ChangeState(state);
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

        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<SpeechState> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, state);
            }
            catch
            {
                // A UI subscriber must not break provider routing.
            }
        }
    }

    private static string SafeMessage(Exception exception) =>
        exception.Message.Replace('\r', ' ').Replace('\n', ' ').Trim();

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
