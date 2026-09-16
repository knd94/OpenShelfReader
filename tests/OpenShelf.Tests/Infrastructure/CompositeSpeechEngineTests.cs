using System.Collections.Immutable;
using OpenShelf.Core;
using OpenShelf.Infrastructure;
using Xunit;

namespace OpenShelf.Tests.Infrastructure;

public sealed class CompositeSpeechEngineTests
{
    [Fact]
    public async Task ProviderFailureFallsBackWithoutLosingTheStartSentence()
    {
        var peter = new SpeechVoice(
            "windows:sapi-x86:peter",
            "Acapela — Peter",
            "en-GB",
            SpeechProvider.WindowsSapi,
            SpeechArchitecture.X86);
        var builtIn = new SpeechVoice(
            "builtin:espeak:en-gb",
            "Built-in — English",
            "en-GB");
        var primary = new FakeSpeechEngine([peter])
        {
            SpeakFailure = new InvalidOperationException("Broker crashed.")
        };
        var fallback = new FakeSpeechEngine([builtIn]);
        await using var composite = new CompositeSpeechEngine([primary, fallback]);
        await composite.InitializeAsync(TestContext.Current.CancellationToken);

        await composite.SpeakAsync(
            ["First.", "Second.", "Third."],
            new SpeechOptions(peter.Id, 200, 80, 1.1),
            startIndex: 1,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, primary.SpeakCount);
        Assert.Equal(1, fallback.SpeakCount);
        Assert.Equal(1, fallback.LastStartIndex);
        Assert.Equal(builtIn.Id, fallback.LastOptions?.VoiceId);
        Assert.Contains("Broker crashed", composite.CreateSpeechDiagnostics());
    }

    [Fact]
    public async Task PeterSortsBeforeDefaultAndBuiltInVoices()
    {
        var windows = new FakeSpeechEngine(
            [
                new SpeechVoice(
                    "windows:modern:george",
                    "Microsoft — George",
                    "en-GB",
                    SpeechProvider.WindowsModern,
                    SpeechArchitecture.X64,
                    IsDefault: true),
                new SpeechVoice(
                    "windows:sapi-x86:peter",
                    "Acapela — Peter",
                    "en-GB",
                    SpeechProvider.WindowsSapi,
                    SpeechArchitecture.X86)
            ]);
        var builtIn = new FakeSpeechEngine(
            [new SpeechVoice(
                "builtin:espeak:en",
                "Built-in — English",
                "en")]);
        await using var composite = new CompositeSpeechEngine([windows, builtIn]);

        await composite.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Acapela — Peter", composite.Voices[0].DisplayName);
        Assert.Equal("Microsoft — George", composite.Voices[1].DisplayName);
    }

    private sealed class FakeSpeechEngine : ISpeechEngine, ISpeechDiagnostics
    {
        public FakeSpeechEngine(IEnumerable<SpeechVoice> voices)
        {
            Voices = voices.ToImmutableArray();
        }

        public SpeechState State { get; private set; } = SpeechState.Stopped;
        public ImmutableArray<SpeechVoice> Voices { get; private set; }
        public Exception? SpeakFailure { get; init; }
        public int SpeakCount { get; private set; }
        public int LastStartIndex { get; private set; }
        public SpeechOptions? LastOptions { get; private set; }

        public event EventHandler<SpeechProgress>? ProgressChanged;
        public event EventHandler<SpeechState>? StateChanged;

        public Task InitializeAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task RefreshVoicesAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task SpeakAsync(
            IReadOnlyList<string> sentenceSegments,
            SpeechOptions options,
            int startIndex,
            CancellationToken cancellationToken)
        {
            SpeakCount++;
            LastStartIndex = startIndex;
            LastOptions = options;
            if (SpeakFailure is not null)
            {
                throw SpeakFailure;
            }

            State = SpeechState.Speaking;
            StateChanged?.Invoke(this, State);
            if (sentenceSegments.Count > 0)
            {
                ProgressChanged?.Invoke(
                    this,
                    new SpeechProgress(
                        startIndex,
                        sentenceSegments.Count,
                        sentenceSegments[startIndex],
                        0,
                        sentenceSegments[startIndex].Length));
            }

            State = SpeechState.Stopped;
            StateChanged?.Invoke(this, State);
            return Task.CompletedTask;
        }

        public Task PreviewAsync(
            SpeechOptions options,
            string sampleText,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task PauseAsync() => Task.CompletedTask;
        public Task ResumeAsync() => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
        public Task PreviousAsync() => Task.CompletedTask;
        public Task NextAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public string CreateSpeechDiagnostics() => $"Fake voices: {Voices.Length}";
    }
}
