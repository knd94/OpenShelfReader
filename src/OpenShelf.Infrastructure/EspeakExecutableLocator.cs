using System.Runtime.InteropServices;

namespace OpenShelf.Infrastructure;

public sealed record EspeakDiscoveryOptions(
    string? ExplicitExecutablePath = null,
    string? BundleRoot = null,
    bool SearchPath = true,
    bool SearchDefaultBundle = true);

public sealed record EspeakRuntime(
    string ExecutablePath,
    string? DataParentDirectory,
    bool IsBundled);

public static class EspeakExecutableLocator
{
    public static EspeakRuntime? Find(
        EspeakDiscoveryOptions? options = null,
        string? pathEnvironment = null)
    {
        options ??= new EspeakDiscoveryOptions();
        var seen = new HashSet<string>(GetPathComparer());

        if (!string.IsNullOrWhiteSpace(options.ExplicitExecutablePath)
            && TryCreateRuntime(
                options.ExplicitExecutablePath,
                isBundled: false,
                seen,
                out var explicitRuntime))
        {
            return explicitRuntime;
        }

        foreach (var candidate in GetBundledCandidates(
                     options.BundleRoot,
                     options.SearchDefaultBundle))
        {
            if (TryCreateRuntime(candidate, isBundled: true, seen, out var runtime))
            {
                return runtime;
            }
        }

        if (!options.SearchPath)
        {
            return null;
        }

        var path = pathEnvironment ?? Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        foreach (var entry in path.Split(
                     Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var directory = entry.Trim('"');
            if (!Path.IsPathRooted(directory))
            {
                continue;
            }

            foreach (var executableName in GetExecutableNames())
            {
                if (TryCreateRuntime(
                    Path.Combine(directory, executableName),
                    isBundled: false,
                    seen,
                    out var runtime))
                {
                    return runtime;
                }
            }
        }

        return null;
    }

    private static IEnumerable<string> GetBundledCandidates(
        string? configuredRoot,
        bool searchDefaultBundle)
    {
        var roots = new List<string>();
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            roots.Add(configuredRoot);
        }

        if (searchDefaultBundle)
        {
            var defaultRoot = Path.Combine(AppContext.BaseDirectory, "tools", "espeak-ng");
            roots.Add(Path.Combine(defaultRoot, RuntimeInformation.RuntimeIdentifier));
            roots.Add(defaultRoot);
        }

        foreach (var root in roots)
        {
            foreach (var executableName in GetExecutableNames())
            {
                yield return Path.Combine(root, executableName);
                yield return Path.Combine(root, "bin", executableName);
            }
        }
    }

    private static IReadOnlyList<string> GetExecutableNames() =>
        OperatingSystem.IsWindows()
            ? ["espeak-ng.exe", "espeak.exe"]
            : ["espeak-ng", "espeak"];

    private static bool TryCreateRuntime(
        string candidate,
        bool isBundled,
        ISet<string> seen,
        out EspeakRuntime? runtime)
    {
        runtime = null;
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(candidate);
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or NotSupportedException
                or PathTooLongException)
        {
            return false;
        }

        if (!seen.Add(fullPath) || !File.Exists(fullPath) || !IsExecutable(fullPath))
        {
            return false;
        }

        var executableDirectory = Path.GetDirectoryName(fullPath);
        var dataDirectory = executableDirectory is null
            ? null
            : Path.Combine(executableDirectory, "espeak-ng-data");
        runtime = new EspeakRuntime(
            fullPath,
            dataDirectory is not null && Directory.Exists(dataDirectory)
                ? executableDirectory
                : null,
            isBundled);
        return true;
    }

    private static bool IsExecutable(string filePath)
    {
        if (OperatingSystem.IsWindows())
        {
            return string.Equals(
                Path.GetExtension(filePath),
                ".exe",
                StringComparison.OrdinalIgnoreCase);
        }

        try
        {
            var mode = File.GetUnixFileMode(filePath);
            const UnixFileMode executeBits =
                UnixFileMode.UserExecute
                | UnixFileMode.GroupExecute
                | UnixFileMode.OtherExecute;
            return (mode & executeBits) != 0;
        }
        catch (PlatformNotSupportedException)
        {
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static StringComparer GetPathComparer() =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
}
