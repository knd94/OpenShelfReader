using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using OpenShelf.Core;
using VersOne.Epub;

namespace OpenShelf.Formats;

public sealed partial class EpubFormatAdapter : IBookFormatAdapter
{
    private static readonly IReadOnlySet<string> SupportedExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".epub" };

    public BookFormat Format => BookFormat.Epub;

    public IReadOnlySet<string> Extensions => SupportedExtensions;

    public bool MatchesSignature(ReadOnlySpan<byte> header) =>
        header.StartsWith("PK\u0003\u0004"u8) ||
        header.IndexOf("application/epub+zip"u8) >= 0;

    public async Task<ImportedBook> ImportAsync(
        BookImportRequest request,
        IProgress<BookImportProgress>? progress,
        CancellationToken cancellationToken)
    {
        FormatUtilities.ValidateInput(request);
        progress?.Report(new BookImportProgress("Opening EPUB", request.DisplayName, 0, null, 0));

        try
        {
            using var package = new SafeZipPackage(request.FilePath, request.Limits);
            var opfPath = await FindPackageDocumentAsync(
                package,
                request.Limits,
                cancellationToken).ConfigureAwait(false);
            var opf = await ReadXmlAsync(
                package,
                opfPath,
                request.Limits,
                cancellationToken).ConfigureAwait(false);

            var manifest = ParseManifest(opf, opfPath);
            var spineIds = opf.Descendants()
                .Where(element => element.Name.LocalName == "itemref")
                .Select(element => element.Attribute("idref")?.Value)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Cast<string>()
                .ToArray();
            if (spineIds.Length == 0)
            {
                throw FormatImportException.Corrupt("The EPUB package has no reading order.");
            }

            await ValidateEncryptionAsync(
                package,
                manifest,
                request.Limits,
                cancellationToken).ConfigureAwait(false);

            var revisionHash = await FormatUtilities.ComputeRevisionHashAsync(
                request.FilePath,
                cancellationToken).ConfigureAwait(false);
            var metadataProbe = await TryReadMetadataWithVersOneAsync(
                request.FilePath,
                cancellationToken).ConfigureAwait(false);
            var metadata = ReadMetadata(opf, request, metadataProbe);
            var warnings = new List<string>();
            if (opf.Descendants().Any(element =>
                    element.Value.Contains(
                        "pre-paginated",
                        StringComparison.OrdinalIgnoreCase)))
            {
                warnings.Add(
                    "This is a fixed-layout EPUB; OpenShelf presents it in reduced-fidelity reading mode.");
            }

            var resources = await ReadImageResourcesAsync(
                package,
                manifest.Values,
                request.Limits,
                cancellationToken).ConfigureAwait(false);
            var resourcesByPath = resources.ToDictionary(
                item => item.Path,
                StringComparer.Ordinal);
            var manifestByPath = manifest.Values
                .GroupBy(item => item.Path, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            var stylesheetCache = new EpubStylesheetCache();

            var sections = new List<DocumentSection>();
            var sectionInfoByPath = new Dictionary<string, EpubSectionInfo>(StringComparer.Ordinal);
            for (var index = 0; index < spineIds.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!manifest.TryGetValue(spineIds[index], out var item))
                {
                    warnings.Add($"Spine item '{spineIds[index]}' is missing from the manifest.");
                    continue;
                }

                if (!package.Contains(item.Path))
                {
                    warnings.Add($"Reading-order file '{item.Path}' is missing.");
                    continue;
                }

                progress?.Report(new BookImportProgress(
                    "Parsing EPUB chapters",
                    item.Path,
                    index,
                    spineIds.Length,
                    (double)index / spineIds.Length));
                var bytes = await package.ReadAsync(
                    item.Path,
                    cancellationToken,
                    Math.Min(request.Limits.MaximumExpandedBytes, int.MaxValue))
                    .ConfigureAwait(false);
                var markup = DecodeMarkup(bytes);
                var cssBackgroundRules = await ReadCssBackgroundRulesAsync(
                    package,
                    item.Path,
                    markup,
                    manifestByPath,
                    stylesheetCache,
                    request.Limits,
                    cancellationToken).ConfigureAwait(false);
                var normalized = MarkupNormalizer.Normalize(
                    markup,
                    item.Path,
                    (reference, alternativeText) =>
                        ResolveImage(
                            item.Path,
                            reference,
                            alternativeText,
                            resourcesByPath),
                    includeCssBackgroundImages: true,
                    cssBackgroundRules: cssBackgroundRules,
                    resolveImageFromBase: (basePath, reference, alternativeText) =>
                        ResolveImage(
                            basePath,
                            reference,
                            alternativeText,
                            resourcesByPath));
                if (normalized.Blocks.Length == 0)
                {
                    continue;
                }

                var sectionId = FormatUtilities.StableId("section", revisionHash, item.Path);
                var sectionTitle = normalized.FirstHeading ??
                    Path.GetFileNameWithoutExtension(item.Path);
                var section = new DocumentSection(
                    sectionId,
                    sectionTitle,
                    normalized.Blocks,
                    0,
                    0);
                sections.Add(section);
                sectionInfoByPath[item.Path] = new EpubSectionInfo(
                    sectionId,
                    normalized.AnchorToBlockId,
                    normalized.Blocks);
            }

            if (sections.Count == 0)
            {
                throw FormatImportException.Corrupt(
                    "No readable chapters were found in the EPUB spine.");
            }

            var toc = await ReadTableOfContentsAsync(
                package,
                manifest,
                opf,
                sectionInfoByPath,
                revisionHash,
                request.Limits,
                cancellationToken).ConfigureAwait(false);
            if (toc.Length == 0)
            {
                toc = BuildHeadingToc(sections, revisionHash);
            }

            var cover = FindCover(opf, manifest, resources);
            var document = FormatUtilities.CreateReflowableDocument(
                metadata,
                revisionHash,
                sections,
                toc,
                resources.Select(item => item.Resource));
            progress?.Report(new BookImportProgress("Complete", request.DisplayName, 1, 1, 1));

            return new ImportedBook(
                Format,
                metadata,
                document,
                cover?.Data,
                cover?.MediaType,
                warnings.ToImmutableArray());
        }
        catch (FormatImportException)
        {
            throw;
        }
        catch (InvalidDataException exception)
        {
            throw FormatImportException.Corrupt(
                "The EPUB ZIP container is damaged.",
                exception);
        }
        catch (XmlException exception)
        {
            throw FormatImportException.Corrupt(
                "The EPUB package metadata is malformed.",
                exception);
        }
    }

    private static async Task<string> FindPackageDocumentAsync(
        SafeZipPackage package,
        ImportSecurityLimits limits,
        CancellationToken cancellationToken)
    {
        const string containerPath = "META-INF/container.xml";
        if (package.Contains(containerPath))
        {
            var container = await ReadXmlAsync(
                package,
                containerPath,
                limits,
                cancellationToken).ConfigureAwait(false);
            var fullPath = container.Descendants()
                .FirstOrDefault(element => element.Name.LocalName == "rootfile")?
                .Attribute("full-path")?
                .Value;
            if (!string.IsNullOrWhiteSpace(fullPath))
            {
                var normalized = ArchivePath.NormalizeEntry(fullPath);
                if (package.Contains(normalized))
                {
                    return normalized;
                }
            }
        }

        var candidates = package.Paths
            .Where(path => path.EndsWith(".opf", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        return candidates.Length switch
        {
            1 => candidates[0],
            0 => throw FormatImportException.Corrupt(
                "The EPUB has no package document."),
            _ => throw FormatImportException.Corrupt(
                "The EPUB package document is ambiguous.")
        };
    }

    private static async Task<XDocument> ReadXmlAsync(
        SafeZipPackage package,
        string path,
        ImportSecurityLimits limits,
        CancellationToken cancellationToken)
    {
        var bytes = await package.ReadAsync(
            path,
            cancellationToken,
            Math.Min(limits.MaximumExpandedBytes, int.MaxValue)).ConfigureAwait(false);
        FormatUtilities.ValidateXmlDepth(bytes, limits);
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = XmlReader.Create(
            stream,
            FormatUtilities.SafeXmlSettings(
                Math.Min(limits.MaximumExpandedBytes, bytes.LongLength + 1)));
        return XDocument.Load(reader, LoadOptions.None);
    }

    private static Dictionary<string, EpubManifestItem> ParseManifest(
        XDocument opf,
        string opfPath)
    {
        var result = new Dictionary<string, EpubManifestItem>(StringComparer.Ordinal);
        foreach (var element in opf.Descendants()
                     .Where(element => element.Name.LocalName == "item"))
        {
            var id = element.Attribute("id")?.Value;
            var href = element.Attribute("href")?.Value;
            if (string.IsNullOrWhiteSpace(id) ||
                string.IsNullOrWhiteSpace(href) ||
                !ArchivePath.TryResolve(opfPath, href, out var path))
            {
                continue;
            }

            result[id] = new EpubManifestItem(
                id,
                path,
                element.Attribute("media-type")?.Value ?? "application/octet-stream",
                (element.Attribute("properties")?.Value ?? string.Empty)
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase));
        }

        return result;
    }

    private static BookMetadata ReadMetadata(
        XDocument opf,
        BookImportRequest request,
        EpubMetadataProbe? probe)
    {
        var metadataElement = opf.Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "metadata");
        string? First(string localName) => metadataElement?
            .Descendants()
            .FirstOrDefault(element => element.Name.LocalName == localName)?
            .Value;

        var title = FormatUtilities.CleanText(First("title"));
        if (title.Length == 0)
        {
            title = FormatUtilities.CleanText(probe?.Title);
        }

        var creators = metadataElement?
            .Descendants()
            .Where(element => element.Name.LocalName == "creator")
            .Select(element => FormatUtilities.CleanText(element.Value))
            .Where(value => value.Length > 0)
            .ToArray() ?? [];
        if (creators.Length == 0)
        {
            creators = probe?.Authors
                .Select(FormatUtilities.CleanText)
                .Where(value => value.Length > 0)
                .ToArray() ?? [];
        }

        return new BookMetadata(
            title.Length == 0 ? FormatUtilities.FallbackTitle(request) : title,
            creators.Length == 0 ? "Unknown author" : string.Join("; ", creators),
            CleanNullable(First("description")) ?? CleanNullable(probe?.Description),
            CleanNullable(First("language")),
            CleanNullable(First("publisher")),
            CleanNullable(First("identifier")));
    }

    private static async Task<EpubMetadataProbe?> TryReadMetadataWithVersOneAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        // SafeZipPackage has already bounded and validated this exact archive before
        // this metadata-only compatibility probe is allowed to open it. OpenBookAsync
        // intentionally leaves chapter and image payloads lazy.
        cancellationToken.ThrowIfCancellationRequested();
        var openTask = EpubReader.OpenBookAsync(filePath);
        EpubBookRef book;
        try
        {
            book = await openTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _ = DisposeWhenCompletedAsync(openTask);
            throw;
        }
        catch (Exception exception) when (IsNonCriticalProbeFailure(exception))
        {
            return null;
        }

        using (book)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new EpubMetadataProbe(
                book.Title,
                book.AuthorList?.ToImmutableArray() ?? [],
                book.Description);
        }
    }

    private static async Task DisposeWhenCompletedAsync(Task<EpubBookRef> openTask)
    {
        try
        {
            using var book = await openTask.ConfigureAwait(false);
        }
        catch
        {
            // Observe a late probe failure after the caller has already cancelled.
        }
    }

    private static bool IsNonCriticalProbeFailure(Exception exception) =>
        exception is not OutOfMemoryException and
            not AccessViolationException and
            not StackOverflowException;

    private static async Task ValidateEncryptionAsync(
        SafeZipPackage package,
        IReadOnlyDictionary<string, EpubManifestItem> manifest,
        ImportSecurityLimits limits,
        CancellationToken cancellationToken)
    {
        const string encryptionPath = "META-INF/encryption.xml";
        if (!package.Contains(encryptionPath))
        {
            return;
        }

        var encryption = await ReadXmlAsync(
            package,
            encryptionPath,
            limits,
            cancellationToken).ConfigureAwait(false);
        var itemByPath = manifest.Values.ToDictionary(item => item.Path, StringComparer.Ordinal);
        foreach (var encryptedData in encryption.Descendants()
                     .Where(element => element.Name.LocalName == "EncryptedData"))
        {
            var algorithm = encryptedData.Descendants()
                .FirstOrDefault(element => element.Name.LocalName == "EncryptionMethod")?
                .Attribute("Algorithm")?
                .Value;
            var reference = encryptedData.Descendants()
                .FirstOrDefault(element => element.Name.LocalName == "CipherReference")?
                .Attribute("URI")?
                .Value;
            var isFontObfuscation = algorithm is
                "http://www.idpf.org/2008/embedding" or
                "http://ns.adobe.com/pdf/enc#RC";
            if (isFontObfuscation &&
                reference is not null &&
                ArchivePath.TryResolve(encryptionPath, reference, out var resolved) &&
                itemByPath.TryGetValue(resolved, out var item) &&
                item.MediaType.StartsWith("font/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            throw FormatImportException.DrmProtected(
                "The EPUB contains encrypted reading content that OpenShelf cannot decrypt.");
        }
    }

    private static async Task<ImmutableArray<CssBackgroundRule>> ReadCssBackgroundRulesAsync(
        SafeZipPackage package,
        string chapterPath,
        string markup,
        IReadOnlyDictionary<string, EpubManifestItem> manifestByPath,
        EpubStylesheetCache cache,
        ImportSecurityLimits limits,
        CancellationToken cancellationToken)
    {
        const int maximumRulesPerChapter = 4_096;
        var parser = new HtmlParser(new HtmlParserOptions { IsScripting = false });
        var document = parser.ParseDocument(markup);
        var result = ImmutableArray.CreateBuilder<CssBackgroundRule>();
        var sourceOrder = 0;

        foreach (var element in document.QuerySelectorAll("style, link"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ImmutableArray<CssBackgroundRule> rules;
            if (element.TagName == "STYLE")
            {
                var type = element.GetAttribute("type");
                if (!string.IsNullOrWhiteSpace(type) &&
                    !type.Equals("text/css", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                rules = CssBackgroundStylesheetParser.Parse(
                    element.TextContent,
                    chapterPath);
            }
            else
            {
                var relations = (element.GetAttribute("rel") ?? string.Empty)
                    .Split(
                        [' ', '\t', '\r', '\n'],
                        StringSplitOptions.RemoveEmptyEntries);
                if (!relations.Contains("stylesheet", StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                var href = element.GetAttribute("href");
                if (string.IsNullOrWhiteSpace(href) ||
                    !ArchivePath.TryResolve(chapterPath, href, out var stylesheetPath))
                {
                    continue;
                }

                rules = await ReadLinkedStylesheetRulesAsync(
                    package,
                    stylesheetPath,
                    manifestByPath,
                    cache,
                    limits,
                    cancellationToken).ConfigureAwait(false);
            }

            foreach (var rule in rules)
            {
                if (result.Count >= maximumRulesPerChapter)
                {
                    throw FormatImportException.Unsupported(
                        $"A chapter applies more than {maximumRulesPerChapter:N0} " +
                        "supported CSS background-image rules.");
                }

                result.Add(rule with { SourceOrder = sourceOrder++ });
            }
        }

        return result.ToImmutable();
    }

    private static async Task<ImmutableArray<CssBackgroundRule>> ReadLinkedStylesheetRulesAsync(
        SafeZipPackage package,
        string stylesheetPath,
        IReadOnlyDictionary<string, EpubManifestItem> manifestByPath,
        EpubStylesheetCache cache,
        ImportSecurityLimits limits,
        CancellationToken cancellationToken)
    {
        if (cache.Rules.TryGetValue(stylesheetPath, out var cached))
        {
            return cached;
        }

        if (!manifestByPath.TryGetValue(stylesheetPath, out var manifestItem) ||
            !package.Contains(stylesheetPath) ||
            !manifestItem.MediaType.Equals("text/css", StringComparison.OrdinalIgnoreCase) &&
            !Path.GetExtension(stylesheetPath).Equals(".css", StringComparison.OrdinalIgnoreCase))
        {
            cache.Rules[stylesheetPath] = [];
            return [];
        }

        var length = package.GetLength(stylesheetPath);
        if (length < 0 ||
            length > limits.MaximumResourceBytes ||
            cache.AggregateBytes > limits.MaximumResourceBytes - length)
        {
            throw FormatImportException.Unsupported(
                $"The EPUB's linked stylesheets exceed the aggregate " +
                $"{limits.MaximumResourceBytes:N0}-byte resource limit.");
        }

        cache.AggregateBytes += length;
        var bytes = await package.ReadAsync(
            stylesheetPath,
            cancellationToken,
            limits.MaximumResourceBytes).ConfigureAwait(false);
        var rules = CssBackgroundStylesheetParser.Parse(
            DecodeMarkup(bytes),
            stylesheetPath);
        cache.Rules[stylesheetPath] = rules;
        return rules;
    }

    private static async Task<List<EpubImageResource>> ReadImageResourcesAsync(
        SafeZipPackage package,
        IEnumerable<EpubManifestItem> manifest,
        ImportSecurityLimits limits,
        CancellationToken cancellationToken)
    {
        var result = new List<EpubImageResource>();
        var imageItems = new List<EpubManifestItem>();
        var seenPaths = new HashSet<string>(StringComparer.Ordinal);
        long aggregateImageBytes = 0;
        foreach (var item in manifest.OrderBy(item => item.Path, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!item.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ||
                !package.Contains(item.Path) ||
                !seenPaths.Add(item.Path))
            {
                continue;
            }

            var resourceLength = package.GetLength(item.Path);
            if (resourceLength > limits.MaximumResourceBytes ||
                aggregateImageBytes > limits.MaximumResourceBytes - resourceLength)
            {
                throw FormatImportException.Unsupported(
                    $"The EPUB's embedded images exceed the aggregate " +
                    $"{limits.MaximumResourceBytes:N0}-byte resource limit.");
            }

            aggregateImageBytes += resourceLength;
            imageItems.Add(item);
        }

        foreach (var item in imageItems)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var data = await package.ReadAsync(
                item.Path,
                cancellationToken,
                limits.MaximumResourceBytes).ConfigureAwait(false);
            var mediaType = FormatUtilities.DetectMediaType(data, item.Path) ??
                item.MediaType;
            if (!mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            FormatUtilities.ValidateImage(data, mediaType, limits);
            ImageDimensions.TryRead(data, mediaType, out var width, out var height);
            result.Add(new EpubImageResource(
                item.Id,
                item.Path,
                item.Properties,
                width > 0 ? width : null,
                height > 0 ? height : null,
                new BookResource(
                    FormatUtilities.StableId("resource", item.Path),
                    mediaType,
                    data,
                    item.Path)));
        }

        return result;
    }

    private static ResolvedImage? ResolveImage(
        string basePath,
        string reference,
        string? alternativeText,
        IReadOnlyDictionary<string, EpubImageResource> resources)
    {
        if (!ArchivePath.TryResolve(basePath, reference, out var resolved) ||
            !resources.TryGetValue(resolved, out var resource))
        {
            return null;
        }

        return new ResolvedImage(
            resource.Resource.Id,
            alternativeText,
            resource.Width,
            resource.Height);
    }

    private static async Task<ImmutableArray<TableOfContentsItem>> ReadTableOfContentsAsync(
        SafeZipPackage package,
        IReadOnlyDictionary<string, EpubManifestItem> manifest,
        XDocument opf,
        IReadOnlyDictionary<string, EpubSectionInfo> sections,
        string revisionHash,
        ImportSecurityLimits limits,
        CancellationToken cancellationToken)
    {
        var nav = manifest.Values.FirstOrDefault(item =>
            item.Properties.Contains("nav") && package.Contains(item.Path));
        if (nav is not null)
        {
            var bytes = await package.ReadAsync(
                nav.Path,
                cancellationToken,
                Math.Min(limits.MaximumExpandedBytes, int.MaxValue)).ConfigureAwait(false);
            var parser = new HtmlParser(new HtmlParserOptions { IsScripting = false });
            var document = parser.ParseDocument(DecodeMarkup(bytes));
            var navElement = document.QuerySelectorAll("nav").FirstOrDefault(element =>
                (element.GetAttribute("epub:type") ?? element.GetAttribute("type") ?? string.Empty)
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Contains("toc", StringComparer.OrdinalIgnoreCase) ||
                element.GetAttribute("role") == "doc-toc");
            var rootList = navElement?.Children.FirstOrDefault(child => child.TagName == "OL");
            if (rootList is not null)
            {
                return ParseHtmlTocList(
                    rootList,
                    nav.Path,
                    sections,
                    revisionHash,
                    "nav").ToImmutableArray();
            }
        }

        var spine = opf.Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "spine");
        var ncxId = spine?.Attribute("toc")?.Value;
        EpubManifestItem? ncx = null;
        if (!string.IsNullOrWhiteSpace(ncxId))
        {
            manifest.TryGetValue(ncxId, out ncx);
        }

        ncx ??= manifest.Values.FirstOrDefault(item =>
            item.MediaType.Equals(
                "application/x-dtbncx+xml",
                StringComparison.OrdinalIgnoreCase));
        if (ncx is not null && package.Contains(ncx.Path))
        {
            var document = await ReadXmlAsync(
                package,
                ncx.Path,
                limits,
                cancellationToken).ConfigureAwait(false);
            var navMap = document.Descendants()
                .FirstOrDefault(element => element.Name.LocalName == "navMap");
            if (navMap is not null)
            {
                return navMap.Elements()
                    .Where(element => element.Name.LocalName == "navPoint")
                    .Select((element, index) => ParseNcxPoint(
                        element,
                        ncx.Path,
                        sections,
                        revisionHash,
                        index.ToString(CultureInfo.InvariantCulture)))
                    .Where(item => item is not null)
                    .Cast<TableOfContentsItem>()
                    .ToImmutableArray();
            }
        }

        return [];
    }

    private static IEnumerable<TableOfContentsItem> ParseHtmlTocList(
        IElement list,
        string tocPath,
        IReadOnlyDictionary<string, EpubSectionInfo> sections,
        string revisionHash,
        string path)
    {
        var index = 0;
        foreach (var item in list.Children.Where(child => child.TagName == "LI"))
        {
            var label = item.Children.FirstOrDefault(child =>
                child.TagName is "A" or "SPAN");
            var title = FormatUtilities.CleanText(label?.TextContent);
            var href = label?.GetAttribute("href");
            if (title.Length == 0 ||
                !TryResolveTocTarget(
                    tocPath,
                    href,
                    sections,
                    out var targetSection,
                    out var targetBlock))
            {
                index++;
                continue;
            }

            var childList = item.Children.FirstOrDefault(child => child.TagName == "OL");
            var itemPath = $"{path}/{index++}";
            var children = childList is null
                ? []
                : ParseHtmlTocList(
                    childList,
                    tocPath,
                    sections,
                    revisionHash,
                    itemPath).ToImmutableArray();
            yield return new TableOfContentsItem(
                FormatUtilities.StableId("toc", revisionHash, itemPath, href),
                title,
                targetSection,
                targetBlock,
                children);
        }
    }

    private static TableOfContentsItem? ParseNcxPoint(
        XElement point,
        string ncxPath,
        IReadOnlyDictionary<string, EpubSectionInfo> sections,
        string revisionHash,
        string path)
    {
        var title = FormatUtilities.CleanText(point.Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "navLabel")?
            .Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "text")?
            .Value);
        var source = point.Elements()
            .FirstOrDefault(element => element.Name.LocalName == "content")?
            .Attribute("src")?
            .Value;
        if (title.Length == 0 ||
            !TryResolveTocTarget(
                ncxPath,
                source,
                sections,
                out var targetSection,
                out var targetBlock))
        {
            return null;
        }

        var children = point.Elements()
            .Where(element => element.Name.LocalName == "navPoint")
            .Select((element, index) => ParseNcxPoint(
                element,
                ncxPath,
                sections,
                revisionHash,
                $"{path}/{index}"))
            .Where(item => item is not null)
            .Cast<TableOfContentsItem>()
            .ToImmutableArray();
        return new TableOfContentsItem(
            FormatUtilities.StableId("toc", revisionHash, path, source),
            title,
            targetSection,
            targetBlock,
            children);
    }

    private static bool TryResolveTocTarget(
        string tocPath,
        string? reference,
        IReadOnlyDictionary<string, EpubSectionInfo> sections,
        out string sectionId,
        out string? blockId)
    {
        sectionId = string.Empty;
        blockId = null;
        if (string.IsNullOrWhiteSpace(reference) ||
            !ArchivePath.TryResolve(tocPath, reference, out var resolvedPath) ||
            !sections.TryGetValue(resolvedPath, out var section))
        {
            return false;
        }

        var hashIndex = reference.IndexOf('#');
        var fragment = hashIndex >= 0 && hashIndex + 1 < reference.Length
            ? reference[(hashIndex + 1)..]
            : null;
        sectionId = section.SectionId;
        blockId = fragment is not null &&
            section.Anchors.TryGetValue(fragment, out var anchor)
                ? anchor
                : section.Blocks.FirstOrDefault()?.Id;
        return true;
    }

    private static ImmutableArray<TableOfContentsItem> BuildHeadingToc(
        IReadOnlyList<DocumentSection> sections,
        string revisionHash)
    {
        return sections.Select((section, index) => new TableOfContentsItem(
            FormatUtilities.StableId("toc", revisionHash, index.ToString(CultureInfo.InvariantCulture)),
            section.Title ?? $"Section {index + 1}",
            section.Id,
            section.Blocks.FirstOrDefault()?.Id,
            [])).ToImmutableArray();
    }

    private static BookResource? FindCover(
        XDocument opf,
        IReadOnlyDictionary<string, EpubManifestItem> manifest,
        IReadOnlyList<EpubImageResource> resources)
    {
        var coverId = opf.Descendants()
            .Where(element => element.Name.LocalName == "meta")
            .FirstOrDefault(element =>
                element.Attribute("name")?.Value.Equals(
                    "cover",
                    StringComparison.OrdinalIgnoreCase) == true)?
            .Attribute("content")?
            .Value;
        if (coverId is not null &&
            manifest.TryGetValue(coverId, out var manifestCover))
        {
            return resources.FirstOrDefault(resource =>
                resource.Path == manifestCover.Path)?.Resource;
        }

        return resources.FirstOrDefault(resource =>
            resource.Properties.Contains("cover-image"))?.Resource;
    }

    private static string DecodeMarkup(byte[] data)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var bomEncoding = DetectBom(data, out var bomLength);
        if (bomEncoding is not null)
        {
            return bomEncoding.GetString(data, bomLength, data.Length - bomLength);
        }

        var prefix = Encoding.ASCII.GetString(data, 0, Math.Min(data.Length, 1024));
        var match = EncodingDeclarationRegex().Match(prefix);
        if (match.Success)
        {
            try
            {
                return Encoding.GetEncoding(match.Groups["encoding"].Value).GetString(data);
            }
            catch (ArgumentException)
            {
                // Invalid publisher encoding declarations fall back to UTF-8.
            }
        }

        return Encoding.UTF8.GetString(data);
    }

    private static Encoding? DetectBom(byte[] data, out int length)
    {
        length = 0;
        if (data.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }))
        {
            length = 3;
            return Encoding.UTF8;
        }

        if (data.AsSpan().StartsWith(new byte[] { 0xff, 0xfe }))
        {
            length = 2;
            return Encoding.Unicode;
        }

        if (data.AsSpan().StartsWith(new byte[] { 0xfe, 0xff }))
        {
            length = 2;
            return Encoding.BigEndianUnicode;
        }

        return null;
    }

    private static string? CleanNullable(string? value)
    {
        var cleaned = FormatUtilities.CleanText(value);
        return cleaned.Length == 0 ? null : cleaned;
    }

    [GeneratedRegex(
        """(?:encoding\s*=\s*["'](?<encoding>[^"']+)["']|charset\s*=\s*["']?(?<encoding>[A-Za-z0-9._-]+))""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EncodingDeclarationRegex();

    private sealed record EpubManifestItem(
        string Id,
        string Path,
        string MediaType,
        ImmutableHashSet<string> Properties);

    private sealed record EpubMetadataProbe(
        string? Title,
        ImmutableArray<string> Authors,
        string? Description);

    private sealed class EpubStylesheetCache
    {
        public Dictionary<string, ImmutableArray<CssBackgroundRule>> Rules { get; } =
            new(StringComparer.Ordinal);

        public long AggregateBytes { get; set; }
    }

    private sealed record EpubImageResource(
        string ManifestId,
        string Path,
        ImmutableHashSet<string> Properties,
        double? Width,
        double? Height,
        BookResource Resource);

    private sealed record EpubSectionInfo(
        string SectionId,
        ImmutableDictionary<string, string> Anchors,
        ImmutableArray<DocumentBlock> Blocks);
}
