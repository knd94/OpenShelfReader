using OpenShelf.Core;
using OpenShelf.Infrastructure;
using Xunit;

namespace OpenShelf.Tests.Infrastructure;

public sealed class WindowsSpeechEngineTests
{
    [Fact]
    public async Task PeterIsPreferredAndDuplicateArchitecturesAreHidden()
    {
        var modern = new FakeBrokerClient(
            "Modern",
            [Voice("modern-david", "Microsoft David", "Microsoft", "x64", isDefault: true)]);
        var sapi64 = new FakeBrokerClient(
            "SAPI x64",
            [Voice("sapi-david", "Microsoft David Desktop", "Microsoft", "x64", isDefault: true)]);
        var sapi32 = new FakeBrokerClient(
            "SAPI x86",
            [Voice("peter", "Peter", "Acapela", "x86")]);
        await using var engine = new WindowsSpeechEngine(
            [
                new WindowsSpeechEngine.ClientRegistration(
                    modern,
                    SpeechProvider.WindowsModern,
                    SpeechArchitecture.X64),
                new WindowsSpeechEngine.ClientRegistration(
                    sapi64,
                    SpeechProvider.WindowsSapi,
                    SpeechArchitecture.X64),
                new WindowsSpeechEngine.ClientRegistration(
                    sapi32,
                    SpeechProvider.WindowsSapi,
                    SpeechArchitecture.X86)
            ]);

        await engine.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Acapela — Peter", engine.Voices[0].DisplayName);
        Assert.Single(engine.Voices, voice =>
            voice.DisplayName.Contains("David", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Peter: ready", engine.CreateSpeechDiagnostics());
    }

    [Fact]
    public async Task UnlicensedPeterIsNotSelectableAndDiagnosticsExplainWhy()
    {
        var client = new FakeBrokerClient(
            "SAPI x86",
            [
                Voice("peter", "Peter", "Acapela", "x86", available: false),
                Voice("david", "Microsoft David Desktop", "Microsoft", "x86")
            ]);
        await using var engine = new WindowsSpeechEngine(
            [new WindowsSpeechEngine.ClientRegistration(
                client,
                SpeechProvider.WindowsSapi,
                SpeechArchitecture.X86)]);

        await engine.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain(
            engine.Voices,
            voice => voice.DisplayName.Contains("Peter", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            "Peter: not exposed through Windows SAPI",
            engine.CreateSpeechDiagnostics());
    }

    [Fact]
    public async Task SelectedVoiceRoutesVolumeRateAndPitchToItsBroker()
    {
        var client = new FakeBrokerClient(
            "SAPI x86",
            [Voice("peter", "Peter", "Acapela", "x86")]);
        await using var engine = new WindowsSpeechEngine(
            [new WindowsSpeechEngine.ClientRegistration(
                client,
                SpeechProvider.WindowsSapi,
                SpeechArchitecture.X86)]);
        await engine.InitializeAsync(TestContext.Current.CancellationToken);
        var selected = Assert.Single(engine.Voices);

        await engine.SpeakAsync(
            ["First sentence."],
            new SpeechOptions(selected.Id, 210, 72, 1.25),
            0,
            TestContext.Current.CancellationToken);

        var request = Assert.Single(client.Spoken);
        Assert.Equal("peter", request.VoiceId);
        Assert.Equal(210d / 175d, request.Rate, 8);
        Assert.Equal(72, request.Volume);
        Assert.Equal(1.25, request.Pitch);
    }

    [Fact]
    public async Task StableVoiceIdSurvivesRefresh()
    {
        var client = new FakeBrokerClient(
            "SAPI x64",
            [Voice("token-david", "Microsoft David Desktop", "Microsoft", "x64")]);
        await using var engine = new WindowsSpeechEngine(
            [new WindowsSpeechEngine.ClientRegistration(
                client,
                SpeechProvider.WindowsSapi,
                SpeechArchitecture.X64)]);
        await engine.InitializeAsync(TestContext.Current.CancellationToken);
        var before = Assert.Single(engine.Voices).Id;

        await engine.RefreshVoicesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(before, Assert.Single(engine.Voices).Id);
    }

    [Fact]
    public async Task LocalBrokersEnumerateMicrosoftVoicesWhenPublished()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var x64 = WindowsSpeechBrokerLocator.Find(
            "win-x64",
            new WindowsSpeechBrokerOptions());
        var x86 = WindowsSpeechBrokerLocator.Find(
            "win-x86",
            new WindowsSpeechBrokerOptions());
        if (x64 is null || x86 is null)
        {
            return;
        }

        await using var engine = new WindowsSpeechEngine();
        await engine.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.True(
            engine.Voices.Any(voice =>
                voice.DisplayName.Contains("David", StringComparison.OrdinalIgnoreCase)),
            engine.CreateSpeechDiagnostics());
        Assert.Contains(engine.Voices, voice =>
            voice.DisplayName.Contains("Hazel", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(engine.Voices, voice =>
            voice.DisplayName.Contains("Zira", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(engine.Voices, voice => !voice.IsAvailable);
    }

    private static WindowsBrokerVoice Voice(
        string id,
        string name,
        string provider,
        string architecture,
        bool available = true,
        bool isDefault = false) =>
        new(
            id,
            name,
            name.Contains("Peter", StringComparison.OrdinalIgnoreCase) ? "en-GB" : "en-US",
            provider,
            architecture,
            available,
            available ? null : "Voice licence check failed.",
            isDefault);

    private sealed class FakeBrokerClient : IWindowsSpeechBrokerClient
    {
        private readonly IReadOnlyList<WindowsBrokerVoice> _voices;

        public FakeBrokerClient(string name, IReadOnlyList<WindowsBrokerVoice> voices)
        {
            Name = name;
            _voices = voices;
        }

        public string Name { get; }
        public bool IsRunning => true;
        public string? LastError => null;
        public List<SpokenRequest> Spoken { get; } = new();

        public Task<IReadOnlyList<WindowsBrokerVoice>> ListVoicesAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(_voices);

        public Task SpeakAsync(
            string voiceId,
            string text,
            double rate,
            int volume,
            double pitch,
            CancellationToken cancellationToken)
        {
            Spoken.Add(new SpokenRequest(voiceId, text, rate, volume, pitch));
            return Task.CompletedTask;
        }

        public Task PauseAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ResumeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed record SpokenRequest(
        string VoiceId,
        string Text,
        double Rate,
        int Volume,
        double Pitch);
}
