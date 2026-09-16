using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;

[assembly: AvaloniaTestApplication(typeof(OpenShelf.HeadlessTests.TestAppBuilder))]

namespace OpenShelf.HeadlessTests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder
            .Configure<OpenShelf.App.App>()
            .UseSkia()
            .UseHeadless(
                new AvaloniaHeadlessPlatformOptions
                {
                    UseHeadlessDrawing = false
                });
}
