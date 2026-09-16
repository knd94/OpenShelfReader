using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenShelf.App.Services;
using OpenShelf.App.ViewModels;
using Xunit;

namespace OpenShelf.Tests.UI;

public sealed class ShellViewModelTests
{
    [Fact]
    public void Sample_library_supports_search_favourites_and_categories()
    {
        var viewModel = CreateViewModel();

        viewModel.LoadSampleLibraryCommand.Execute(null);
        Assert.Equal(6, viewModel.VisibleBooks.Count);

        viewModel.SearchText = "Copper";
        Assert.Single(viewModel.VisibleBooks);
        Assert.Equal("Under Copper Skies", viewModel.VisibleBooks[0].Title);

        viewModel.SearchText = string.Empty;
        viewModel.ShowFavouritesCommand.Execute(null);
        Assert.Single(viewModel.VisibleBooks);
        Assert.True(viewModel.VisibleBooks[0].IsFavorite);

        viewModel.ShowAllCommand.Execute(null);
        viewModel.SelectedLibrarySort = "Favourites first";
        Assert.True(viewModel.VisibleBooks[0].IsFavorite);

        var fiction = viewModel.Categories.Single(category => category.Name == "Fiction");
        viewModel.SelectCategoryCommand.Execute(fiction);
        Assert.Equal(3, viewModel.VisibleBooks.Count);
    }

    [Fact]
    public async Task Reader_annotations_survive_layout_changes()
    {
        var viewModel = CreateViewModel();
        viewModel.LoadSampleLibraryCommand.Execute(null);
        viewModel.OpenBookCommand.Execute(viewModel.VisibleBooks[0]);
        await WaitUntilAsync(() => viewModel.IsReaderOpen);

        viewModel.UpdateReaderSelection(
            "A good reader should disappear",
            "chapter-1-p1",
            startOffset: 0,
            length: 30);
        viewModel.HighlightSelectionCommand.Execute(null);
        viewModel.ReaderFontSize = 27;
        viewModel.ReaderLineSpacing = 1.9;
        viewModel.SelectedReaderTheme = "Sepia";

        var highlight = Assert.Single(viewModel.Annotations);
        Assert.Equal(AnnotationKind.Highlight, highlight.Kind);
        Assert.Equal("chapter-1-p1", highlight.Locator);
        Assert.Equal("A good reader should disappear", highlight.Excerpt);
        var textBlock = Assert.IsType<ReaderParagraphBlockViewModel>(
            viewModel.ReaderBlocks.Single());
        var paintedRange = Assert.Single(textBlock.HighlightRanges);
        Assert.Equal(0, paintedRange.Start);
        Assert.Equal(30, paintedRange.Length);
        Assert.Equal("Yellow", paintedRange.ColorName);
    }

    [Fact]
    public async Task Progress_debounce_can_be_flushed_repeatedly_without_disposed_source_errors()
    {
        var viewModel = CreateViewModel();
        viewModel.LoadSampleLibraryCommand.Execute(null);
        viewModel.OpenBookCommand.Execute(viewModel.VisibleBooks[0]);
        await WaitUntilAsync(() => viewModel.IsReaderOpen);

        for (var index = 0; index < 100; index++)
        {
            viewModel.SetReadingProgress(index / 100d);
            await viewModel.FlushReadingProgressAsync(
                TestContext.Current.CancellationToken);
        }

        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.DoesNotContain(
            "CancellationTokenSource has been disposed",
            viewModel.StatusMessage,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Metadata_edits_are_local_to_the_book_view_model()
    {
        var viewModel = CreateViewModel();
        viewModel.LoadSampleLibraryCommand.Execute(null);
        var book = viewModel.VisibleBooks[0];

        viewModel.OpenBookEditorCommand.Execute(book);
        viewModel.EditingTitle = "A New Local Title";
        viewModel.EditingAuthor = "Local Author";
        viewModel.EditingCategory = "Reference";
        viewModel.SaveBookEditorCommand.Execute(null);

        Assert.Equal("A New Local Title", book.Title);
        Assert.Equal("Local Author", book.Author);
        Assert.Equal("Reference", book.Category);
    }

    [Fact]
    public void Book_time_remaining_uses_progress_personal_speed_and_tts_speed()
    {
        var book = new BookItemViewModel(
            new BookDescriptor(
                Guid.NewGuid(),
                "Timed book",
                "Reader",
                "EPUB",
                string.Empty,
                string.Empty,
                "Fiction",
                null,
                Progress: 0.5,
                EstimatedWordCount: 60000));

        book.SetTimeEstimateSpeeds(500, 250);

        Assert.Equal("50% · Read 1h · TTS 2h", book.ProgressAndTimeLabel);
        book.Progress = 1;
        Assert.Equal("Finished · 0 min left", book.ProgressAndTimeLabel);
    }

    [Fact]
    public async Task Text_to_speech_starts_at_double_clicked_sentence_and_persists_anchor()
    {
        var gateway = new StubGateway();
        var speech = new StubSpeechGateway();
        var viewModel = CreateViewModel(gateway, speech);
        viewModel.LoadSampleLibraryCommand.Execute(null);
        viewModel.OpenBookCommand.Execute(viewModel.VisibleBooks[0]);
        await WaitUntilAsync(() => viewModel.IsReaderOpen);

        await viewModel.StartSpeechAtAsync("chapter-1-p1", 18);

        Assert.Equal(1, speech.StartIndex);
        Assert.Equal("Second sentence begins here.", speech.Segments[1]);
        await WaitUntilAsync(() => gateway.SavedSpeechLocator is not null);
        Assert.Equal("chapter-1-p1", gateway.SavedSpeechLocator);
        Assert.Equal(1, gateway.SavedSpeechSentenceIndex);
        Assert.Equal(16, gateway.SavedSpeechCharacterOffset);
    }

    [Fact]
    public async Task Text_to_speech_keeps_closing_quotes_with_the_spoken_sentence()
    {
        const string text = "\"First sentence.\" Second sentence.";
        var gateway = new StubGateway
        {
            ReaderBlocks = new ReaderBlockViewModel[]
            {
                new ReaderParagraphBlockViewModel("quoted-dialogue", text)
            }
        };
        var speech = new StubSpeechGateway();
        var viewModel = CreateViewModel(gateway, speech);
        viewModel.LoadSampleLibraryCommand.Execute(null);
        viewModel.OpenBookCommand.Execute(viewModel.VisibleBooks[0]);
        await WaitUntilAsync(() => viewModel.IsReaderOpen);

        await viewModel.StartSpeechAtAsync(
            "quoted-dialogue",
            text.IndexOf("Second", StringComparison.Ordinal));

        Assert.Equal(1, speech.StartIndex);
        Assert.Equal(new[] { "\"First sentence.\"", "Second sentence." }, speech.Segments);
    }

    [Fact]
    public async Task Reader_reopen_resumes_from_persisted_spoken_sentence()
    {
        var gateway = new StubGateway
        {
            LibraryBooks = new[]
            {
                new BookDescriptor(
                    Guid.NewGuid(),
                    "Resume test",
                    "Reader",
                    "EPUB",
                    string.Empty,
                    string.Empty,
                    "Uncategorised",
                    null,
                    SpeechLocator: "chapter-1-p1",
                    SpeechSentenceIndex: 1,
                    SpeechCharacterOffset: 16)
            }
        };
        var speech = new StubSpeechGateway();
        var viewModel = CreateViewModel(gateway, speech);
        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);
        viewModel.OpenBookCommand.Execute(viewModel.VisibleBooks[0]);
        await WaitUntilAsync(() => viewModel.IsReaderOpen);

        viewModel.ToggleSpeechCommand.Execute(null);
        await WaitUntilAsync(() => speech.CallCount > 0);

        Assert.Equal(1, speech.StartIndex);
    }

    [Fact]
    public async Task Paged_layout_displays_a_responsive_spread_and_flips_by_spread()
    {
        var gateway = new StubGateway
        {
            ReaderBlocks = Enumerable.Range(0, 40)
                .Select(
                    index => (ReaderBlockViewModel)new ReaderParagraphBlockViewModel(
                        $"chapter|paragraph-{index}",
                        new string((char)('a' + index % 20), 420)))
                .ToArray()
        };
        var viewModel = CreateViewModel(gateway, new StubSpeechGateway());
        viewModel.LoadSampleLibraryCommand.Execute(null);
        viewModel.OpenBookCommand.Execute(viewModel.VisibleBooks[0]);
        await WaitUntilAsync(() => viewModel.IsReaderOpen);

        viewModel.UpdateReaderViewport(1500, 820);
        viewModel.SelectedReaderLayoutMode = "Paged";

        Assert.True(viewModel.ReaderPageCount > 1);
        Assert.Equal(2, viewModel.ReaderPageColumns);
        Assert.Equal(2, viewModel.DisplayedReaderPages.Count);
        Assert.True(viewModel.DisplayedReaderBlocks.Count < viewModel.ReaderBlocks.Count);
        viewModel.NextPageCommand.Execute(null);
        Assert.Equal(2, viewModel.CurrentReaderPageIndex);
        Assert.True(viewModel.ReadingProgress > 0);

        viewModel.UpdateReaderViewport(820, 760);
        Assert.Equal(1, viewModel.ReaderPageColumns);
        Assert.Single(viewModel.DisplayedReaderPages);
    }

    [Fact]
    public async Task Paged_layout_splits_long_epub_text_at_stable_character_offsets()
    {
        var text = string.Join(
            ' ',
            Enumerable.Range(0, 1500).Select(index => $"word{index}"));
        var gateway = new StubGateway
        {
            ReaderBlocks = new ReaderBlockViewModel[]
            {
                new ReaderParagraphBlockViewModel("chapter|long-paragraph", text)
            }
        };
        var viewModel = CreateViewModel(gateway, new StubSpeechGateway());
        viewModel.LoadSampleLibraryCommand.Execute(null);
        viewModel.OpenBookCommand.Execute(viewModel.VisibleBooks[0]);
        await WaitUntilAsync(() => viewModel.IsReaderOpen);

        viewModel.UpdateReaderViewport(1500, 820);
        viewModel.SelectedReaderLayoutMode = "Paged";

        Assert.True(viewModel.ReaderPageCount > 2);
        var firstSpread = viewModel.DisplayedReaderBlocks
            .OfType<ReaderTextBlockViewModel>()
            .OrderBy(fragment => fragment.SourceOffset)
            .ToArray();
        Assert.NotEmpty(firstSpread);
        Assert.Equal(0, firstSpread[0].SourceOffset);
        Assert.All(
            firstSpread,
            fragment => Assert.Equal("chapter|long-paragraph", fragment.Locator));

        viewModel.NextPageCommand.Execute(null);
        var nextSpreadFirst = viewModel.DisplayedReaderBlocks
            .OfType<ReaderTextBlockViewModel>()
            .Min(fragment => fragment.SourceOffset);
        Assert.True(nextSpreadFirst > 0);
        Assert.Contains(
            $"#{nextSpreadFirst}",
            viewModel.GetReaderLocatorAtProgress(viewModel.ReadingProgress),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Library_and_reader_settings_are_flushed_to_the_gateway()
    {
        var gateway = new StubGateway();
        var viewModel = CreateViewModel(gateway, new StubSpeechGateway());
        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);

        viewModel.SelectedLibraryTheme = "Light";
        viewModel.SelectedReaderTheme = "Sepia";
        viewModel.SelectedReaderLayoutMode = "Paged";
        await viewModel.FlushUiSettingsAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(gateway.SavedSettings);
        Assert.Equal("Light", gateway.SavedSettings.LibraryTheme);
        Assert.Equal("Sepia", gateway.SavedSettings.ReaderTheme);
        Assert.Equal("Paged", gateway.SavedSettings.ReaderLayoutMode);
    }

    [Fact]
    public async Task Speech_tracking_toggles_load_and_persist()
    {
        var gateway = new StubGateway
        {
            LoadedSettings = UiApplicationSettings.Default with
            {
                HighlightSpokenSentence = false,
                FollowSpokenSentence = false
            }
        };
        var viewModel = CreateViewModel(gateway, new StubSpeechGateway());

        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.False(viewModel.HighlightSpokenSentence);
        Assert.False(viewModel.FollowSpokenSentence);
        viewModel.HighlightSpokenSentence = true;
        viewModel.FollowSpokenSentence = true;
        await viewModel.FlushUiSettingsAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(gateway.SavedSettings);
        Assert.True(gateway.SavedSettings.HighlightSpokenSentence);
        Assert.True(gateway.SavedSettings.FollowSpokenSentence);
    }

    [Fact]
    public async Task Voice_settings_migrate_legacy_name_and_persist_controls()
    {
        var gateway = new StubGateway
        {
            LoadedSettings = UiApplicationSettings.Default with
            {
                SpeechVoice = "System default",
                SpeechRate = 210,
                SpeechVolume = 64,
                SpeechPitch = 1.3,
                PersonalReadingWordsPerMinute = 725
            }
        };
        var speech = new StubSpeechGateway();
        var viewModel = CreateViewModel(gateway, speech);

        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal("test:system", viewModel.SelectedVoice?.Id);
        Assert.Equal(210d / 175d, viewModel.SpeechRate, 8);
        Assert.Equal(64, viewModel.SpeechVolume);
        Assert.Equal(1.3, viewModel.SpeechPitch);
        Assert.Equal(725, viewModel.PersonalReadingWordsPerMinute);
        await viewModel.FlushUiSettingsAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(gateway.SavedSettings);
        Assert.Equal("test:system", gateway.SavedSettings.SpeechVoice);
        Assert.Equal(64, gateway.SavedSettings.SpeechVolume);
        Assert.Equal(1.3, gateway.SavedSettings.SpeechPitch);
        Assert.Equal(725, gateway.SavedSettings.PersonalReadingWordsPerMinute);
    }

    [Fact]
    public async Task Voice_refresh_and_preview_use_the_selected_stable_id()
    {
        var speech = new StubSpeechGateway();
        var viewModel = CreateViewModel(new StubGateway(), speech);
        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);

        viewModel.RefreshVoicesCommand.Execute(null);
        await WaitUntilAsync(() => speech.RefreshCount == 1 && !viewModel.IsRefreshingVoices);
        viewModel.PreviewVoiceCommand.Execute(null);
        await WaitUntilAsync(() => speech.PreviewCount == 1);

        Assert.Equal("test:system", speech.LastPreviewVoiceId);
        Assert.Equal(100, speech.LastPreviewVolume);
        Assert.Equal(1, speech.LastPreviewPitch);
    }

    [Fact]
    public async Task Speech_progress_highlights_and_follows_until_stop_without_closing_book()
    {
        var blocks = Enumerable.Range(0, 12)
            .Select(
                index => (ReaderBlockViewModel)new ReaderParagraphBlockViewModel(
                    $"chapter|paragraph-{index}",
                    new string((char)('a' + index), 1000) + "."))
            .ToArray();
        var gateway = new StubGateway { ReaderBlocks = blocks };
        var speech = new ControllableSpeechGateway();
        var viewModel = CreateViewModel(gateway, speech);
        viewModel.LoadSampleLibraryCommand.Execute(null);
        viewModel.OpenBookCommand.Execute(viewModel.VisibleBooks[0]);
        await WaitUntilAsync(() => viewModel.IsReaderOpen);
        var openBook = Assert.IsType<BookItemViewModel>(viewModel.CurrentBook);

        var target = Assert.IsType<ReaderParagraphBlockViewModel>(blocks[8]);
        viewModel.SelectedHighlightColor = "Blue";
        viewModel.UpdateReaderSelection("iiiii", target.Locator, 0, 5);
        viewModel.HighlightSelectionCommand.Execute(null);
        var savedHighlight = Assert.Single(target.HighlightRanges);
        viewModel.ClearReaderSelection();
        viewModel.SelectedReaderLayoutMode = "Paged";

        ReaderNavigationRequest? navigation = null;
        viewModel.ReaderNavigationRequested += (_, request) => navigation = request;
        viewModel.ToggleSpeechCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.IsSpeaking);

        speech.RaiseProgress(8);

        var spokenRange = Assert.IsType<ReaderSpokenRange>(target.SpokenRange);
        Assert.Equal(0, spokenRange.Start);
        Assert.Equal(target.Text.Length, spokenRange.Length);
        Assert.Contains(
            viewModel.DisplayedReaderBlocks.OfType<ReaderTextBlockViewModel>(),
            fragment => string.Equals(
                    fragment.Locator,
                    target.Locator,
                    StringComparison.Ordinal)
                && fragment.SpokenRange is not null);
        Assert.Same(savedHighlight, Assert.Single(target.HighlightRanges));
        Assert.True(viewModel.CurrentReaderPageIndex > 0);
        Assert.NotNull(navigation);
        Assert.StartsWith(target.Locator, navigation.Locator, StringComparison.Ordinal);

        viewModel.FollowSpokenSentence = false;
        var followedPage = viewModel.CurrentReaderPageIndex;
        speech.RaiseProgress(0);
        Assert.Equal(followedPage, viewModel.CurrentReaderPageIndex);

        var first = Assert.IsType<ReaderParagraphBlockViewModel>(blocks[0]);
        Assert.NotNull(first.SpokenRange);
        viewModel.HighlightSpokenSentence = false;
        Assert.Null(first.SpokenRange);
        viewModel.HighlightSpokenSentence = true;
        Assert.NotNull(first.SpokenRange);

        var stopsBeforeCommand = speech.StopCount;
        viewModel.StopSpeechCommand.Execute(null);

        Assert.False(viewModel.IsSpeaking);
        Assert.False(viewModel.IsSpeechPaused);
        Assert.True(viewModel.IsReaderOpen);
        Assert.Same(openBook, viewModel.CurrentBook);
        Assert.All(
            blocks.OfType<ReaderTextBlockViewModel>(),
            block => Assert.Null(block.SpokenRange));
        Assert.Same(savedHighlight, Assert.Single(target.HighlightRanges));
        Assert.True(speech.StopCount > stopsBeforeCommand);
    }

    [Fact]
    public async Task Reader_image_loader_is_lazy_and_runs_only_once()
    {
        var calls = 0;
        var image = new ReaderImageBlockViewModel(
            "image-1",
            cancellationToken =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                calls++;
                return Task.FromResult<Avalonia.Media.IImage?>(null);
            },
            "Missing test image");

        Assert.Equal(0, calls);
        Assert.False(image.IsImageMissing);
        await image.LoadAsync(TestContext.Current.CancellationToken);
        await image.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, calls);
        Assert.True(image.IsImageMissing);
    }

    [Fact]
    public async Task MultipleEpubAndPdfBooksKeepIndependentStablePositions()
    {
        var epubId = Guid.NewGuid();
        var pdfId = Guid.NewGuid();
        var epubBlocks = new ReaderBlockViewModel[]
        {
            new ReaderParagraphBlockViewModel(
                "epub-section|paragraph-1",
                new string('a', 100)),
            new ReaderParagraphBlockViewModel(
                "epub-section|paragraph-2",
                new string('b', 50))
        };
        var pdfBlocks = Enumerable.Range(0, 10)
            .Select(
                page => (ReaderBlockViewModel)new ReaderParagraphBlockViewModel(
                    $"page:{page}",
                    $"Selectable text for page {page + 1}"))
            .ToArray();
        var gateway = new StubGateway
        {
            LibraryBooks = new[]
            {
                new BookDescriptor(
                    epubId,
                    "EPUB A",
                    "Author A",
                    "EPUB",
                    string.Empty,
                    string.Empty,
                    "Fiction",
                    null,
                    Progress: 0.1,
                    ReadingLocator: "epub-section|paragraph-1#15"),
                new BookDescriptor(
                    pdfId,
                    "PDF B",
                    "Author B",
                    "PDF",
                    string.Empty,
                    string.Empty,
                    "Reference",
                    null,
                    Progress: 0.7,
                    ReadingLocator: "page:7@0.0000")
            },
            ReaderDocuments = new Dictionary<Guid, ReaderDocument>
            {
                [epubId] = new ReaderDocument(
                    epubBlocks,
                    Array.Empty<TocItemViewModel>()),
                [pdfId] = new ReaderDocument(
                    pdfBlocks,
                    Array.Empty<TocItemViewModel>(),
                    IsFixedPage: true,
                    PageCount: 10)
            }
        };
        var viewModel = CreateViewModel(gateway, new StubSpeechGateway());
        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);
        var epub = viewModel.VisibleBooks.Single(book => book.Id == epubId);
        var pdf = viewModel.VisibleBooks.Single(book => book.Id == pdfId);

        viewModel.OpenBookCommand.Execute(epub);
        await WaitUntilAsync(() => viewModel.CurrentBook?.Id == epubId);
        viewModel.SetReaderPosition("epub-section|paragraph-1", 0.5);
        var stableEpubProgress = viewModel.ReadingProgress;
        viewModel.ReaderFontSize = 31;
        viewModel.ReaderContentWidth = 520;
        viewModel.SelectedReaderLayoutMode = "Paged";

        Assert.Equal(stableEpubProgress, viewModel.ReadingProgress, 8);
        viewModel.CloseReaderCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.CurrentBook is null);
        Assert.Equal(
            "epub-section|paragraph-1#50",
            gateway.SavedReadingPositions[epubId].Locator);

        viewModel.OpenBookCommand.Execute(pdf);
        await WaitUntilAsync(() => viewModel.CurrentBook?.Id == pdfId);
        Assert.Equal(0.7, viewModel.ReadingProgress, 8);
        Assert.Equal(7, viewModel.CurrentReaderPageIndex);
        Assert.Equal(stableEpubProgress, epub.Progress, 8);
        viewModel.SetReaderPosition("page:3@0.2500", 0);
        viewModel.CloseReaderCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.CurrentBook is null);

        Assert.Equal(2, gateway.SavedReadingPositions.Count);
        Assert.Equal(stableEpubProgress, gateway.SavedReadingPositions[epubId].Progress, 8);
        Assert.Equal(0.325, gateway.SavedReadingPositions[pdfId].Progress, 8);
        Assert.Equal("page:3@0.2500", gateway.SavedReadingPositions[pdfId].Locator);
    }

    private static ShellViewModel CreateViewModel()
    {
        var gateway = new StubGateway();
        return CreateViewModel(gateway, new StubSpeechGateway());
    }

    private static ShellViewModel CreateViewModel(
        StubGateway gateway,
        ITextToSpeechGateway speech)
    {
        return new ShellViewModel(
            new AppServices(
                gateway,
                gateway,
                speech,
                new StubDiagnosticsGateway()));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 50 && !condition(); attempt++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    private sealed class StubGateway : ILibraryGateway, IReaderGateway
    {
        public IReadOnlyList<BookDescriptor> LibraryBooks { get; init; } =
            Array.Empty<BookDescriptor>();

        public string? SavedSpeechLocator { get; private set; }

        public int SavedSpeechSentenceIndex { get; private set; }

        public int SavedSpeechCharacterOffset { get; private set; }

        public IReadOnlyList<ReaderBlockViewModel>? ReaderBlocks { get; init; }

        public IReadOnlyDictionary<Guid, ReaderDocument>? ReaderDocuments { get; init; }

        public Dictionary<Guid, (double Progress, string Locator)> SavedReadingPositions
            { get; } = new();

        public UiApplicationSettings? SavedSettings { get; private set; }

        public UiApplicationSettings LoadedSettings { get; init; } =
            UiApplicationSettings.Default;

        public Task<IReadOnlyList<BookDescriptor>> LoadLibraryAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(LibraryBooks);

        public Task<ImportBatchResult> ImportAsync(
            IReadOnlyList<string> selectedPaths,
            IProgress<ImportProgress> progress,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(
                new ImportBatchResult(
                    Array.Empty<BookDescriptor>(),
                    Array.Empty<ImportResultItemViewModel>(),
                    0,
                    false));
        }

        public Task<ReaderDocument> OpenAsync(
            BookDescriptor book,
            CancellationToken cancellationToken)
        {
            if (ReaderDocuments?.TryGetValue(book.Id, out var document) == true)
            {
                return Task.FromResult(document);
            }

            IReadOnlyList<ReaderBlockViewModel> blocks = ReaderBlocks
                ?? new ReaderBlockViewModel[]
                {
                    new ReaderParagraphBlockViewModel(
                        "chapter-1-p1",
                        "First sentence. Second sentence begins here. Third sentence.")
                };
            TocItemViewModel[] toc =
            {
                new("Chapter one", "chapter-1-p1", 1, 0)
            };
            return Task.FromResult(new ReaderDocument(blocks, toc));
        }

        public Task SaveReadingProgressAsync(
            Guid bookId,
            double progress,
            string locator,
            CancellationToken cancellationToken = default)
        {
            SavedReadingPositions[bookId] = (progress, locator);
            return Task.CompletedTask;
        }

        public Task SaveSpeechPositionAsync(
            Guid bookId,
            string locator,
            int sentenceIndex,
            int characterOffset,
            CancellationToken cancellationToken = default)
        {
            SavedSpeechLocator = locator;
            SavedSpeechSentenceIndex = sentenceIndex;
            SavedSpeechCharacterOffset = characterOffset;
            return Task.CompletedTask;
        }

        public Task SaveUiSettingsAsync(
            UiApplicationSettings settings,
            CancellationToken cancellationToken = default)
        {
            SavedSettings = settings;
            return Task.CompletedTask;
        }

        public Task<UiApplicationSettings> LoadUiSettingsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(LoadedSettings);
    }

    private sealed class StubSpeechGateway : ITextToSpeechGateway
    {
        public IReadOnlyList<SpeechVoiceOption> Voices { get; } =
            new[] { CreateTestVoice() };

        public string ProviderStatus => "Test provider";

        public string PeterStatus => "Peter test status";

        public IReadOnlyList<string> Segments { get; private set; } =
            Array.Empty<string>();

        public int StartIndex { get; private set; }

        public int CallCount { get; private set; }

        public int RefreshCount { get; private set; }

        public int PreviewCount { get; private set; }

        public string? LastPreviewVoiceId { get; private set; }

        public int LastPreviewVolume { get; private set; }

        public double LastPreviewPitch { get; private set; }

        public Task RefreshVoicesAsync(CancellationToken cancellationToken)
        {
            RefreshCount++;
            return Task.CompletedTask;
        }

        public Task PreviewAsync(
            string voiceId,
            double rate,
            int volume,
            double pitch,
            CancellationToken cancellationToken)
        {
            PreviewCount++;
            LastPreviewVoiceId = voiceId;
            LastPreviewVolume = volume;
            LastPreviewPitch = pitch;
            return Task.CompletedTask;
        }

        public Task SpeakAsync(
            IReadOnlyList<string> segments,
            string voiceId,
            double rate,
            int volume,
            double pitch,
            int startIndex,
            CancellationToken cancellationToken)
        {
            Segments = segments;
            StartIndex = startIndex;
            CallCount++;
            return Task.CompletedTask;
        }

        public void Pause()
        {
        }

        public void Resume()
        {
        }

        public void Stop()
        {
        }
    }

    private sealed class ControllableSpeechGateway :
        ITextToSpeechGateway,
        IProgressTextToSpeechGateway
    {
        private IReadOnlyList<string> _segments = Array.Empty<string>();

        public event EventHandler<TextToSpeechProgress>? ProgressChanged;

        public IReadOnlyList<SpeechVoiceOption> Voices { get; } =
            new[] { CreateTestVoice() };

        public string ProviderStatus => "Test provider";

        public string PeterStatus => "Peter test status";

        public int StopCount { get; private set; }

        public Task RefreshVoicesAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task PreviewAsync(
            string voiceId,
            double rate,
            int volume,
            double pitch,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public async Task SpeakAsync(
            IReadOnlyList<string> segments,
            string voiceId,
            double rate,
            int volume,
            double pitch,
            int startIndex,
            CancellationToken cancellationToken)
        {
            _segments = segments;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        public void RaiseProgress(int segmentIndex)
        {
            ProgressChanged?.Invoke(
                this,
                new TextToSpeechProgress(
                    segmentIndex,
                    _segments.Count,
                    _segments[segmentIndex]));
        }

        public void Pause()
        {
        }

        public void Resume()
        {
        }

        public void Stop()
        {
            StopCount++;
        }
    }

    private static SpeechVoiceOption CreateTestVoice() =>
        new(
            "test:system",
            "System default",
            "en-GB",
            "Test",
            "Current",
            true,
            true,
            true,
            true);

    private sealed class StubDiagnosticsGateway : IDiagnosticsGateway
    {
        public string CreateSnapshot()
        {
            return "OpenShelf test diagnostics";
        }
    }
}
