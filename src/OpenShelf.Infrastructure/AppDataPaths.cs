using System.Runtime.InteropServices;

namespace OpenShelf.Infrastructure;

public interface IAppDataPaths
{
    string RootDirectory { get; }
    string LibraryDirectory { get; }
    string DatabasePath { get; }
    string LogsDirectory { get; }
    string SpeechDirectory { get; }

    void EnsureCreated();
}

public sealed class PlatformAppDataPaths : IAppDataPaths
{
    private const string DefaultApplicationName = "OpenShelf Reader";

    public PlatformAppDataPaths(string? rootDirectory = null, string applicationName = DefaultApplicationName)
    {
        if (string.IsNullOrWhiteSpace(applicationName))
        {
            throw new ArgumentException("The application name cannot be empty.", nameof(applicationName));
        }

        RootDirectory = Path.GetFullPath(
            string.IsNullOrWhiteSpace(rootDirectory)
                ? ResolvePlatformRoot(applicationName)
                : rootDirectory);
        LibraryDirectory = Path.Combine(RootDirectory, "library");
        DatabasePath = Path.Combine(RootDirectory, "openshelf.db");
        LogsDirectory = Path.Combine(RootDirectory, "logs");
        SpeechDirectory = Path.Combine(RootDirectory, "speech");
    }

    public string RootDirectory { get; }
    public string LibraryDirectory { get; }
    public string DatabasePath { get; }
    public string LogsDirectory { get; }
    public string SpeechDirectory { get; }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(LibraryDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(SpeechDirectory);
    }

    private static string ResolvePlatformRoot(string applicationName)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            var xdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            if (!string.IsNullOrWhiteSpace(xdgDataHome))
            {
                return Path.Combine(xdgDataHome, applicationName);
            }

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(home))
            {
                return Path.Combine(home, ".local", "share", applicationName);
            }
        }

        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localData))
        {
            localData = AppContext.BaseDirectory;
        }

        return Path.Combine(localData, applicationName);
    }
}
