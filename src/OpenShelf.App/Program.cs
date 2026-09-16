using System;
using System.Linq;
using Avalonia;
using OpenShelf.App.Services;
using OpenShelf.Formats;

namespace OpenShelf.App;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Any(argument =>
                string.Equals(argument, "--smoke-test", StringComparison.OrdinalIgnoreCase)))
        {
            return RunSmokeTestAsync().GetAwaiter().GetResult();
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder
            .Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
    }

    private static async System.Threading.Tasks.Task<int> RunSmokeTestAsync()
    {
        var smokeRoot = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"openshelf-smoke-{Guid.NewGuid():N}");
        AppServices? services = null;

        try
        {
            services = await AppCompositionRoot
                .CreateProductionAsync(smokeRoot)
                .ConfigureAwait(false);
            if (services.RegistryCount != 8)
            {
                throw new InvalidOperationException(
                    $"Expected 8 format adapters, found {services.RegistryCount}.");
            }

            if (OperatingSystem.IsWindows()
                && System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
                    == System.Runtime.InteropServices.Architecture.X64
                && !LibMobiDiscovery.Probe().NativeLibraryAvailable)
            {
                throw new InvalidOperationException(
                    "The staged win-x64 libmobi runtime is unavailable.");
            }

            var books = await services.Library
                .LoadLibraryAsync()
                .ConfigureAwait(false);
            if (books.Count != 0)
            {
                throw new InvalidOperationException(
                    "The isolated smoke-test database was not empty.");
            }

            _ = services.Diagnostics.CreateSnapshot();
            var voices = services.Speech.Voices;
            if (voices.Count == 0
                || voices.Any(voice =>
                    string.IsNullOrWhiteSpace(voice.Id)
                    || string.IsNullOrWhiteSpace(voice.DisplayName))
                || !voices.Any(voice =>
                    voice.DisplayName.Contains(
                        "English",
                        StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException(
                    "The bundled eSpeak runtime did not discover a usable built-in voice.");
            }

            Console.Out.WriteLine("OpenShelf smoke test: OK");
            return 0;
        }
        catch (Exception exception)
        {
            var message = exception.Message
                .Replace('\r', ' ')
                .Replace('\n', ' ');
            Console.Error.WriteLine(
                $"OpenShelf smoke test: FAIL - {exception.GetType().Name}: {message}");
            return 1;
        }
        finally
        {
            if (services is not null)
            {
                await services.DisposeAsync().ConfigureAwait(false);
            }

            try
            {
                var fullSmokeRoot = System.IO.Path.GetFullPath(smokeRoot);
                var fullTempRoot = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath());
                if (fullSmokeRoot.StartsWith(
                        fullTempRoot,
                        StringComparison.OrdinalIgnoreCase)
                    && System.IO.Path.GetFileName(fullSmokeRoot)
                        .StartsWith("openshelf-smoke-", StringComparison.Ordinal)
                    && System.IO.Directory.Exists(fullSmokeRoot))
                {
                    System.IO.Directory.Delete(fullSmokeRoot, recursive: true);
                }
            }
            catch
            {
                // Smoke-test cleanup must not hide its initialization result.
            }
        }
    }
}
