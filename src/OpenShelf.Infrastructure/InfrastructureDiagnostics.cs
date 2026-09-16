using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.InteropServices;
using OpenShelf.Core;

namespace OpenShelf.Infrastructure;

public enum DiagnosticSeverity
{
    Information,
    Success,
    Warning,
    Error
}

public sealed record DiagnosticEntry(
    string Name,
    string Value,
    DiagnosticSeverity Severity = DiagnosticSeverity.Information);

public sealed record InfrastructureDiagnosticReport(
    DateTimeOffset GeneratedAt,
    ImmutableArray<DiagnosticEntry> Entries);

public interface IInfrastructureDiagnosticsService
{
    Task<InfrastructureDiagnosticReport> CaptureAsync(
        CancellationToken cancellationToken = default);
}

public sealed class InfrastructureDiagnosticsService : IInfrastructureDiagnosticsService
{
    private readonly IAppDataPaths _paths;
    private readonly IBookFormatRegistry _formatRegistry;
    private readonly EspeakDiscoveryOptions _speechOptions;

    public InfrastructureDiagnosticsService(
        IAppDataPaths paths,
        IBookFormatRegistry formatRegistry,
        EspeakDiscoveryOptions? speechOptions = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _formatRegistry =
            formatRegistry ?? throw new ArgumentNullException(nameof(formatRegistry));
        _speechOptions = speechOptions ?? new EspeakDiscoveryOptions();
    }

    public Task<InfrastructureDiagnosticReport> CaptureAsync(
        CancellationToken cancellationToken = default) =>
        Task.Run(
            () => Capture(cancellationToken),
            cancellationToken);

    private InfrastructureDiagnosticReport Capture(CancellationToken cancellationToken)
    {
        var entries = ImmutableArray.CreateBuilder<DiagnosticEntry>();
        cancellationToken.ThrowIfCancellationRequested();

        var assembly = Assembly.GetEntryAssembly() ?? typeof(InfrastructureDiagnosticsService).Assembly;
        entries.Add(new DiagnosticEntry(
            "Application version",
            assembly.GetName().Version?.ToString() ?? "unknown"));
        entries.Add(new DiagnosticEntry(
            "Runtime",
            RuntimeInformation.FrameworkDescription));
        entries.Add(new DiagnosticEntry(
            "Operating system",
            RuntimeInformation.OSDescription));
        entries.Add(new DiagnosticEntry(
            "Process architecture",
            RuntimeInformation.ProcessArchitecture.ToString()));
        entries.Add(new DiagnosticEntry("App-data root", _paths.RootDirectory));

        try
        {
            _paths.EnsureCreated();
            cancellationToken.ThrowIfCancellationRequested();
            ProbeWriteAccess(_paths.RootDirectory);
            entries.Add(new DiagnosticEntry(
                "App-data writable",
                "yes",
                DiagnosticSeverity.Success));
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or NotSupportedException)
        {
            entries.Add(new DiagnosticEntry(
                "App-data writable",
                $"no: {exception.Message}",
                DiagnosticSeverity.Error));
        }

        try
        {
            var databaseInfo = new FileInfo(_paths.DatabasePath);
            entries.Add(new DiagnosticEntry(
                "Library database",
                databaseInfo.Exists
                    ? $"{databaseInfo.Length} bytes"
                    : "not created",
                databaseInfo.Exists
                    ? DiagnosticSeverity.Success
                    : DiagnosticSeverity.Information));
            entries.Add(new DiagnosticEntry(
                "SQLite WAL",
                File.Exists(_paths.DatabasePath + "-wal") ? "present" : "not active"));
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or NotSupportedException)
        {
            entries.Add(new DiagnosticEntry(
                "Library database",
                exception.Message,
                DiagnosticSeverity.Warning));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var formatNames = _formatRegistry.Adapters
            .Select(adapter => adapter.Format.ToString())
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        entries.Add(new DiagnosticEntry(
            "Document formats",
            formatNames.Length == 0 ? "none" : string.Join(", ", formatNames),
            formatNames.Length == 0
                ? DiagnosticSeverity.Warning
                : DiagnosticSeverity.Success));

        var speechRuntime = EspeakExecutableLocator.Find(_speechOptions);
        entries.Add(new DiagnosticEntry(
            "Text to speech",
            speechRuntime is null
                ? "eSpeak unavailable"
                : speechRuntime.ExecutablePath,
            speechRuntime is null
                ? DiagnosticSeverity.Warning
                : DiagnosticSeverity.Success));
        if (speechRuntime is not null)
        {
            entries.Add(new DiagnosticEntry(
                "eSpeak data",
                speechRuntime.DataParentDirectory is null
                    ? "default discovery"
                    : Path.Combine(
                        speechRuntime.DataParentDirectory,
                        "espeak-ng-data"),
                speechRuntime.DataParentDirectory is null
                    ? DiagnosticSeverity.Warning
                    : DiagnosticSeverity.Success));
        }

        return new InfrastructureDiagnosticReport(
            DateTimeOffset.UtcNow,
            entries.ToImmutable());
    }

    private static void ProbeWriteAccess(string directory)
    {
        var path = Path.Combine(directory, $".write-test-{Guid.NewGuid():N}.tmp");
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1,
                FileOptions.DeleteOnClose);
            stream.WriteByte(0);
        }
        finally
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // The original write result is more useful than a cleanup failure.
            }
        }
    }
}
