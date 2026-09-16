using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using OpenShelf.Core;

namespace OpenShelf.Formats;

public sealed record LibMobiAvailability(
    bool NativeLibraryAvailable,
    string? NativeLibraryPath,
    string? ToolPath,
    string Diagnostic)
{
    public bool IsAvailable => NativeLibraryAvailable || ToolPath is not null;
}

public static class LibMobiDiscovery
{
    private static readonly string[] NativeNames =
    [
        "libmobi.dll",
        "mobi.dll",
        "libmobi.so.0",
        "libmobi.so",
        "libmobi.dylib"
    ];

    public static LibMobiAvailability Probe()
    {
        var nativePath = FindNativeLibrary();
        var toolPath = FindTool();
        var diagnostic = nativePath is not null && toolPath is not null
            ? "libmobi native library and mobitool are available."
            : nativePath is not null
                ? "libmobi native library is available; mobitool is not installed."
                : toolPath is not null
                    ? "mobitool is available; the libmobi shared library was not found."
                    : "libmobi is unavailable. Set OPENSHELF_LIBMOBI_PATH or " +
                      "OPENSHELF_MOBITOOL_PATH, or install libmobi on PATH.";
        return new LibMobiAvailability(
            nativePath is not null,
            nativePath,
            toolPath,
            diagnostic);
    }

    internal static string? FindNativeLibrary()
    {
        var configured = Environment.GetEnvironmentVariable("OPENSHELF_LIBMOBI_PATH");
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(configured))
        {
            candidates.Add(configured);
        }

        var baseDirectory = AppContext.BaseDirectory;
        var runtimeIdentifier = RuntimeInformation.RuntimeIdentifier;
        candidates.AddRange(NativeNames.Select(name =>
            Path.Combine(baseDirectory, "runtimes", runtimeIdentifier, "native", name)));
        candidates.AddRange(NativeNames.Select(name => Path.Combine(baseDirectory, name)));
        candidates.AddRange(NativeNames);

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!NativeLibrary.TryLoad(candidate, out var handle))
            {
                continue;
            }

            try
            {
                if (NativeLibrary.TryGetExport(handle, "mobi_init", out _) &&
                    NativeLibrary.TryGetExport(handle, "mobi_load_filename", out _) &&
                    NativeLibrary.TryGetExport(handle, "mobi_get_rawml", out _) &&
                    NativeLibrary.TryGetExport(handle, "mobi_free", out _))
                {
                    return candidate;
                }
            }
            finally
            {
                NativeLibrary.Free(handle);
            }
        }

        return null;
    }

    internal static string? FindTool()
    {
        var configured = Environment.GetEnvironmentVariable("OPENSHELF_MOBITOOL_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return Path.GetFullPath(configured);
        }

        var executableName = OperatingSystem.IsWindows() ? "mobitool.exe" : "mobitool";
        var bundled = Path.Combine(AppContext.BaseDirectory, "tools", executableName);
        if (File.Exists(bundled))
        {
            return bundled;
        }

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim('"'), executableName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
            }
        }

        return null;
    }
}

public sealed class MobiFormatAdapter : MobiFormatAdapterBase
{
    public MobiFormatAdapter()
        : base(BookFormat.Mobi, ".mobi")
    {
    }
}

public sealed class Azw3FormatAdapter : MobiFormatAdapterBase
{
    public Azw3FormatAdapter()
        : base(BookFormat.Azw3, ".azw3")
    {
    }
}

public abstract class MobiFormatAdapterBase : IBookFormatAdapter
{
    private readonly IReadOnlySet<string> extensions;

    protected MobiFormatAdapterBase(BookFormat format, string extension)
    {
        Format = format;
        extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { extension };
    }

    public BookFormat Format { get; }

    public IReadOnlySet<string> Extensions => extensions;

    public bool MatchesSignature(ReadOnlySpan<byte> header) =>
        header.Length >= 68 &&
        header.Slice(60, 8).SequenceEqual("BOOKMOBI"u8);

    public async Task<ImportedBook> ImportAsync(
        BookImportRequest request,
        IProgress<BookImportProgress>? progress,
        CancellationToken cancellationToken)
    {
        FormatUtilities.ValidateInput(request);
        progress?.Report(new BookImportProgress("Reading Kindle book", request.DisplayName, 0, null, 0));
        var data = await FormatUtilities.ReadAllBytesLimitedAsync(
            request.FilePath,
            request.Limits.MaximumInputBytes,
            cancellationToken).ConfigureAwait(false);
        MobiManagedBook managed;
        try
        {
            managed = MobiManagedParser.Parse(data, request.Limits);
        }
        catch (FormatImportException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw FormatImportException.Corrupt("The MOBI container is malformed.", exception);
        }

        if (managed.EncryptionType != 0)
        {
            throw FormatImportException.DrmProtected(
                "This Kindle book is encrypted. OpenShelf supports only DRM-free MOBI/AZW3 files.");
        }

        var revisionHash = await FormatUtilities.ComputeRevisionHashAsync(
            request.FilePath,
            cancellationToken).ConfigureAwait(false);
        var warnings = new List<string>();
        var availability = LibMobiDiscovery.Probe();
        MobiToolResult? toolResult = null;
        if (availability.ToolPath is not null)
        {
            try
            {
                toolResult = await MobiToolExtractor.ExtractAsync(
                    availability.ToolPath,
                    request.FilePath,
                    request.Limits,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                warnings.Add($"mobitool extraction failed; using a fallback parser: {exception.Message}");
            }
        }

        var sections = new List<DocumentSection>();
        var resources = new Dictionary<string, BookResource>(StringComparer.Ordinal);
        if (toolResult is not null && toolResult.Markup.Count > 0)
        {
            foreach (var resource in toolResult.Resources)
            {
                resources[resource.Path] = resource.Resource;
            }

            for (var index = 0; index < toolResult.Markup.Count; index++)
            {
                var part = toolResult.Markup[index];
                var normalized = MarkupNormalizer.Normalize(
                    part.Content,
                    part.Path,
                    (reference, alternativeText) =>
                        ResolveToolImage(
                            part.Path,
                            reference,
                            alternativeText,
                            toolResult.Resources));
                if (normalized.Blocks.Length == 0)
                {
                    continue;
                }

                sections.Add(new DocumentSection(
                    FormatUtilities.StableId("section", revisionHash, part.Path),
                    normalized.FirstHeading ?? $"Section {index + 1}",
                    normalized.Blocks,
                    0,
                    0));
            }
        }

        if (sections.Count == 0)
        {
            byte[]? rawMarkup = null;
            if (availability.NativeLibraryPath is not null)
            {
                try
                {
                    rawMarkup = LibMobiNativeBridge.ExtractRawMarkup(
                        availability.NativeLibraryPath,
                        request.FilePath,
                        request.Limits.MaximumExpandedBytes);
                }
                catch (FormatImportException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    warnings.Add($"libmobi extraction failed: {exception.Message}");
                }
            }

            rawMarkup ??= managed.RawMarkup;
            if (rawMarkup is null || rawMarkup.Length == 0)
            {
                throw FormatImportException.Unsupported(
                    "This book uses Huff/CDIC or KF8 structures that require libmobi. " +
                    availability.Diagnostic);
            }

            foreach (var image in managed.Images)
            {
                resources[image.LogicalName] = image.Resource;
            }

            var markupText = managed.TextEncoding.GetString(rawMarkup);
            var normalized = MarkupNormalizer.Normalize(
                markupText,
                request.FilePath,
                (reference, alternativeText) =>
                    ResolveManagedImage(reference, alternativeText, managed.Images));
            if (normalized.Blocks.Length == 0)
            {
                throw FormatImportException.Corrupt(
                    "No readable text could be reconstructed from the Kindle book.");
            }

            sections.Add(new DocumentSection(
                FormatUtilities.StableId("section", revisionHash, "rawml"),
                normalized.FirstHeading,
                normalized.Blocks,
                0,
                0));
            warnings.Add(availability.IsAvailable
                ? "The book was opened through libmobi's raw-text compatibility path."
                : "The book was opened with the managed PalmDOC compatibility parser.");
        }

        var title = string.IsNullOrWhiteSpace(managed.Title)
            ? FormatUtilities.FallbackTitle(request)
            : managed.Title;
        var author = string.IsNullOrWhiteSpace(managed.Author)
            ? "Unknown author"
            : managed.Author;
        var metadata = new BookMetadata(
            title,
            author,
            managed.Description,
            managed.Language,
            managed.Publisher,
            managed.Identifier);
        var toc = sections.Select((section, index) => new TableOfContentsItem(
            FormatUtilities.StableId("toc", revisionHash, index.ToString(CultureInfo.InvariantCulture)),
            section.Title ?? $"Section {index + 1}",
            section.Id,
            section.Blocks.FirstOrDefault()?.Id,
            [])).ToImmutableArray();
        var document = FormatUtilities.CreateReflowableDocument(
            metadata,
            revisionHash,
            sections,
            toc,
            resources.Values.DistinctBy(resource => resource.Id));
        var cover = managed.Cover ??
            toolResult?.Resources.FirstOrDefault()?.Resource ??
            resources.Values.FirstOrDefault();
        progress?.Report(new BookImportProgress("Complete", request.DisplayName, 1, 1, 1));
        return new ImportedBook(
            Format,
            metadata,
            document,
            cover?.Data,
            cover?.MediaType,
            warnings.ToImmutableArray());
    }

    private static ResolvedImage? ResolveManagedImage(
        string reference,
        string? alternativeText,
        IReadOnlyList<MobiManagedImage> images)
    {
        var digits = new string(reference.Where(char.IsDigit).ToArray());
        if (!int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
        {
            return null;
        }

        // MOBI recindex is normally one-based. Reconstructed libmobi resource
        // names can be zero-based UIDs, so check both representations.
        var image = images.FirstOrDefault(item =>
            item.Ordinal == index || item.Ordinal + 1 == index);
        return image is null
            ? null
            : new ResolvedImage(image.Resource.Id, alternativeText, image.Width, image.Height);
    }

    private static ResolvedImage? ResolveToolImage(
        string markupPath,
        string reference,
        string? alternativeText,
        IReadOnlyList<MobiToolResource> resources)
    {
        if (!ArchivePath.TryResolve(markupPath, reference, out var resolved))
        {
            return null;
        }

        var resource = resources.FirstOrDefault(item =>
            item.Path.Equals(resolved, StringComparison.OrdinalIgnoreCase));
        return resource is null
            ? null
            : new ResolvedImage(
                resource.Resource.Id,
                alternativeText,
                resource.Width,
                resource.Height);
    }
}

internal static class LibMobiNativeBridge
{
    private const int Success = 0;
    private const int FileEncrypted = 5;

    public static byte[] ExtractRawMarkup(
        string libraryPath,
        string filePath,
        long maximumBytes)
    {
        if (!NativeLibrary.TryLoad(libraryPath, out var library))
        {
            throw new DllNotFoundException($"Could not load libmobi from '{libraryPath}'.");
        }

        IntPtr mobi = IntPtr.Zero;
        IntPtr nativePath = IntPtr.Zero;
        IntPtr buffer = IntPtr.Zero;
        try
        {
            var init = GetDelegate<MobiInit>(library, "mobi_init");
            var free = GetDelegate<MobiFree>(library, "mobi_free");
            var load = GetDelegate<MobiLoadFilename>(library, "mobi_load_filename");
            var maximumSize = GetDelegate<MobiGetTextMaximumSize>(
                library,
                "mobi_get_text_maxsize");
            var getRawMarkup = GetDelegate<MobiGetRawMarkup>(library, "mobi_get_rawml");
            mobi = init();
            if (mobi == IntPtr.Zero)
            {
                throw new InvalidOperationException("libmobi could not initialize a document.");
            }

            nativePath = Marshal.StringToCoTaskMemUTF8(filePath);
            var status = load(mobi, nativePath);
            if (status == FileEncrypted)
            {
                throw FormatImportException.DrmProtected();
            }

            if (status != Success)
            {
                throw new InvalidDataException($"libmobi load failed with status {status}.");
            }

            var size = maximumSize(mobi);
            if (size == 0 || size > (nuint)Math.Min(maximumBytes, int.MaxValue))
            {
                throw FormatImportException.Unsupported(
                    "The decompressed MOBI text exceeds the configured limit.");
            }

            buffer = Marshal.AllocHGlobal(checked((int)size + 1));
            var outputLength = size;
            status = getRawMarkup(mobi, buffer, ref outputLength);
            if (status != Success || outputLength > size)
            {
                throw new InvalidDataException(
                    $"libmobi raw-text extraction failed with status {status}.");
            }

            var result = GC.AllocateUninitializedArray<byte>(checked((int)outputLength));
            Marshal.Copy(buffer, result, 0, result.Length);
            free(mobi);
            mobi = IntPtr.Zero;
            return result;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(buffer);
            }

            if (nativePath != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(nativePath);
            }

            if (mobi != IntPtr.Zero)
            {
                try
                {
                    GetDelegate<MobiFree>(library, "mobi_free")(mobi);
                }
                catch
                {
                }
            }

            NativeLibrary.Free(library);
        }
    }

    private static T GetDelegate<T>(IntPtr library, string name) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr MobiInit();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void MobiFree(IntPtr mobi);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int MobiLoadFilename(IntPtr mobi, IntPtr path);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nuint MobiGetTextMaximumSize(IntPtr mobi);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int MobiGetRawMarkup(IntPtr mobi, IntPtr output, ref nuint length);
}

internal static class MobiToolExtractor
{
    public static async Task<MobiToolResult> ExtractAsync(
        string toolPath,
        string sourcePath,
        ImportSecurityLimits limits,
        CancellationToken cancellationToken)
    {
        var temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            $"openshelf-mobi-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = toolPath,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            process.StartInfo.ArgumentList.Add("-s");
            process.StartInfo.ArgumentList.Add("-o");
            process.StartInfo.ArgumentList.Add(temporaryDirectory);
            process.StartInfo.ArgumentList.Add(sourcePath);
            if (!process.Start())
            {
                throw new InvalidOperationException("mobitool could not be started.");
            }

            var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException("mobitool exceeded the 45-second import limit.");
            }

            var output = await standardOutput.ConfigureAwait(false);
            var error = await standardError.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                var detail = FormatUtilities.CleanText(error.Length > 0 ? error : output);
                if (detail.Contains("encrypted", StringComparison.OrdinalIgnoreCase))
                {
                    throw FormatImportException.DrmProtected();
                }

                throw new InvalidDataException(
                    $"mobitool exited with code {process.ExitCode}: {detail}");
            }

            return await ReadOutputAsync(
                temporaryDirectory,
                limits,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                if (Directory.Exists(temporaryDirectory))
                {
                    Directory.Delete(temporaryDirectory, recursive: true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static async Task<MobiToolResult> ReadOutputAsync(
        string root,
        ImportSecurityLimits limits,
        CancellationToken cancellationToken)
    {
        var markup = new List<MobiToolMarkup>();
        var resources = new List<MobiToolResource>();
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Take(limits.MaximumArchiveEntries + 1)
            .ToArray();
        if (files.Length > limits.MaximumArchiveEntries)
        {
            throw FormatImportException.Unsupported(
                "mobitool produced too many extracted files.");
        }

        long total = 0;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(file);
            total = checked(total + info.Length);
            if (total > limits.MaximumExpandedBytes)
            {
                throw FormatImportException.Unsupported(
                    "mobitool output exceeds the expanded-size limit.");
            }

            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (Path.GetExtension(file).Equals(".html", StringComparison.OrdinalIgnoreCase) ||
                Path.GetExtension(file).Equals(".xhtml", StringComparison.OrdinalIgnoreCase) ||
                Path.GetExtension(file).Equals(".htm", StringComparison.OrdinalIgnoreCase))
            {
                var bytes = await FormatUtilities.ReadAllBytesLimitedAsync(
                    file,
                    Math.Min(limits.MaximumExpandedBytes, int.MaxValue),
                    cancellationToken).ConfigureAwait(false);
                markup.Add(new MobiToolMarkup(relative, Encoding.UTF8.GetString(bytes)));
                continue;
            }

            if (info.Length > limits.MaximumResourceBytes)
            {
                continue;
            }

            var data = await FormatUtilities.ReadAllBytesLimitedAsync(
                file,
                limits.MaximumResourceBytes,
                cancellationToken).ConfigureAwait(false);
            var mediaType = FormatUtilities.DetectMediaType(data, file);
            if (mediaType is null)
            {
                continue;
            }

            FormatUtilities.ValidateImage(data, mediaType, limits);
            ImageDimensions.TryRead(data, mediaType, out var width, out var height);
            resources.Add(new MobiToolResource(
                relative,
                width > 0 ? width : null,
                height > 0 ? height : null,
                new BookResource(
                    FormatUtilities.StableId("resource", relative),
                    mediaType,
                    data,
                    relative)));
        }

        return new MobiToolResult(markup, resources);
    }
}

internal sealed record MobiToolResult(
    IReadOnlyList<MobiToolMarkup> Markup,
    IReadOnlyList<MobiToolResource> Resources);

internal sealed record MobiToolMarkup(string Path, string Content);

internal sealed record MobiToolResource(
    string Path,
    double? Width,
    double? Height,
    BookResource Resource);

internal sealed record MobiManagedBook(
    string? Title,
    string? Author,
    string? Description,
    string? Language,
    string? Publisher,
    string? Identifier,
    int EncryptionType,
    Encoding TextEncoding,
    byte[]? RawMarkup,
    IReadOnlyList<MobiManagedImage> Images,
    BookResource? Cover);

internal sealed record MobiManagedImage(
    int Ordinal,
    string LogicalName,
    double? Width,
    double? Height,
    BookResource Resource);

internal static class MobiManagedParser
{
    public static MobiManagedBook Parse(
        byte[] data,
        ImportSecurityLimits limits)
    {
        if (data.Length < 86 ||
            !data.AsSpan(60, 8).SequenceEqual("BOOKMOBI"u8))
        {
            throw FormatImportException.Corrupt("The file has no MOBI/Palm database signature.");
        }

        var recordCount = ReadUInt16(data, 76);
        if (recordCount == 0 ||
            recordCount > limits.MaximumArchiveEntries ||
            78L + recordCount * 8L > data.Length)
        {
            throw FormatImportException.Corrupt("The MOBI record table is invalid.");
        }

        var offsets = new int[recordCount + 1];
        for (var index = 0; index < recordCount; index++)
        {
            var offset = ReadUInt32(data, 78 + index * 8);
            if (offset > int.MaxValue ||
                offset >= data.Length ||
                (index > 0 && offset < offsets[index - 1]))
            {
                throw FormatImportException.Corrupt("The MOBI record offsets are invalid.");
            }

            offsets[index] = (int)offset;
        }

        offsets[^1] = data.Length;
        var recordZero = offsets[0];
        if (recordZero + 132 > offsets[1] ||
            !data.AsSpan(recordZero + 16, 4).SequenceEqual("MOBI"u8))
        {
            throw FormatImportException.Corrupt("The MOBI header is missing or truncated.");
        }

        var compression = ReadUInt16(data, recordZero);
        var textLength = ReadUInt32(data, recordZero + 4);
        var textRecordCount = ReadUInt16(data, recordZero + 8);
        var encryption = ReadUInt16(data, recordZero + 12);
        var headerLength = ReadUInt32(data, recordZero + 20);
        var textEncoding = ReadUInt32(data, recordZero + 44) == 65001
            ? Encoding.UTF8
            : GetWindows1252();
        var fullNameOffset = ReadUInt32(data, recordZero + 100);
        var fullNameLength = ReadUInt32(data, recordZero + 104);
        var imageIndex = ReadUInt32(data, recordZero + 124);
        var exthFlags = ReadUInt32(data, recordZero + 144);
        var metadata = new Dictionary<uint, string>();
        uint? coverOffset = null;

        if ((exthFlags & 0x40) != 0)
        {
            var exthStartLong = recordZero + 16L + headerLength;
            if (exthStartLong + 12 <= offsets[1] &&
                exthStartLong <= int.MaxValue)
            {
                var exthStart = (int)exthStartLong;
                if (data.AsSpan(exthStart, 4).SequenceEqual("EXTH"u8))
                {
                    var exthLength = ReadUInt32(data, exthStart + 4);
                    var exthCount = ReadUInt32(data, exthStart + 8);
                    var position = exthStart + 12;
                    var end = Math.Min(
                        offsets[1],
                        checked(exthStart + (int)Math.Min(exthLength, int.MaxValue)));
                    for (var index = 0u;
                         index < exthCount && position + 8 <= end;
                         index++)
                    {
                        var type = ReadUInt32(data, position);
                        var length = ReadUInt32(data, position + 4);
                        if (length < 8 || length > int.MaxValue || position + length > end)
                        {
                            break;
                        }

                        var value = data.AsSpan(position + 8, (int)length - 8);
                        if (type == 201 && value.Length >= 4)
                        {
                            coverOffset = ReadUInt32(value, 0);
                        }
                        else if (type is 99 or 100 or 101 or 103 or 104 or 113 or 503 or 524)
                        {
                            metadata[type] = FormatUtilities.CleanText(textEncoding.GetString(value));
                        }

                        position += (int)length;
                    }
                }
            }
        }

        string? title = FirstNonEmpty(metadata, 503, 99);
        if (string.IsNullOrWhiteSpace(title) &&
            fullNameLength > 0 &&
            fullNameLength <= int.MaxValue &&
            recordZero + fullNameOffset + fullNameLength <= offsets[1])
        {
            title = FormatUtilities.CleanText(textEncoding.GetString(
                data,
                checked(recordZero + (int)fullNameOffset),
                (int)fullNameLength));
        }

        byte[]? rawMarkup = null;
        if (encryption == 0 && textRecordCount > 0 && textRecordCount < recordCount)
        {
            rawMarkup = compression switch
            {
                1 => ConcatenateTextRecords(data, offsets, textRecordCount, textLength),
                2 => DecompressPalmDoc(data, offsets, textRecordCount, textLength, limits),
                _ => null
            };
        }

        var images = new List<MobiManagedImage>();
        if (imageIndex < recordCount)
        {
            for (var record = (int)imageIndex; record < recordCount; record++)
            {
                var length = offsets[record + 1] - offsets[record];
                if (length <= 0 || length > limits.MaximumResourceBytes)
                {
                    continue;
                }

                var span = data.AsSpan(offsets[record], length);
                var mediaType = FormatUtilities.DetectMediaType(span);
                if (mediaType is null)
                {
                    continue;
                }

                var bytes = span.ToArray();
                FormatUtilities.ValidateImage(bytes, mediaType, limits);
                ImageDimensions.TryRead(bytes, mediaType, out var width, out var height);
                var ordinal = record - (int)imageIndex;
                var logicalName = $"resource{ordinal:D5}";
                images.Add(new MobiManagedImage(
                    ordinal,
                    logicalName,
                    width > 0 ? width : null,
                    height > 0 ? height : null,
                    new BookResource(
                        FormatUtilities.StableId("resource", logicalName),
                        mediaType,
                        bytes,
                        logicalName)));
            }
        }

        var cover = coverOffset is not null
            ? images.FirstOrDefault(image => image.Ordinal == coverOffset.Value)?.Resource
            : null;
        return new MobiManagedBook(
            title,
            FirstNonEmpty(metadata, 100),
            FirstNonEmpty(metadata, 103),
            FirstNonEmpty(metadata, 524),
            FirstNonEmpty(metadata, 101),
            FirstNonEmpty(metadata, 104, 113),
            encryption,
            textEncoding,
            rawMarkup,
            images,
            cover);
    }

    private static byte[] ConcatenateTextRecords(
        byte[] data,
        int[] offsets,
        int count,
        uint expectedLength)
    {
        using var output = new MemoryStream();
        for (var index = 1; index <= count && index + 1 < offsets.Length; index++)
        {
            output.Write(data, offsets[index], offsets[index + 1] - offsets[index]);
        }

        var result = output.ToArray();
        return result.Length > expectedLength
            ? result.AsSpan(0, (int)Math.Min(expectedLength, int.MaxValue)).ToArray()
            : result;
    }

    private static byte[] DecompressPalmDoc(
        byte[] data,
        int[] offsets,
        int count,
        uint expectedLength,
        ImportSecurityLimits limits)
    {
        if (expectedLength > limits.MaximumExpandedBytes || expectedLength > int.MaxValue)
        {
            throw FormatImportException.Unsupported(
                "The decompressed MOBI text exceeds the configured limit.");
        }

        var output = new List<byte>(checked((int)expectedLength));
        for (var record = 1; record <= count && record + 1 < offsets.Length; record++)
        {
            var position = offsets[record];
            var end = offsets[record + 1];
            while (position < end && output.Count < expectedLength)
            {
                var value = data[position++];
                if (value == 0 || value is >= 9 and <= 0x7f)
                {
                    output.Add(value);
                }
                else if (value is >= 1 and <= 8)
                {
                    var literalCount = Math.Min(value, end - position);
                    for (var index = 0; index < literalCount; index++)
                    {
                        output.Add(data[position++]);
                    }
                }
                else if (value is >= 0x80 and <= 0xbf)
                {
                    if (position >= end)
                    {
                        break;
                    }

                    var pair = (value << 8) | data[position++];
                    var distance = (pair >> 3) & 0x7ff;
                    var length = (pair & 7) + 3;
                    if (distance == 0 || distance > output.Count)
                    {
                        throw FormatImportException.Corrupt(
                            "The PalmDOC back-reference stream is invalid.");
                    }

                    for (var index = 0;
                         index < length && output.Count < expectedLength;
                         index++)
                    {
                        output.Add(output[output.Count - distance]);
                    }
                }
                else
                {
                    output.Add((byte)' ');
                    output.Add((byte)(value ^ 0x80));
                }
            }
        }

        return output.ToArray();
    }

    private static string? FirstNonEmpty(
        IReadOnlyDictionary<uint, string> values,
        params uint[] keys)
    {
        foreach (var key in keys)
        {
            if (values.TryGetValue(key, out var value) &&
                !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static Encoding GetWindows1252()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1252);
    }

    private static ushort ReadUInt16(byte[] data, int offset) =>
        checked((ushort)((data[offset] << 8) | data[offset + 1]));

    private static uint ReadUInt32(byte[] data, int offset) =>
        ((uint)data[offset] << 24) |
        ((uint)data[offset + 1] << 16) |
        ((uint)data[offset + 2] << 8) |
        data[offset + 3];

    private static uint ReadUInt32(ReadOnlySpan<byte> data, int offset) =>
        ((uint)data[offset] << 24) |
        ((uint)data[offset + 1] << 16) |
        ((uint)data[offset + 2] << 8) |
        data[offset + 3];
}
