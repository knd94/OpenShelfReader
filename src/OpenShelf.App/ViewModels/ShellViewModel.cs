using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using OpenShelf.App.Services;

namespace OpenShelf.App.ViewModels;

public sealed class ShellViewModel : ViewModelBase
{
    private readonly AppServices _services;
    private readonly ObservableCollection<BookItemViewModel> _books = new();
    private CancellationTokenSource? _importCancellation;
    private CancellationTokenSource? _speechCancellation;
    private CancellationTokenSource? _progressSaveCancellation;
    private CancellationTokenSource? _settingsSaveCancellation;
    private readonly List<List<ReaderBlockViewModel>> _readerPages = new();
    private readonly List<ReaderSpeechSegment> _speechSegments = new();
    private string _searchText = string.Empty;
    private string? _selectedCategory;
    private bool _favouritesOnly;
    private bool _isReaderOpen;
    private bool _isTocOpen;
    private bool _isAnnotationsOpen;
    private bool _isAppearanceOpen;
    private bool _isHelpOpen;
    private bool _isImportPanelOpen;
    private bool _isImporting;
    private bool _isImportResultsVisible;
    private bool _isAddCategoryOpen;
    private bool _isBookEditorOpen;
    private bool _isNoteComposerOpen;
    private string _newCategoryName = string.Empty;
    private string _editingTitle = string.Empty;
    private string _editingAuthor = string.Empty;
    private string _editingCategory = "Uncategorised";
    private BookItemViewModel? _editingBook;
    private BookItemViewModel? _currentBook;
    private string _readerTitle = "OpenShelf";
    private string _readerAuthor = string.Empty;
    private double _readerFontSize = 19;
    private double _readerLineSpacing = 1.62;
    private double _readerContentWidth = 760;
    private double _readerMargin = 48;
    private double _readerParagraphSpacing = 19;
    private string _selectedTextAlignment = "Start";
    private string _selectedFontFamily = "Segoe UI";
    private string _selectedReaderTheme = "Midnight";
    private IBrush _readerPageBrush = CreateBrush("#171A22");
    private IBrush _readerTextBrush = CreateBrush("#EEF1F7");
    private IBrush _readerCanvasBrush = CreateBrush("#090B10");
    private double _readingProgress;
    private string _selectedReaderText = string.Empty;
    private string _selectedReaderLocator = string.Empty;
    private string _currentReaderLocator = string.Empty;
    private int _selectedReaderStart;
    private int _selectedReaderLength;
    private string _selectedHighlightColor = "Yellow";
    private string _selectedLibrarySort = "Title";
    private string _selectedLibraryTheme = "Dark";
    private string _selectedReaderLayoutMode = "Scroll";
    private int _currentReaderPageIndex;
    private double _readerViewportWidth = 1180;
    private double _readerViewportHeight = 700;
    private double _readerPagedPageWidth = 550;
    private double _readerPagedPageHeight = 648;
    private int _readerPageColumns = 2;
    private bool _settingsLoaded;
    private AnnotationItemViewModel? _editingAnnotation;
    private string _pendingNoteText = string.Empty;
    private string _importStatus = "Choose books or a folder to begin.";
    private double _importProgressValue;
    private bool _isImportIndeterminate;
    private string _importSummary = string.Empty;
    private bool _isPasswordPromptOpen;
    private string _importPassword = string.Empty;
    private string _passwordBookName = string.Empty;
    private string? _passwordRetryToken;
    private bool _isReaderPasswordPromptOpen;
    private string _readerPassword = string.Empty;
    private BookItemViewModel? _pendingPasswordBook;
    private bool _isOpeningBook;
    private string _statusMessage = string.Empty;
    private bool _isStatusVisible;
    private SpeechVoiceOption? _selectedVoice;
    private double _speechRate = 1;
    private int _personalReadingWordsPerMinute = 250;
    private double _speechVolume = 100;
    private double _speechPitch = 1;
    private bool _isRefreshingVoices;
    private bool _isSpeaking;
    private bool _isSpeechPaused;
    private bool _highlightSpokenSentence = true;
    private bool _followSpokenSentence = true;
    private string _speechProgressLabel = string.Empty;
    private int _speechResumeSegmentIndex;
    private string _speechResumeLocator = string.Empty;
    private int _speechResumeCharacterOffset;
    private int _activeSpeechSegmentIndex = -1;
    private bool _isFixedPageDocument;
    private int _fixedPageCount;
    private string _diagnosticsText;

    public ShellViewModel(AppServices services)
    {
        _services = services;
        _selectedVoice = services.Speech.Voices.FirstOrDefault();
        _diagnosticsText = services.Diagnostics.CreateSnapshot();
        if (services.Speech is IProgressTextToSpeechGateway progressSpeech)
        {
            progressSpeech.ProgressChanged += Speech_ProgressChanged;
        }

        OpenBookCommand = new RelayCommand<BookItemViewModel>(book => _ = OpenBookAsync(book));
        ToggleFavouriteCommand = new RelayCommand<BookItemViewModel>(ToggleFavourite);
        ShowAllCommand = new RelayCommand(ShowAll);
        ShowFavouritesCommand = new RelayCommand(ShowFavourites);
        SelectCategoryCommand = new RelayCommand<CategoryItemViewModel>(SelectCategory);
        ClearSearchCommand = new RelayCommand(() => SearchText = string.Empty);
        LoadSampleLibraryCommand = new RelayCommand(LoadSampleLibrary);
        CloseReaderCommand = new RelayCommand(() => _ = CloseReaderAsync());
        PreviousPageCommand = new RelayCommand(
            PreviousReaderPage,
            () => CanGoToPreviousPage);
        NextPageCommand = new RelayCommand(
            NextReaderPage,
            () => CanGoToNextPage);
        ToggleTocCommand = new RelayCommand(() => ToggleReaderPanel(ReaderPanel.TableOfContents));
        ToggleAnnotationsCommand = new RelayCommand(() => ToggleReaderPanel(ReaderPanel.Annotations));
        ToggleAppearanceCommand = new RelayCommand(() => ToggleReaderPanel(ReaderPanel.Appearance));
        ToggleHelpCommand = new RelayCommand(ToggleHelp);
        AddBookmarkCommand = new RelayCommand(AddBookmark, () => CurrentBook is not null);
        HighlightSelectionCommand = new RelayCommand(AddHighlight, () => HasSelection);
        OpenNoteComposerCommand = new RelayCommand(OpenNoteComposer, () => HasSelection);
        OpenEditNoteCommand =
            new RelayCommand<AnnotationItemViewModel>(OpenEditNote);
        SaveNoteCommand = new RelayCommand(SaveNote, () => HasSelection && !string.IsNullOrWhiteSpace(PendingNoteText));
        CancelNoteCommand = new RelayCommand(CancelNote);
        RemoveAnnotationCommand = new RelayCommand<AnnotationItemViewModel>(RemoveAnnotation);
        JumpToTocCommand = new RelayCommand<TocItemViewModel>(JumpToToc);
        JumpToAnnotationCommand = new RelayCommand<AnnotationItemViewModel>(JumpToAnnotation);
        CancelImportCommand = new RelayCommand(CancelImport, () => IsImporting);
        CloseImportCommand = new RelayCommand(CloseImport, () => !IsImporting);
        ShowAddCategoryCommand = new RelayCommand(() => IsAddCategoryOpen = true);
        CancelAddCategoryCommand = new RelayCommand(CancelAddCategory);
        SaveCategoryCommand = new RelayCommand(SaveCategory, () => !string.IsNullOrWhiteSpace(NewCategoryName));
        OpenBookEditorCommand = new RelayCommand<BookItemViewModel>(OpenBookEditor);
        CancelBookEditorCommand = new RelayCommand(CancelBookEditor);
        SaveBookEditorCommand = new RelayCommand(SaveBookEditor, () => _editingBook is not null && !string.IsNullOrWhiteSpace(EditingTitle));
        ToggleSpeechCommand = new RelayCommand(
            () => _ = ToggleSpeechAsync(),
            () => ReaderBlocks.Count > 0 && IsSpeechAvailable);
        StopSpeechCommand = new RelayCommand(StopSpeech, () => IsSpeaking || IsSpeechPaused);
        PreviousSpeechCommand = new RelayCommand(() => _services.Speech.Previous());
        NextSpeechCommand = new RelayCommand(() => _services.Speech.Next());
        RefreshVoicesCommand = new RelayCommand(
            () => _ = RefreshVoicesAsync(),
            () => !IsRefreshingVoices && !IsSpeaking);
        PreviewVoiceCommand = new RelayCommand(
            () => _ = PreviewVoiceAsync(),
            () => SelectedVoice is not null && !IsRefreshingVoices && !IsSpeaking);
        OpenPasswordPromptCommand =
            new RelayCommand<ImportResultItemViewModel>(OpenPasswordPrompt);
        CancelPasswordPromptCommand = new RelayCommand(CancelPasswordPrompt);
        RetryPasswordCommand = new RelayCommand(
            () => _ = RetryPasswordAsync(),
            () =>
                !string.IsNullOrWhiteSpace(_passwordRetryToken)
                && !string.IsNullOrEmpty(ImportPassword)
                && !IsImporting);
        CancelReaderPasswordCommand = new RelayCommand(CancelReaderPassword);
        RetryReaderPasswordCommand = new RelayCommand(
            () => _ = RetryReaderPasswordAsync(),
            () =>
                _pendingPasswordBook is not null
                && !string.IsNullOrEmpty(ReaderPassword)
                && !_isOpeningBook);

        Categories.Add(new CategoryItemViewModel("Uncategorised"));
        RefreshVisibleBooks();
    }

    public event EventHandler<ReaderNavigationRequest>? ReaderNavigationRequested;

    public ObservableCollection<BookItemViewModel> VisibleBooks { get; } = new();

    public ObservableCollection<CategoryItemViewModel> Categories { get; } = new();

    public ObservableCollection<ReaderBlockViewModel> ReaderBlocks { get; } = new();

    public ObservableCollection<ReaderBlockViewModel> DisplayedReaderBlocks { get; } = new();

    public ObservableCollection<ReaderPageViewModel> DisplayedReaderPages { get; } = new();

    public ObservableCollection<TocItemViewModel> TableOfContents { get; } = new();

    public ObservableCollection<AnnotationItemViewModel> Annotations { get; } = new();

    public ObservableCollection<ImportResultItemViewModel> ImportResults { get; } = new();

    public IReadOnlyList<string> ReaderThemes { get; } =
        new[] { "Midnight", "Paper", "Sepia" };

    public IReadOnlyList<string> LibraryThemes { get; } =
        new[] { "Dark", "Light", "System" };

    public IReadOnlyList<string> ReaderLayoutModes { get; } =
        new[] { "Scroll", "Paged" };

    public IReadOnlyList<string> FontFamilies { get; } =
        new[] { "Segoe UI", "Georgia", "Palatino Linotype", "Atkinson Hyperlegible" };

    public IReadOnlyList<string> TextAlignments { get; } =
        new[] { "Start", "Center", "End", "Justify" };

    public IReadOnlyList<string> LibrarySortOptions { get; } =
        new[] { "Title", "Author", "Progress", "Recently added", "Favourites first" };

    public IReadOnlyList<SpeechVoiceOption> VoiceOptions => _services.Speech.Voices;

    public bool IsSpeechAvailable => VoiceOptions.Count > 0;

    public string VoiceProviderStatus => _services.Speech.ProviderStatus;

    public string PeterStatus => _services.Speech.PeterStatus;

    public IReadOnlyList<string> HighlightColors { get; } =
        new[] { "Yellow", "Green", "Blue", "Pink" };

    public IReadOnlyList<string> CategoryNames =>
        Categories.Select(category => category.Name).ToArray();

    public ICommand OpenBookCommand { get; }

    public ICommand ToggleFavouriteCommand { get; }

    public ICommand ShowAllCommand { get; }

    public ICommand ShowFavouritesCommand { get; }

    public ICommand SelectCategoryCommand { get; }

    public ICommand ClearSearchCommand { get; }

    public ICommand LoadSampleLibraryCommand { get; }

    public ICommand CloseReaderCommand { get; }

    public RelayCommand PreviousPageCommand { get; }

    public RelayCommand NextPageCommand { get; }

    public ICommand ToggleTocCommand { get; }

    public ICommand ToggleAnnotationsCommand { get; }

    public ICommand ToggleAppearanceCommand { get; }

    public ICommand ToggleHelpCommand { get; }

    public ICommand AddBookmarkCommand { get; }

    public RelayCommand HighlightSelectionCommand { get; }

    public RelayCommand OpenNoteComposerCommand { get; }

    public ICommand OpenEditNoteCommand { get; }

    public RelayCommand SaveNoteCommand { get; }

    public ICommand CancelNoteCommand { get; }

    public ICommand RemoveAnnotationCommand { get; }

    public ICommand JumpToTocCommand { get; }

    public ICommand JumpToAnnotationCommand { get; }

    public RelayCommand CancelImportCommand { get; }

    public RelayCommand CloseImportCommand { get; }

    public ICommand ShowAddCategoryCommand { get; }

    public ICommand CancelAddCategoryCommand { get; }

    public RelayCommand SaveCategoryCommand { get; }

    public ICommand OpenBookEditorCommand { get; }

    public ICommand CancelBookEditorCommand { get; }

    public RelayCommand SaveBookEditorCommand { get; }

    public RelayCommand ToggleSpeechCommand { get; }

    public RelayCommand StopSpeechCommand { get; }

    public ICommand PreviousSpeechCommand { get; }

    public ICommand NextSpeechCommand { get; }

    public RelayCommand RefreshVoicesCommand { get; }

    public RelayCommand PreviewVoiceCommand { get; }

    public ICommand OpenPasswordPromptCommand { get; }

    public ICommand CancelPasswordPromptCommand { get; }

    public RelayCommand RetryPasswordCommand { get; }

    public ICommand CancelReaderPasswordCommand { get; }

    public RelayCommand RetryReaderPasswordCommand { get; }

    public bool IsPreviewMode => _services.IsPreview;

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                RefreshVisibleBooks();
                OnPropertyChanged(nameof(HasSearchText));
            }
        }
    }

    public bool HasSearchText => !string.IsNullOrEmpty(SearchText);

    public string LibraryHeading =>
        _favouritesOnly
            ? "Favourites"
            : _selectedCategory ?? "Your library";

    public string LibrarySummary =>
        VisibleBooks.Count == 1 ? "1 book" : $"{VisibleBooks.Count} books";

    public bool HasVisibleBooks => VisibleBooks.Count > 0;

    public bool ShowEmptyLibrary => VisibleBooks.Count == 0;

    public bool HasAnyBooks => _books.Count > 0;

    public bool IsLibraryVisible => !IsReaderOpen;

    public string WindowTitle =>
        IsReaderOpen
            ? $"{ReaderTitle} — {ReadingProgress:P0} — OpenShelf Reader"
            : "OpenShelf Reader";

    public bool IsReaderOpen
    {
        get => _isReaderOpen;
        private set
        {
            if (SetProperty(ref _isReaderOpen, value))
            {
                OnPropertyChanged(nameof(IsLibraryVisible));
                OnPropertyChanged(nameof(WindowTitle));
            }
        }
    }

    public BookItemViewModel? CurrentBook
    {
        get => _currentBook;
        private set => SetProperty(ref _currentBook, value);
    }

    public string ReaderTitle
    {
        get => _readerTitle;
        private set
        {
            if (SetProperty(ref _readerTitle, value))
            {
                OnPropertyChanged(nameof(WindowTitle));
            }
        }
    }

    public string ReaderAuthor
    {
        get => _readerAuthor;
        private set => SetProperty(ref _readerAuthor, value);
    }

    public bool IsTocOpen
    {
        get => _isTocOpen;
        set => SetProperty(ref _isTocOpen, value);
    }

    public bool IsAnnotationsOpen
    {
        get => _isAnnotationsOpen;
        set => SetProperty(ref _isAnnotationsOpen, value);
    }

    public bool IsAppearanceOpen
    {
        get => _isAppearanceOpen;
        set => SetProperty(ref _isAppearanceOpen, value);
    }

    public bool IsHelpOpen
    {
        get => _isHelpOpen;
        set => SetProperty(ref _isHelpOpen, value);
    }

    public double ReaderFontSize
    {
        get => _readerFontSize;
        set
        {
            if (SetProperty(ref _readerFontSize, Math.Clamp(value, 13, 34)))
            {
                ApplyTypography();
                ScheduleUiSettingsSave();
            }
        }
    }

    public double ReaderLineSpacing
    {
        get => _readerLineSpacing;
        set
        {
            if (SetProperty(ref _readerLineSpacing, Math.Clamp(value, 1.2, 2.2)))
            {
                ApplyTypography();
                ScheduleUiSettingsSave();
            }
        }
    }

    public double ReaderContentWidth
    {
        get => _readerContentWidth;
        set
        {
            if (SetProperty(ref _readerContentWidth, Math.Clamp(value, 480, 1080)))
            {
                RebuildReaderPages();
                ScheduleUiSettingsSave();
            }
        }
    }

    public string SelectedFontFamily
    {
        get => _selectedFontFamily;
        set
        {
            if (SetProperty(ref _selectedFontFamily, value))
            {
                ScheduleUiSettingsSave();
            }
        }
    }

    public double ReaderMargin
    {
        get => _readerMargin;
        set
        {
            if (SetProperty(ref _readerMargin, Math.Clamp(value, 20, 96)))
            {
                OnPropertyChanged(nameof(ReaderPageThickness));
                RebuildReaderPages();
                ScheduleUiSettingsSave();
            }
        }
    }

    public double ReaderParagraphSpacing
    {
        get => _readerParagraphSpacing;
        set
        {
            if (SetProperty(
                    ref _readerParagraphSpacing,
                    Math.Clamp(value, 6, 40)))
            {
                ApplyTypography();
                ScheduleUiSettingsSave();
            }
        }
    }

    public string SelectedTextAlignment
    {
        get => _selectedTextAlignment;
        set
        {
            if (SetProperty(ref _selectedTextAlignment, value))
            {
                ApplyTypography();
                ScheduleUiSettingsSave();
            }
        }
    }

    public string SelectedLibrarySort
    {
        get => _selectedLibrarySort;
        set
        {
            if (SetProperty(ref _selectedLibrarySort, value))
            {
                RefreshVisibleBooks();
            }
        }
    }

    public Thickness ReaderPageThickness => new(ReaderMargin);

    public double ReaderPagedPageWidth
    {
        get => _readerPagedPageWidth;
        private set => SetProperty(ref _readerPagedPageWidth, value);
    }

    public double ReaderPagedPageHeight
    {
        get => _readerPagedPageHeight;
        private set => SetProperty(ref _readerPagedPageHeight, value);
    }

    public int ReaderPageColumns
    {
        get => _readerPageColumns;
        private set
        {
            if (SetProperty(ref _readerPageColumns, value))
            {
                OnPropertyChanged(nameof(ReaderPageLabel));
                OnPropertyChanged(nameof(CanGoToNextPage));
                NextPageCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string SelectedReaderTheme
    {
        get => _selectedReaderTheme;
        set
        {
            if (SetProperty(ref _selectedReaderTheme, value))
            {
                ApplyReaderTheme();
                ScheduleUiSettingsSave();
            }
        }
    }

    public string SelectedLibraryTheme
    {
        get => _selectedLibraryTheme;
        set
        {
            if (SetProperty(ref _selectedLibraryTheme, value))
            {
                ApplyLibraryTheme();
                ScheduleUiSettingsSave();
            }
        }
    }

    public string SelectedReaderLayoutMode
    {
        get => _selectedReaderLayoutMode;
        set
        {
            if (SetProperty(ref _selectedReaderLayoutMode, value))
            {
                OnPropertyChanged(nameof(IsPagedMode));
                OnPropertyChanged(nameof(IsScrollMode));
                UpdateReaderPageMetrics();
                RebuildReaderPages();
                ScheduleUiSettingsSave();
            }
        }
    }

    public bool IsPagedMode =>
        string.Equals(
            SelectedReaderLayoutMode,
            "Paged",
            StringComparison.OrdinalIgnoreCase);

    public bool IsScrollMode => !IsPagedMode;

    public bool IsFixedPageDocument => _isFixedPageDocument;

    public int FixedPageCount => _fixedPageCount;

    public int CurrentReaderPageIndex
    {
        get => _currentReaderPageIndex;
        private set
        {
            if (SetProperty(ref _currentReaderPageIndex, value))
            {
                OnPropertyChanged(nameof(ReaderPageLabel));
                OnPropertyChanged(nameof(CanGoToPreviousPage));
                OnPropertyChanged(nameof(CanGoToNextPage));
                PreviousPageCommand.RaiseCanExecuteChanged();
                NextPageCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public int ReaderPageCount => _readerPages.Count;

    public string ReaderPageLabel =>
        ReaderPageCount == 0
            ? "0 / 0"
            : ReaderPageColumns > 1
              && CurrentReaderPageIndex + 1 < ReaderPageCount
                ? $"{CurrentReaderPageIndex + 1}-{Math.Min(CurrentReaderPageIndex + ReaderPageColumns, ReaderPageCount)} / {ReaderPageCount}"
                : $"{CurrentReaderPageIndex + 1} / {ReaderPageCount}";

    public bool CanGoToPreviousPage =>
        IsPagedMode && CurrentReaderPageIndex > 0;

    public bool CanGoToNextPage =>
        IsPagedMode && CurrentReaderPageIndex + ReaderPageColumns < ReaderPageCount;

    public IBrush ReaderPageBrush
    {
        get => _readerPageBrush;
        private set => SetProperty(ref _readerPageBrush, value);
    }

    public IBrush ReaderTextBrush
    {
        get => _readerTextBrush;
        private set => SetProperty(ref _readerTextBrush, value);
    }

    public IBrush ReaderCanvasBrush
    {
        get => _readerCanvasBrush;
        private set => SetProperty(ref _readerCanvasBrush, value);
    }

    public double ReadingProgress
    {
        get => _readingProgress;
        set
        {
            if (SetProperty(ref _readingProgress, Math.Clamp(value, 0, 1)))
            {
                OnPropertyChanged(nameof(ReadingProgressPercent));
                OnPropertyChanged(nameof(ReadingProgressLabel));
                OnPropertyChanged(nameof(ReadingTimeRemainingLabel));
                OnPropertyChanged(nameof(ReadingProgressSummary));
                OnPropertyChanged(nameof(WindowTitle));

                if (CurrentBook is not null)
                {
                    CurrentBook.Progress = ReadingProgress;
                }
            }
        }
    }

    public double ReadingProgressPercent => ReadingProgress * 100;

    public string ReadingProgressLabel => $"{ReadingProgress:P0} read";

    public string ReadingTimeRemainingLabel =>
        CurrentBook?.TimeRemainingLabel ?? "Time estimate unavailable";

    public string ReadingProgressSummary =>
        $"{ReadingProgressLabel} · {ReadingTimeRemainingLabel}";

    public string SelectedReaderText
    {
        get => _selectedReaderText;
        private set
        {
            if (SetProperty(ref _selectedReaderText, value))
            {
                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(SelectionPreview));
                HighlightSelectionCommand.RaiseCanExecuteChanged();
                OpenNoteComposerCommand.RaiseCanExecuteChanged();
                SaveNoteCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasSelection => !string.IsNullOrWhiteSpace(SelectedReaderText);

    public string SelectionPreview =>
        SelectedReaderText.Length > 160
            ? SelectedReaderText[..160] + "…"
            : SelectedReaderText;

    public string SelectedHighlightColor
    {
        get => _selectedHighlightColor;
        set => SetProperty(ref _selectedHighlightColor, value);
    }

    public string PendingNoteText
    {
        get => _pendingNoteText;
        set
        {
            if (SetProperty(ref _pendingNoteText, value))
            {
                SaveNoteCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsNoteComposerOpen
    {
        get => _isNoteComposerOpen;
        private set => SetProperty(ref _isNoteComposerOpen, value);
    }

    public bool HasAnnotations => Annotations.Count > 0;

    public bool IsImportPanelOpen
    {
        get => _isImportPanelOpen;
        set => SetProperty(ref _isImportPanelOpen, value);
    }

    public bool IsImporting
    {
        get => _isImporting;
        private set
        {
            if (SetProperty(ref _isImporting, value))
            {
                CancelImportCommand.RaiseCanExecuteChanged();
                CloseImportCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsImportResultsVisible
    {
        get => _isImportResultsVisible;
        private set => SetProperty(ref _isImportResultsVisible, value);
    }

    public string ImportStatus
    {
        get => _importStatus;
        private set => SetProperty(ref _importStatus, value);
    }

    public double ImportProgressValue
    {
        get => _importProgressValue;
        private set => SetProperty(ref _importProgressValue, value);
    }

    public bool IsImportIndeterminate
    {
        get => _isImportIndeterminate;
        private set => SetProperty(ref _isImportIndeterminate, value);
    }

    public string ImportSummary
    {
        get => _importSummary;
        private set => SetProperty(ref _importSummary, value);
    }

    public bool IsPasswordPromptOpen
    {
        get => _isPasswordPromptOpen;
        private set => SetProperty(ref _isPasswordPromptOpen, value);
    }

    public string ImportPassword
    {
        get => _importPassword;
        set
        {
            if (SetProperty(ref _importPassword, value))
            {
                RetryPasswordCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string PasswordBookName
    {
        get => _passwordBookName;
        private set => SetProperty(ref _passwordBookName, value);
    }

    public bool IsReaderPasswordPromptOpen
    {
        get => _isReaderPasswordPromptOpen;
        private set => SetProperty(ref _isReaderPasswordPromptOpen, value);
    }

    public string ReaderPassword
    {
        get => _readerPassword;
        set
        {
            if (SetProperty(ref _readerPassword, value))
            {
                RetryReaderPasswordCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string ReaderPasswordBookName =>
        _pendingPasswordBook?.Title ?? "Protected book";

    public bool IsAddCategoryOpen
    {
        get => _isAddCategoryOpen;
        private set => SetProperty(ref _isAddCategoryOpen, value);
    }

    public string NewCategoryName
    {
        get => _newCategoryName;
        set
        {
            if (SetProperty(ref _newCategoryName, value))
            {
                SaveCategoryCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsBookEditorOpen
    {
        get => _isBookEditorOpen;
        private set => SetProperty(ref _isBookEditorOpen, value);
    }

    public string EditingTitle
    {
        get => _editingTitle;
        set
        {
            if (SetProperty(ref _editingTitle, value))
            {
                SaveBookEditorCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string EditingAuthor
    {
        get => _editingAuthor;
        set => SetProperty(ref _editingAuthor, value);
    }

    public string EditingCategory
    {
        get => _editingCategory;
        set => SetProperty(ref _editingCategory, value);
    }

    public SpeechVoiceOption? SelectedVoice
    {
        get => _selectedVoice;
        set
        {
            if (SetProperty(ref _selectedVoice, value))
            {
                PreviewVoiceCommand.RaiseCanExecuteChanged();
                ScheduleUiSettingsSave();
            }
        }
    }

    public double SpeechRate
    {
        get => _speechRate;
        set
        {
            if (SetProperty(ref _speechRate, Math.Clamp(value, 0.5, 2)))
            {
                UpdateBookTimeEstimateSpeeds();
                ScheduleUiSettingsSave();
            }
        }
    }

    public int PersonalReadingWordsPerMinute
    {
        get => _personalReadingWordsPerMinute;
        set
        {
            if (SetProperty(
                    ref _personalReadingWordsPerMinute,
                    Math.Clamp(value, 100, 1000)))
            {
                UpdateBookTimeEstimateSpeeds();
                ScheduleUiSettingsSave();
            }
        }
    }

    public double SpeechVolume
    {
        get => _speechVolume;
        set
        {
            if (SetProperty(ref _speechVolume, Math.Clamp(value, 0, 100)))
            {
                ScheduleUiSettingsSave();
            }
        }
    }

    public double SpeechPitch
    {
        get => _speechPitch;
        set
        {
            if (SetProperty(ref _speechPitch, Math.Clamp(value, 0.5, 2)))
            {
                ScheduleUiSettingsSave();
            }
        }
    }

    public bool IsRefreshingVoices
    {
        get => _isRefreshingVoices;
        private set
        {
            if (SetProperty(ref _isRefreshingVoices, value))
            {
                RefreshVoicesCommand.RaiseCanExecuteChanged();
                PreviewVoiceCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HighlightSpokenSentence
    {
        get => _highlightSpokenSentence;
        set
        {
            if (!SetProperty(ref _highlightSpokenSentence, value))
            {
                return;
            }

            if (value && IsSpeaking)
            {
                ApplySpokenHighlight();
            }
            else
            {
                ClearSpokenHighlight();
            }

            ScheduleUiSettingsSave();
        }
    }

    public bool FollowSpokenSentence
    {
        get => _followSpokenSentence;
        set
        {
            if (SetProperty(ref _followSpokenSentence, value))
            {
                if (value && IsSpeaking)
                {
                    FollowActiveSpeechSegment();
                }

                ScheduleUiSettingsSave();
            }
        }
    }

    public bool IsSpeaking
    {
        get => _isSpeaking;
        private set
        {
            if (SetProperty(ref _isSpeaking, value))
            {
                OnPropertyChanged(nameof(SpeechGlyph));
                OnPropertyChanged(nameof(SpeechActionLabel));
                OnPropertyChanged(nameof(SpeechStatus));
                ToggleSpeechCommand.RaiseCanExecuteChanged();
                StopSpeechCommand.RaiseCanExecuteChanged();
                RefreshVoicesCommand.RaiseCanExecuteChanged();
                PreviewVoiceCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsSpeechPaused
    {
        get => _isSpeechPaused;
        private set
        {
            if (SetProperty(ref _isSpeechPaused, value))
            {
                OnPropertyChanged(nameof(SpeechGlyph));
                OnPropertyChanged(nameof(SpeechActionLabel));
                OnPropertyChanged(nameof(SpeechStatus));
                StopSpeechCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string SpeechGlyph => IsSpeaking && !IsSpeechPaused ? "Ⅱ" : "▶";

    public string SpeechActionLabel =>
        !IsSpeechAvailable
            ? "Unavailable"
            : IsSpeaking
            ? IsSpeechPaused ? "Resume" : "Pause"
            : "Read";

    public string SpeechStatus =>
        !IsSpeechAvailable
            ? "Text to speech unavailable"
            : IsSpeechPaused
                ? "Paused"
                : IsSpeaking ? "Reading aloud" : "Text to speech";

    public string SpeechProgressLabel
    {
        get => _speechProgressLabel;
        private set => SetProperty(ref _speechProgressLabel, value);
    }

    public string DiagnosticsText
    {
        get => _diagnosticsText;
        private set => SetProperty(ref _diagnosticsText, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public bool IsStatusVisible
    {
        get => _isStatusVisible;
        private set => SetProperty(ref _isStatusVisible, value);
    }

    public async Task ImportAsync(IReadOnlyList<string> selectedPaths)
    {
        if (selectedPaths.Count == 0 || IsImporting)
        {
            return;
        }

        _importCancellation = new CancellationTokenSource();
        ImportResults.Clear();
        ImportSummary = string.Empty;
        ImportStatus = "Preparing import…";
        ImportProgressValue = 0;
        IsImportIndeterminate = true;
        IsImportResultsVisible = false;
        IsImportPanelOpen = true;
        IsImporting = true;

        var progress = new Progress<ImportProgress>(UpdateImportProgress);

        try
        {
            var batch = await _services.Library.ImportAsync(
                selectedPaths,
                progress,
                _importCancellation.Token);

            foreach (var descriptor in batch.ImportedBooks)
            {
                _books.Add(CreateBookItem(descriptor));
            }

            foreach (var result in batch.Results)
            {
                ImportResults.Add(result);
            }

            ImportSummary =
                $"{batch.ImportedBooks.Count} imported · {batch.Skipped} skipped · " +
                $"{batch.Results.Count(result => !result.Succeeded)} need attention";
            ImportStatus = batch.WasCancelled ? "Import cancelled" : "Import complete";
            IsImportResultsVisible = true;
            RebuildCategories();
            RefreshVisibleBooks();

            var passwordRequired = batch.Results.FirstOrDefault(
                result => result.RequiresPassword);
            if (passwordRequired is not null)
            {
                OpenPasswordPrompt(passwordRequired);
            }
        }
        catch (OperationCanceledException)
        {
            ImportStatus = "Import cancelled";
            ImportSummary = "No partially imported item was added.";
            IsImportResultsVisible = true;
        }
        catch (Exception exception)
        {
            ImportStatus = "Import could not finish";
            ImportSummary = exception.Message;
            IsImportResultsVisible = true;
        }
        finally
        {
            IsImportIndeterminate = false;
            IsImporting = false;
            _importCancellation.Dispose();
            _importCancellation = null;
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _services.Library
            .LoadUiSettingsAsync(cancellationToken);
        var books = await _services.Library
            .LoadLibraryAsync(cancellationToken);
        var categoryNames = await _services.Library
            .LoadCategoriesAsync(cancellationToken);

        ApplyUiSettings(settings);

        _books.Clear();
        foreach (var descriptor in books)
        {
            _books.Add(CreateBookItem(descriptor));
        }

        Categories.Clear();
        foreach (var categoryName in categoryNames)
        {
            Categories.Add(new CategoryItemViewModel(categoryName));
        }

        if (!Categories.Any(category =>
                string.Equals(
                    category.Name,
                    "Uncategorised",
                    StringComparison.CurrentCultureIgnoreCase)))
        {
            Categories.Add(new CategoryItemViewModel("Uncategorised"));
        }

        RebuildCategories();
        RefreshVisibleBooks();
    }

    public void OpenImportPanel()
    {
        IsImportPanelOpen = true;
        IsImportResultsVisible = false;
        ImportStatus = "Choose books or a folder to begin.";
        ImportSummary = "EPUB, PDF, MOBI, AZW3, FB2, TXT, RTF, and DOCX";
    }

    public void UpdateReaderSelection(
        string text,
        string locator,
        int startOffset,
        int length)
    {
        SelectedReaderText = text.Trim();
        _selectedReaderLocator = locator;
        _selectedReaderStart = Math.Max(0, startOffset);
        _selectedReaderLength = Math.Max(0, length);
    }

    public void ClearReaderSelection()
    {
        SelectedReaderText = string.Empty;
        _selectedReaderLocator = string.Empty;
        _selectedReaderStart = 0;
        _selectedReaderLength = 0;
    }

    public void NotifyCopyCompleted()
    {
        ShowStatus("Copied selected text");
    }

    public Task StartSpeechAtAsync(string locator, int characterOffset)
    {
        var segmentIndex = FindSpeechSegmentIndex(locator, characterOffset);
        if (segmentIndex < 0)
        {
            ShowStatus("There is no readable sentence at this location");
            return Task.CompletedTask;
        }

        return StartSpeechAsync(segmentIndex);
    }

    public void DismissStatus()
    {
        IsStatusVisible = false;
    }

    public void ReportStateSaveFailure(string message)
    {
        ShowStatus(
            $"OpenShelf stayed open because progress could not be saved: {message}");
    }

    public void SetReadingProgress(double progress)
    {
        ReadingProgress = progress;
        ScheduleReadingProgressSave();
    }

    public void UpdateReaderViewport(double width, double height)
    {
        if (!double.IsFinite(width)
            || !double.IsFinite(height)
            || width <= 0
            || height <= 0)
        {
            return;
        }

        var widthChanged = Math.Abs(_readerViewportWidth - width) >= 8;
        var heightChanged = Math.Abs(_readerViewportHeight - height) >= 8;
        if (!widthChanged && !heightChanged)
        {
            return;
        }

        _readerViewportWidth = width;
        _readerViewportHeight = height;
        var metricsChanged = UpdateReaderPageMetrics();
        if (IsPagedMode && (widthChanged || heightChanged || metricsChanged))
        {
            RebuildReaderPages();
        }
    }

    public void SetReaderPosition(string locator, double withinBlock)
    {
        if (string.IsNullOrWhiteSpace(locator))
        {
            return;
        }

        if (IsFixedPageDocument)
        {
            if (!ReaderPositionLocator.TryParsePdf(
                    locator,
                    out var pageIndex,
                    out var encodedWithinPage))
            {
                return;
            }

            var withinPage = locator.Contains('@', StringComparison.Ordinal)
                ? encodedWithinPage
                : Math.Clamp(withinBlock, 0, 1);
            pageIndex = Math.Clamp(
                pageIndex,
                0,
                Math.Max(0, FixedPageCount - 1));
            _currentReaderLocator = ReaderPositionLocator.FormatPdf(
                pageIndex,
                withinPage);
            ReadingProgress = FixedPageCount <= 0
                ? 0
                : Math.Clamp(
                    (pageIndex + withinPage) / FixedPageCount,
                    0,
                    1);
            ScheduleReadingProgressSave();
            return;
        }

        if (!ReaderPositionLocator.TryParseReflowable(
                locator,
                out var baseLocator,
                out var encodedOffset))
        {
            return;
        }

        var block = ReaderBlocks.FirstOrDefault(
            item => string.Equals(
                item.Locator,
                baseLocator,
                StringComparison.Ordinal));
        if (block is null)
        {
            _currentReaderLocator = locator;
            return;
        }

        var characterOffset = locator.Contains('#', StringComparison.Ordinal)
            ? Math.Clamp(encodedOffset, 0, (int)Math.Min(int.MaxValue, block.NormalizedLength))
            : (int)Math.Round(
                Math.Clamp(withinBlock, 0, 1) * block.NormalizedLength);
        _currentReaderLocator = ReaderPositionLocator.FormatReflowable(
            baseLocator,
            characterOffset);
        ReadingProgress = CalculateNormalizedProgress(block, characterOffset);
        ScheduleReadingProgressSave();
    }

    public double GetReaderPositionFraction(string locator)
    {
        if (IsFixedPageDocument
            && ReaderPositionLocator.TryParsePdf(locator, out _, out var withinPage))
        {
            return withinPage;
        }

        if (!ReaderPositionLocator.TryParseReflowable(
                locator,
                out var baseLocator,
                out var characterOffset))
        {
            return 0;
        }

        var block = ReaderBlocks.FirstOrDefault(
            item => string.Equals(
                item.Locator,
                baseLocator,
                StringComparison.Ordinal));
        return block is null
            ? 0
            : Math.Clamp(
                (double)characterOffset / block.NormalizedLength,
                0,
                1);
    }

    public string GetReaderLocatorAtProgress(double progress)
    {
        var normalizedProgress = Math.Clamp(progress, 0, 1);
        if (IsFixedPageDocument)
        {
            if (FixedPageCount <= 0)
            {
                return string.Empty;
            }

            var absolutePosition = normalizedProgress * FixedPageCount;
            var pageIndex = Math.Min(
                FixedPageCount - 1,
                (int)Math.Floor(absolutePosition));
            var withinPage = normalizedProgress >= 1
                ? 1
                : absolutePosition - pageIndex;
            return ReaderPositionLocator.FormatPdf(pageIndex, withinPage);
        }

        var totalLength = ReaderBlocks.Sum(block => block.NormalizedLength);
        if (totalLength <= 0)
        {
            return string.Empty;
        }

        var absoluteOffset = normalizedProgress * totalLength;
        long precedingLength = 0;
        foreach (var block in ReaderBlocks)
        {
            var blockEnd = precedingLength + block.NormalizedLength;
            if (absoluteOffset <= blockEnd || ReferenceEquals(block, ReaderBlocks[^1]))
            {
                var characterOffset = (int)Math.Clamp(
                    Math.Round(absoluteOffset - precedingLength),
                    0,
                    Math.Min(int.MaxValue, block.NormalizedLength));
                return ReaderPositionLocator.FormatReflowable(
                    block.Locator,
                    characterOffset);
            }

            precedingLength = blockEnd;
        }

        return string.Empty;
    }

    public void SetCurrentReaderLocator(string locator)
    {
        if (!string.IsNullOrWhiteSpace(locator))
        {
            _currentReaderLocator = locator;
        }
    }

    public async Task FlushReadingProgressAsync(
        CancellationToken cancellationToken = default)
    {
        var pendingSave = Interlocked.Exchange(
            ref _progressSaveCancellation,
            null);
        CancelPendingSave(pendingSave);
        if (CurrentBook is null)
        {
            return;
        }

        await SaveReadingProgressCoreAsync(cancellationToken);
    }

    public async Task FlushUiSettingsAsync(
        CancellationToken cancellationToken = default)
    {
        var pendingSave = Interlocked.Exchange(
            ref _settingsSaveCancellation,
            null);
        CancelPendingSave(pendingSave);
        if (!_settingsLoaded)
        {
            return;
        }

        await SaveUiSettingsCoreAsync(cancellationToken);
    }

    public Task FlushSpeechPositionAsync(
        CancellationToken cancellationToken = default)
    {
        return SaveSpeechPositionCoreAsync(cancellationToken);
    }

    public async Task FlushAllStateAsync(
        CancellationToken cancellationToken = default)
    {
        await Task.WhenAll(
                FlushReadingProgressAsync(cancellationToken),
                FlushUiSettingsAsync(cancellationToken),
                FlushSpeechPositionAsync(cancellationToken));
    }

    private async Task SaveReadingProgressCoreAsync(
        CancellationToken cancellationToken)
    {
        if (CurrentBook is null)
        {
            return;
        }

        CurrentBook.ReadingLocator = _currentReaderLocator;
        await _services.Library
            .SaveReadingProgressAsync(
                CurrentBook.Id,
                ReadingProgress,
                _currentReaderLocator,
                cancellationToken);
    }

    private Task SaveSpeechPositionCoreAsync(CancellationToken cancellationToken)
    {
        if (CurrentBook is null || _speechSegments.Count == 0)
        {
            return Task.CompletedTask;
        }

        CurrentBook.SpeechLocator = _speechResumeLocator;
        CurrentBook.SpeechSentenceIndex = _speechResumeSegmentIndex;
        CurrentBook.SpeechCharacterOffset = _speechResumeCharacterOffset;
        return _services.Library.SaveSpeechPositionAsync(
            CurrentBook.Id,
            _speechResumeLocator,
            _speechResumeSegmentIndex,
            _speechResumeCharacterOffset,
            cancellationToken);
    }

    public void RefreshDiagnostics()
    {
        DiagnosticsText = _services.Diagnostics.CreateSnapshot();
    }

    private async Task OpenBookAsync(BookItemViewModel? book)
    {
        _ = await OpenBookCoreAsync(book, password: null);
    }

    private async Task<bool> OpenBookCoreAsync(
        BookItemViewModel? book,
        string? password)
    {
        if (book is null || _isOpeningBook)
        {
            return false;
        }

        _isOpeningBook = true;
        RetryReaderPasswordCommand.RaiseCanExecuteChanged();
        try
        {
            if (CurrentBook is not null)
            {
                StopSpeech();
                try
                {
                    await Task.WhenAll(
                        FlushReadingProgressAsync(),
                        FlushSpeechPositionAsync());
                }
                catch (Exception exception)
                {
                    ShowStatus(
                        $"The previous book's position could not be saved: {exception.Message}");
                }

                CurrentBook = null;
            }

            ReaderTitle = book.Title;
            ReaderAuthor = book.Author;
            ReadingProgress = book.Progress;
            _currentReaderLocator = book.ReadingLocator;
            ReaderBlocks.Clear();
            DisplayedReaderBlocks.Clear();
            _readerPages.Clear();
            _speechSegments.Clear();
            _activeSpeechSegmentIndex = -1;
            ClearSpokenHighlight();
            TableOfContents.Clear();
            ClearReaderSelection();

            var descriptor = new BookDescriptor(
                book.Id,
                book.Title,
                book.Author,
                book.Format,
                book.SourcePath,
                book.SourceFolder,
                book.Category,
                book.Cover,
                book.Progress,
                book.IsFavorite,
                book.ReadingLocator,
                book.ImportedAt,
                book.SpeechLocator,
                book.SpeechSentenceIndex,
                book.SpeechCharacterOffset,
                book.EstimatedWordCount);

            var document = password is null
                ? await _services.Reader
                    .OpenAsync(descriptor, CancellationToken.None)
                : await _services.Reader
                    .OpenWithPasswordAsync(
                        descriptor,
                        password,
                        CancellationToken.None);

            book.EstimatedWordCount = document.EstimatedWordCount;
            CurrentBook = book;
            OnPropertyChanged(nameof(ReadingTimeRemainingLabel));
            OnPropertyChanged(nameof(ReadingProgressSummary));
            _isFixedPageDocument = document.IsFixedPage;
            _fixedPageCount = document.PageCount;
            OnPropertyChanged(nameof(IsFixedPageDocument));
            OnPropertyChanged(nameof(FixedPageCount));
            foreach (var block in document.Blocks)
            {
                ReaderBlocks.Add(block);
            }

            BuildSpeechSegments();
            ToggleSpeechCommand.RaiseCanExecuteChanged();
            _speechResumeSegmentIndex = ResolveSpeechResumeIndex(book);
            if (_speechSegments.Count > 0)
            {
                var resume = _speechSegments[_speechResumeSegmentIndex];
                _speechResumeLocator = resume.Locator;
                _speechResumeCharacterOffset = resume.CharacterOffset;
            }
            else
            {
                _speechResumeLocator = string.Empty;
                _speechResumeCharacterOffset = 0;
            }

            foreach (var item in document.TableOfContents)
            {
                TableOfContents.Add(item);
            }

            Annotations.Clear();
            var storedAnnotations = await _services.Library
                .LoadAnnotationsAsync(book.Id);
            foreach (var annotation in storedAnnotations)
            {
                Annotations.Add(annotation);
            }

            AnnotationCollectionChanged();
            ApplyTypography();
            if (string.IsNullOrWhiteSpace(_currentReaderLocator))
            {
                _currentReaderLocator =
                    ReaderBlocks.FirstOrDefault()?.Locator ?? string.Empty;
            }

            RebuildReaderPages();
            IsReaderOpen = true;
            ReaderNavigationRequested?.Invoke(
                this,
                new ReaderNavigationRequest(
                    _currentReaderLocator,
                    ReadingProgress));
            return true;
        }
        catch (ReaderPasswordRequiredException exception)
        {
            _pendingPasswordBook = book;
            ReaderPassword = string.Empty;
            OnPropertyChanged(nameof(ReaderPasswordBookName));
            IsReaderPasswordPromptOpen = true;
            ShowStatus(exception.Message);
            return false;
        }
        catch (Exception exception)
        {
            ShowStatus($"Could not open book: {exception.Message}");
            return false;
        }
        finally
        {
            _isOpeningBook = false;
            RetryReaderPasswordCommand.RaiseCanExecuteChanged();
        }
    }

    private async Task RetryReaderPasswordAsync()
    {
        if (_pendingPasswordBook is null
            || string.IsNullOrEmpty(ReaderPassword)
            || _isOpeningBook)
        {
            return;
        }

        var book = _pendingPasswordBook;
        var password = ReaderPassword;
        try
        {
            var opened = await OpenBookCoreAsync(book, password);
            if (opened)
            {
                _pendingPasswordBook = null;
                IsReaderPasswordPromptOpen = false;
                OnPropertyChanged(nameof(ReaderPasswordBookName));
            }
        }
        finally
        {
            password = string.Empty;
            ReaderPassword = string.Empty;
        }
    }

    private void CancelReaderPassword()
    {
        ReaderPassword = string.Empty;
        _pendingPasswordBook = null;
        IsReaderPasswordPromptOpen = false;
        OnPropertyChanged(nameof(ReaderPasswordBookName));
        RetryReaderPasswordCommand.RaiseCanExecuteChanged();
    }

    private async Task CloseReaderAsync()
    {
        StopSpeech();
        IsReaderOpen = false;
        IsTocOpen = false;
        IsAnnotationsOpen = false;
        IsAppearanceOpen = false;
        ClearReaderSelection();

        try
        {
            await FlushAllStateAsync();
        }
        catch (Exception exception)
        {
            ShowStatus($"Reading progress could not be saved: {exception.Message}");
        }
        finally
        {
            CurrentBook = null;
            RefreshVisibleBooks();
        }
    }

    private void ToggleFavourite(BookItemViewModel? book)
    {
        if (book is null)
        {
            return;
        }

        book.IsFavorite = !book.IsFavorite;
        _ = PersistFavoriteAsync(book);
        if (_favouritesOnly)
        {
            RefreshVisibleBooks();
        }
    }

    private void ShowAll()
    {
        _favouritesOnly = false;
        _selectedCategory = null;
        RefreshVisibleBooks();
    }

    private void ShowFavourites()
    {
        _favouritesOnly = true;
        _selectedCategory = null;
        RefreshVisibleBooks();
    }

    private void SelectCategory(CategoryItemViewModel? category)
    {
        if (category is null)
        {
            return;
        }

        _favouritesOnly = false;
        _selectedCategory = category.Name;
        RefreshVisibleBooks();
    }

    private void RefreshVisibleBooks()
    {
        var query = _books.AsEnumerable();

        if (_favouritesOnly)
        {
            query = query.Where(book => book.IsFavorite);
        }

        if (!string.IsNullOrWhiteSpace(_selectedCategory))
        {
            query = query.Where(
                book => string.Equals(
                    book.Category,
                    _selectedCategory,
                    StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            query = query.Where(
                book =>
                    book.Title.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase)
                    || book.Author.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase)
                    || book.Format.Contains(SearchText, StringComparison.OrdinalIgnoreCase));
        }

        VisibleBooks.Clear();
        query = SelectedLibrarySort switch
        {
            "Author" => query
                .OrderBy(book => book.Author, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(book => book.Title, StringComparer.CurrentCultureIgnoreCase),
            "Progress" => query
                .OrderByDescending(book => book.Progress)
                .ThenBy(book => book.Title, StringComparer.CurrentCultureIgnoreCase),
            "Recently added" => query
                .OrderByDescending(book => book.ImportedAt)
                .ThenBy(book => book.Title, StringComparer.CurrentCultureIgnoreCase),
            "Favourites first" => query
                .OrderByDescending(book => book.IsFavorite)
                .ThenBy(book => book.Title, StringComparer.CurrentCultureIgnoreCase),
            _ => query.OrderBy(
                book => book.Title,
                StringComparer.CurrentCultureIgnoreCase)
        };

        foreach (var book in query)
        {
            VisibleBooks.Add(book);
        }

        OnPropertyChanged(nameof(LibraryHeading));
        OnPropertyChanged(nameof(LibrarySummary));
        OnPropertyChanged(nameof(HasVisibleBooks));
        OnPropertyChanged(nameof(ShowEmptyLibrary));
        OnPropertyChanged(nameof(HasAnyBooks));
    }

    private void LoadSampleLibrary()
    {
        if (_books.Count > 0)
        {
            ShowAll();
            return;
        }

        var samples = new[]
        {
            new BookDescriptor(Guid.NewGuid(), "The Quiet Geometry", "Mara Voss", "EPUB", "", "Samples", "Essays", null, 0.32, true, EstimatedWordCount: 62000),
            new BookDescriptor(Guid.NewGuid(), "Orbits of Dust", "Ilya North", "EPUB", "", "Samples", "Fiction", null, EstimatedWordCount: 84000),
            new BookDescriptor(Guid.NewGuid(), "Under Copper Skies", "Nadia Bell", "PDF", "", "Samples", "Fiction", null, 1, EstimatedWordCount: 71000),
            new BookDescriptor(Guid.NewGuid(), "Field Notes at Dusk", "Emil Rowan", "FB2", "", "Samples", "Essays", null, 0.68, EstimatedWordCount: 48000),
            new BookDescriptor(Guid.NewGuid(), "A Map of Small Things", "Lin Okafor", "MOBI", "", "Samples", "Reference", null, EstimatedWordCount: 56000),
            new BookDescriptor(Guid.NewGuid(), "After the Last Train", "S. K. Vale", "DOCX", "", "Samples", "Fiction", null, 0.12, EstimatedWordCount: 39000)
        };

        foreach (var descriptor in samples)
        {
            _books.Add(CreateBookItem(descriptor));
        }

        RebuildCategories();
        RefreshVisibleBooks();
    }

    private void RebuildCategories()
    {
        var categoryNames = Categories
            .Select(category => category.Name)
            .Concat(_books.Select(book => book.Category))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        Categories.Clear();
        foreach (var categoryName in categoryNames)
        {
            Categories.Add(
                new CategoryItemViewModel(
                    categoryName,
                    _books.Count(
                        book => string.Equals(
                            book.Category,
                            categoryName,
                            StringComparison.CurrentCultureIgnoreCase))));
        }

        OnPropertyChanged(nameof(CategoryNames));
    }

    private BookItemViewModel CreateBookItem(BookDescriptor descriptor)
    {
        var book = new BookItemViewModel(descriptor);
        book.SetTimeEstimateSpeeds(
            PersonalReadingWordsPerMinute,
            GetSpeechWordsPerMinute());
        return book;
    }

    private void UpdateBookTimeEstimateSpeeds()
    {
        var speechWordsPerMinute = GetSpeechWordsPerMinute();
        foreach (var book in _books)
        {
            book.SetTimeEstimateSpeeds(
                PersonalReadingWordsPerMinute,
                speechWordsPerMinute);
        }

        OnPropertyChanged(nameof(ReadingTimeRemainingLabel));
        OnPropertyChanged(nameof(ReadingProgressSummary));
    }

    private int GetSpeechWordsPerMinute() =>
        Math.Clamp((int)Math.Round(175 * SpeechRate), 80, 450);

    private void ToggleReaderPanel(ReaderPanel panel)
    {
        var tocWasOpen = IsTocOpen;
        var annotationsWasOpen = IsAnnotationsOpen;
        var appearanceWasOpen = IsAppearanceOpen;

        IsTocOpen = false;
        IsAnnotationsOpen = false;
        IsAppearanceOpen = false;
        IsHelpOpen = false;

        switch (panel)
        {
            case ReaderPanel.TableOfContents:
                IsTocOpen = !tocWasOpen;
                break;
            case ReaderPanel.Annotations:
                IsAnnotationsOpen = !annotationsWasOpen;
                break;
            case ReaderPanel.Appearance:
                IsAppearanceOpen = !appearanceWasOpen;
                break;
        }
    }

    private void ToggleHelp()
    {
        RefreshDiagnostics();
        IsHelpOpen = !IsHelpOpen;
    }

    private void ApplyTypography()
    {
        foreach (var block in ReaderBlocks)
        {
            switch (block)
            {
                case ReaderHeadingBlockViewModel heading:
                    heading.FontSize = ReaderFontSize + 13;
                    break;
                case ReaderParagraphBlockViewModel paragraph:
                    paragraph.FontSize = ReaderFontSize;
                    paragraph.LineHeight = ReaderFontSize * ReaderLineSpacing;
                    paragraph.ParagraphMargin =
                        new Thickness(0, 0, 0, ReaderParagraphSpacing);
                    paragraph.TextAlignment = SelectedTextAlignment switch
                    {
                        "Center" => TextAlignment.Center,
                        "End" => TextAlignment.Right,
                        "Justify" => TextAlignment.Justify,
                        _ => TextAlignment.Left
                    };
                    break;
            }
        }

        RebuildReaderPages();
    }

    private void ApplyReaderTheme()
    {
        switch (SelectedReaderTheme)
        {
            case "Paper":
                ReaderCanvasBrush = CreateBrush("#D9D7D0");
                ReaderPageBrush = CreateBrush("#F7F5EF");
                ReaderTextBrush = CreateBrush("#25231F");
                break;
            case "Sepia":
                ReaderCanvasBrush = CreateBrush("#CFC0A4");
                ReaderPageBrush = CreateBrush("#EEE1C8");
                ReaderTextBrush = CreateBrush("#3D3020");
                break;
            default:
                ReaderCanvasBrush = CreateBrush("#090B10");
                ReaderPageBrush = CreateBrush("#171A22");
                ReaderTextBrush = CreateBrush("#EEF1F7");
                break;
        }
    }

    private void ApplyLibraryTheme()
    {
        var application = Application.Current;
        if (application is null)
        {
            return;
        }

        application.RequestedThemeVariant = SelectedLibraryTheme switch
        {
            "Light" => ThemeVariant.Light,
            "System" => ThemeVariant.Default,
            _ => ThemeVariant.Dark
        };

        var useLightPalette =
            string.Equals(
                SelectedLibraryTheme,
                "Light",
                StringComparison.OrdinalIgnoreCase)
            || (string.Equals(
                    SelectedLibraryTheme,
                    "System",
                    StringComparison.OrdinalIgnoreCase)
                && application.ActualThemeVariant == ThemeVariant.Light);
        var palette = useLightPalette
            ? new Dictionary<string, string>
            {
                ["OpenShelfCanvas"] = "#F3F1EC",
                ["OpenShelfSurface"] = "#FAF9F6",
                ["OpenShelfSurfaceRaised"] = "#FFFFFF",
                ["OpenShelfBorder"] = "#D7D2C8",
                ["OpenShelfText"] = "#1D2027",
                ["OpenShelfMuted"] = "#667085",
                ["OpenShelfAccent"] = "#8868E8",
                ["OpenShelfAccentStrong"] = "#6847C7"
            }
            : new Dictionary<string, string>
            {
                ["OpenShelfCanvas"] = "#0B0D12",
                ["OpenShelfSurface"] = "#12151D",
                ["OpenShelfSurfaceRaised"] = "#191D27",
                ["OpenShelfBorder"] = "#2A3040",
                ["OpenShelfText"] = "#F5F7FB",
                ["OpenShelfMuted"] = "#9CA5B8",
                ["OpenShelfAccent"] = "#9C7BFF",
                ["OpenShelfAccentStrong"] = "#B39BFF"
            };

        foreach (var (key, value) in palette)
        {
            application.Resources[key] = CreateBrush(value);
        }
    }

    private void ApplyUiSettings(UiApplicationSettings settings)
    {
        _settingsLoaded = false;
        SelectedLibraryTheme = SelectAvailable(
            LibraryThemes,
            settings.LibraryTheme,
            "Dark");
        SelectedReaderTheme = SelectAvailable(
            ReaderThemes,
            settings.ReaderTheme,
            "Midnight");
        SelectedReaderLayoutMode = SelectAvailable(
            ReaderLayoutModes,
            settings.ReaderLayoutMode,
            "Scroll");
        SelectedFontFamily = SelectAvailable(
            FontFamilies,
            settings.FontFamily,
            "Georgia");
        ReaderFontSize = settings.FontSize;
        ReaderLineSpacing = settings.LineHeight;
        ReaderParagraphSpacing = settings.ParagraphSpacing;
        ReaderMargin = settings.HorizontalMargin;
        ReaderContentWidth = settings.ColumnWidth;
        SelectedTextAlignment = SelectAvailable(
            TextAlignments,
            settings.Alignment,
            "Start");
        if (!string.IsNullOrWhiteSpace(settings.SpeechVoice))
        {
            SelectedVoice = ResolveSavedVoice(settings.SpeechVoice) ?? SelectedVoice;
        }

        SpeechRate = settings.SpeechRate / 175d;
        PersonalReadingWordsPerMinute = settings.PersonalReadingWordsPerMinute;
        SpeechVolume = settings.SpeechVolume;
        SpeechPitch = settings.SpeechPitch;
        HighlightSpokenSentence = settings.HighlightSpokenSentence;
        FollowSpokenSentence = settings.FollowSpokenSentence;
        _settingsLoaded = true;

        ApplyLibraryTheme();
        ApplyReaderTheme();
        ApplyTypography();
    }

    private static string SelectAvailable(
        IReadOnlyList<string> options,
        string? value,
        string fallback)
    {
        return options.FirstOrDefault(
                option => string.Equals(
                    option,
                    value,
                    StringComparison.OrdinalIgnoreCase))
            ?? fallback;
    }

    private SpeechVoiceOption? ResolveSavedVoice(string? savedVoice)
    {
        if (string.IsNullOrWhiteSpace(savedVoice))
        {
            return null;
        }

        var exact = VoiceOptions.FirstOrDefault(
            voice => string.Equals(
                voice.Id,
                savedVoice,
                StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    voice.DisplayName,
                    savedVoice,
                    StringComparison.CurrentCultureIgnoreCase));
        if (exact is not null)
        {
            return exact;
        }

        return VoiceOptions.FirstOrDefault(
            voice => voice.DisplayName.EndsWith(
                $"— {savedVoice}",
                StringComparison.CurrentCultureIgnoreCase)
                || voice.DisplayName.Contains(
                    savedVoice,
                    StringComparison.CurrentCultureIgnoreCase));
    }

    private bool UpdateReaderPageMetrics()
    {
        var columns = IsPagedMode
            && !IsFixedPageDocument
            && _readerViewportWidth >= 1040
                ? 2
                : 1;
        var horizontalPadding = 56d;
        var spreadGap = columns > 1 ? 24d : 0d;
        var availableWidth = Math.Max(
            300,
            (_readerViewportWidth - horizontalPadding - spreadGap) / columns);
        var pageWidth = Math.Clamp(
            Math.Min(ReaderContentWidth, availableWidth),
            300,
            1080);
        var pageHeight = Math.Clamp(
            _readerViewportHeight - 112,
            420,
            920);
        var changed = columns != ReaderPageColumns
            || Math.Abs(pageWidth - ReaderPagedPageWidth) >= 1
            || Math.Abs(pageHeight - ReaderPagedPageHeight) >= 1;

        ReaderPageColumns = columns;
        ReaderPagedPageWidth = pageWidth;
        ReaderPagedPageHeight = pageHeight;
        return changed;
    }

    private void RebuildReaderPages()
    {
        var locator = _currentReaderLocator;
        _readerPages.Clear();
        DisplayedReaderBlocks.Clear();
        DisplayedReaderPages.Clear();
        UpdateReaderPageMetrics();

        if (ReaderBlocks.Count == 0)
        {
            CurrentReaderPageIndex = 0;
            NotifyReaderPageStateChanged();
            return;
        }

        if (!IsPagedMode)
        {
            var allBlocks = ReaderBlocks.ToList();
            _readerPages.Add(allBlocks);
            foreach (var block in allBlocks)
            {
                DisplayedReaderBlocks.Add(block);
            }

            CurrentReaderPageIndex = 0;
            NotifyReaderPageStateChanged();
            return;
        }

        if (IsFixedPageDocument)
        {
            foreach (var group in ReaderBlocks.GroupBy(
                         block => ReaderPositionLocator.GetBaseLocator(block.Locator)))
            {
                _readerPages.Add(group.ToList());
            }

            var fixedTargetPage = FindReaderPage(locator);
            if (fixedTargetPage < 0)
            {
                fixedTargetPage = FindReaderPageByProgress(ReadingProgress);
            }

            ShowReaderPage(fixedTargetPage, updateReadingPosition: false);
            NotifyReaderPageStateChanged();
            return;
        }

        var innerWidth = Math.Max(
            220,
            ReaderPagedPageWidth - ReaderMargin * 2);
        var innerHeight = Math.Max(
            260,
            ReaderPagedPageHeight - ReaderMargin * 2);
        var averageCharacterWidth = Math.Max(6, ReaderFontSize * 0.52);
        var charactersPerLine = Math.Max(
            18,
            (int)Math.Floor(innerWidth / averageCharacterWidth));
        var linesPerPage = Math.Max(
            8,
            (int)Math.Floor(
                innerHeight / Math.Max(18, ReaderFontSize * ReaderLineSpacing)));
        var pageBudget = Math.Clamp(
            charactersPerLine * linesPerPage,
            320,
            4200);
        var page = new List<ReaderBlockViewModel>();
        var pageCost = 0;

        void CommitPage()
        {
            if (page.Count == 0)
            {
                return;
            }

            _readerPages.Add(page);
            page = new List<ReaderBlockViewModel>();
            pageCost = 0;
        }

        foreach (var block in ReaderBlocks)
        {
            if (block is ReaderTextBlockViewModel textBlock)
            {
                var textOffset = 0;
                if (textBlock.Text.Length == 0)
                {
                    page.Add(CreateTextFragment(textBlock, 0, 0));
                    continue;
                }

                while (textOffset < textBlock.Text.Length)
                {
                    var isHeading = textBlock is ReaderHeadingBlockViewModel;
                    var scale = isHeading
                        ? Math.Clamp(
                            Math.Pow(
                                Math.Max(1, ((ReaderHeadingBlockViewModel)textBlock).FontSize)
                                    / Math.Max(1, ReaderFontSize),
                                1.25),
                            1.35,
                            3.2)
                        : 1d;
                    var spacingCost = isHeading
                        ? charactersPerLine * 2
                        : (int)Math.Ceiling(
                            charactersPerLine
                            * ReaderParagraphSpacing
                            / Math.Max(18, ReaderFontSize * ReaderLineSpacing));
                    var availableCost = pageBudget - pageCost - spacingCost;
                    var minimumFragment = Math.Max(24, charactersPerLine);
                    if (page.Count > 0
                        && availableCost / scale < minimumFragment)
                    {
                        CommitPage();
                        availableCost = pageBudget - spacingCost;
                    }

                    var maximumCharacters = Math.Max(
                        1,
                        (int)Math.Floor(availableCost / scale));
                    var remainingCharacters = textBlock.Text.Length - textOffset;
                    var take = remainingCharacters <= maximumCharacters
                        ? remainingCharacters
                        : FindPageBreakLength(
                            textBlock.Text,
                            textOffset,
                            maximumCharacters,
                            minimumFragment);
                    take = Math.Clamp(take, 1, remainingCharacters);
                    page.Add(CreateTextFragment(textBlock, textOffset, take));
                    pageCost += spacingCost + (int)Math.Ceiling(take * scale);
                    textOffset += take;

                    if (textOffset < textBlock.Text.Length)
                    {
                        CommitPage();
                    }
                }

                continue;
            }

            var cost = block switch
            {
                ReaderImageBlockViewModel => Math.Max(220, pageBudget * 4 / 5),
                ReaderNoticeBlockViewModel notice =>
                    Math.Max(charactersPerLine * 2, notice.Text.Length + charactersPerLine),
                _ => charactersPerLine * 2
            };
            if (page.Count > 0 && pageCost + cost > pageBudget)
            {
                CommitPage();
            }

            page.Add(block);
            pageCost += cost;
        }

        CommitPage();

        var targetPage = FindReaderPage(locator);
        if (targetPage < 0)
        {
            targetPage = FindReaderPageByProgress(ReadingProgress);
        }

        ShowReaderPage(targetPage, updateReadingPosition: false);
        RefreshDisplayedFragmentDecorations();
        NotifyReaderPageStateChanged();
    }

    private static int FindPageBreakLength(
        string text,
        int offset,
        int maximumCharacters,
        int minimumFragment)
    {
        var available = Math.Min(maximumCharacters, text.Length - offset);
        if (available <= 1 || offset + available >= text.Length)
        {
            return Math.Max(1, available);
        }

        var minimumIndex = offset + Math.Min(available, minimumFragment);
        for (var index = offset + available; index >= minimumIndex; index--)
        {
            if (char.IsWhiteSpace(text[index - 1]))
            {
                return index - offset;
            }
        }

        return available;
    }

    private static ReaderTextBlockViewModel CreateTextFragment(
        ReaderTextBlockViewModel source,
        int relativeOffset,
        int length)
    {
        var boundedOffset = Math.Clamp(relativeOffset, 0, source.Text.Length);
        var boundedLength = Math.Clamp(
            length,
            0,
            source.Text.Length - boundedOffset);
        var fragmentText = source.Text.Substring(boundedOffset, boundedLength);
        var sourceOffset = source.SourceOffset + boundedOffset;
        ReaderTextBlockViewModel fragment = source switch
        {
            ReaderHeadingBlockViewModel heading =>
                new ReaderHeadingBlockViewModel(
                    heading.Locator,
                    fragmentText,
                    sourceOffset)
                {
                    FontSize = heading.FontSize
                },
            ReaderParagraphBlockViewModel paragraph =>
                new ReaderParagraphBlockViewModel(
                    paragraph.Locator,
                    fragmentText,
                    sourceOffset)
                {
                    FontSize = paragraph.FontSize,
                    LineHeight = paragraph.LineHeight,
                    ParagraphMargin = paragraph.ParagraphMargin,
                    TextAlignment = paragraph.TextAlignment
                },
            _ => throw new InvalidOperationException("Unsupported reader text block.")
        };
        ProjectTextDecorations(source, fragment);
        return fragment;
    }

    private static void ProjectTextDecorations(
        ReaderTextBlockViewModel source,
        ReaderTextBlockViewModel fragment)
    {
        var fragmentStart = fragment.SourceOffset;
        var fragmentEnd = fragmentStart + fragment.Text.Length;
        var highlights = source.HighlightRanges
            .Select(
                range =>
                {
                    var start = Math.Max(range.Start, fragmentStart);
                    var end = Math.Min(range.Start + range.Length, fragmentEnd);
                    return new ReaderHighlightRange(
                        start - fragmentStart,
                        Math.Max(0, end - start),
                        range.ColorName);
                })
            .Where(range => range.Length > 0)
            .ToArray();
        fragment.SetHighlightRanges(highlights);

        var spoken = source.SpokenRange;
        if (spoken is null)
        {
            fragment.SetSpokenRange(null);
            return;
        }

        var spokenStart = Math.Max(spoken.Start, fragmentStart);
        var spokenEnd = Math.Min(spoken.Start + spoken.Length, fragmentEnd);
        fragment.SetSpokenRange(
            spokenEnd > spokenStart
                ? new ReaderSpokenRange(
                    spokenStart - fragmentStart,
                    spokenEnd - spokenStart)
                : null);
    }

    private void RefreshDisplayedFragmentDecorations()
    {
        var sourceBlocks = ReaderBlocks
            .OfType<ReaderTextBlockViewModel>()
            .ToArray();
        foreach (var fragment in _readerPages
                     .SelectMany(item => item)
                     .OfType<ReaderTextBlockViewModel>())
        {
            var source = sourceBlocks.FirstOrDefault(
                block => string.Equals(
                    ReaderPositionLocator.GetBaseLocator(block.Locator),
                    ReaderPositionLocator.GetBaseLocator(fragment.Locator),
                    StringComparison.Ordinal));
            if (source is not null && !ReferenceEquals(source, fragment))
            {
                ProjectTextDecorations(source, fragment);
            }
        }
    }

    private int FindReaderPage(string locator)
    {
        if (string.IsNullOrWhiteSpace(locator))
        {
            return -1;
        }

        var baseLocator = ReaderPositionLocator.GetBaseLocator(locator);
        var isSectionTarget = baseLocator.EndsWith('|');
        var characterOffset = 0;
        var hasCharacterOffset = locator.Contains('#', StringComparison.Ordinal)
            && ReaderPositionLocator.TryParseReflowable(
                locator,
                out _,
                out characterOffset);
        for (var index = 0; index < _readerPages.Count; index++)
        {
            if (_readerPages[index].Any(
                    block =>
                        PageBlockContainsPosition(
                            block,
                            baseLocator,
                            hasCharacterOffset,
                            characterOffset)
                        || isSectionTarget
                        && ReaderPositionLocator.GetBaseLocator(block.Locator).StartsWith(
                            baseLocator,
                            StringComparison.Ordinal)))
            {
                return index;
            }
        }

        return -1;
    }

    private bool PageBlockContainsPosition(
        ReaderBlockViewModel block,
        string baseLocator,
        bool hasCharacterOffset,
        int characterOffset)
    {
        if (!string.Equals(
                ReaderPositionLocator.GetBaseLocator(block.Locator),
                baseLocator,
                StringComparison.Ordinal))
        {
            return false;
        }

        if (!hasCharacterOffset || block is not ReaderTextBlockViewModel text)
        {
            return true;
        }

        var fragmentEnd = text.SourceOffset + text.Text.Length;
        if (characterOffset >= text.SourceOffset && characterOffset < fragmentEnd)
        {
            return true;
        }

        var sourceLength = ReaderBlocks
            .OfType<ReaderTextBlockViewModel>()
            .FirstOrDefault(
                item => string.Equals(
                    ReaderPositionLocator.GetBaseLocator(item.Locator),
                    baseLocator,
                    StringComparison.Ordinal))
            ?.Text.Length;
        return sourceLength.HasValue
            && characterOffset == sourceLength.Value
            && fragmentEnd == sourceLength.Value;
    }

    private int FindReaderPageByProgress(double progress)
    {
        if (_readerPages.Count == 0)
        {
            return 0;
        }

        var target = Math.Clamp(progress, 0, 1);
        var selectedPage = 0;
        for (var index = 0; index < _readerPages.Count; index++)
        {
            var firstBlock = _readerPages[index].FirstOrDefault();
            if (firstBlock is null)
            {
                continue;
            }

            var pageProgress = CalculatePageStartProgress(firstBlock);
            if (pageProgress > target)
            {
                break;
            }

            selectedPage = index;
        }

        return selectedPage;
    }

    private double CalculatePageStartProgress(ReaderBlockViewModel firstBlock)
    {
        if (firstBlock is not ReaderTextBlockViewModel textFragment
            || IsFixedPageDocument)
        {
            return CalculateNormalizedProgress(firstBlock, 0);
        }

        var source = ReaderBlocks.FirstOrDefault(
            block => string.Equals(
                ReaderPositionLocator.GetBaseLocator(block.Locator),
                ReaderPositionLocator.GetBaseLocator(textFragment.Locator),
                StringComparison.Ordinal));
        return source is null
            ? 0
            : CalculateNormalizedProgress(source, textFragment.SourceOffset);
    }

    private void ShowReaderPage(int pageIndex, bool updateReadingPosition)
    {
        if (_readerPages.Count == 0)
        {
            return;
        }

        var clampedIndex = Math.Clamp(pageIndex, 0, _readerPages.Count - 1);
        var columns = Math.Max(1, ReaderPageColumns);
        var spreadStart = clampedIndex / columns * columns;
        DisplayedReaderBlocks.Clear();
        DisplayedReaderPages.Clear();
        for (var index = spreadStart;
             index < Math.Min(_readerPages.Count, spreadStart + columns);
             index++)
        {
            var pageBlocks = _readerPages[index];
            DisplayedReaderPages.Add(
                new ReaderPageViewModel(
                    index + 1,
                    _readerPages.Count,
                    pageBlocks));
            foreach (var block in pageBlocks)
            {
                DisplayedReaderBlocks.Add(block);
            }
        }

        CurrentReaderPageIndex = spreadStart;
        if (updateReadingPosition)
        {
            var firstBlock = _readerPages[spreadStart].FirstOrDefault();
            if (firstBlock is not null)
            {
                SetReaderPosition(GetPageBlockStartLocator(firstBlock), 0);
            }
        }
    }

    private static string GetPageBlockStartLocator(ReaderBlockViewModel block)
    {
        return block is ReaderTextBlockViewModel text
            ? ReaderPositionLocator.FormatReflowable(
                text.Locator,
                text.SourceOffset)
            : block.Locator;
    }

    private void PreviousReaderPage()
    {
        if (CanGoToPreviousPage)
        {
            ShowReaderPage(
                CurrentReaderPageIndex - ReaderPageColumns,
                updateReadingPosition: true);
        }
    }

    private void NextReaderPage()
    {
        if (CanGoToNextPage)
        {
            ShowReaderPage(
                CurrentReaderPageIndex + ReaderPageColumns,
                updateReadingPosition: true);
        }
    }

    private void NavigateToReaderPage(string locator, double progress)
    {
        if (!IsPagedMode || _readerPages.Count == 0)
        {
            return;
        }

        var pageIndex = FindReaderPage(locator);
        if (pageIndex < 0)
        {
            pageIndex = FindReaderPageByProgress(progress);
        }

        ShowReaderPage(pageIndex, updateReadingPosition: false);
        SetReaderPosition(locator, 0);
    }

    private double CalculateNormalizedProgress(
        ReaderBlockViewModel targetBlock,
        long offsetWithinBlock)
    {
        if (IsFixedPageDocument
            && ReaderPositionLocator.TryParsePdf(
                targetBlock.Locator,
                out var pageIndex,
                out _))
        {
            return FixedPageCount <= 0
                ? 0
                : Math.Clamp((double)pageIndex / FixedPageCount, 0, 1);
        }

        var totalLength = ReaderBlocks.Sum(block => block.NormalizedLength);
        if (totalLength <= 0)
        {
            return 0;
        }

        long precedingLength = 0;
        foreach (var block in ReaderBlocks)
        {
            if (ReferenceEquals(block, targetBlock))
            {
                var clampedOffset = Math.Clamp(
                    offsetWithinBlock,
                    0,
                    block.NormalizedLength);
                return Math.Clamp(
                    (double)(precedingLength + clampedOffset) / totalLength,
                    0,
                    1);
            }

            precedingLength += block.NormalizedLength;
        }

        return 0;
    }

    private void NotifyReaderPageStateChanged()
    {
        OnPropertyChanged(nameof(ReaderPageCount));
        OnPropertyChanged(nameof(ReaderPageLabel));
        OnPropertyChanged(nameof(CanGoToPreviousPage));
        OnPropertyChanged(nameof(CanGoToNextPage));
        PreviousPageCommand.RaiseCanExecuteChanged();
        NextPageCommand.RaiseCanExecuteChanged();
    }

    private void AddBookmark()
    {
        if (CurrentBook is null)
        {
            return;
        }

        var locator = _currentReaderLocator;
        if (string.IsNullOrWhiteSpace(locator))
        {
            ShowStatus("The current reading location is not available yet");
            return;
        }
        if (Annotations.Any(annotation =>
                annotation.Kind == AnnotationKind.Bookmark && annotation.Locator == locator))
        {
            ShowStatus("This location is already bookmarked");
            return;
        }

        var chapterLabel = TableOfContents
            .Where(item => item.Progress <= ReadingProgress)
            .LastOrDefault()
            ?.Title;
        var bookmarkTitle = string.IsNullOrWhiteSpace(chapterLabel)
            ? $"Bookmark at {ReadingProgress:P0}"
            : $"Bookmark · {chapterLabel}";
        var annotation = new AnnotationItemViewModel(
                Guid.NewGuid(),
                AnnotationKind.Bookmark,
                locator,
                bookmarkTitle,
                ReaderTitle,
                string.Empty,
                DateTimeOffset.Now,
                anchorStart: 0,
                anchorLength: 0);
        Annotations.Insert(0, annotation);
        _ = PersistAnnotationAsync(annotation);
        AnnotationCollectionChanged();
        ShowStatus("Bookmark added");
    }

    private void AddHighlight()
    {
        if (!HasSelection)
        {
            return;
        }

        var annotation = new AnnotationItemViewModel(
                Guid.NewGuid(),
                AnnotationKind.Highlight,
                _selectedReaderLocator,
                "Highlight",
                SelectionPreview,
                string.Empty,
                DateTimeOffset.Now,
                _selectedReaderStart,
                _selectedReaderLength,
                SelectedHighlightColor);
        Annotations.Insert(0, annotation);
        _ = PersistAnnotationAsync(annotation);
        AnnotationCollectionChanged();
        ShowStatus("Highlight saved");
    }

    private void OpenNoteComposer()
    {
        if (!HasSelection)
        {
            return;
        }

        _editingAnnotation = null;
        PendingNoteText = string.Empty;
        IsNoteComposerOpen = true;
    }

    private void OpenEditNote(AnnotationItemViewModel? annotation)
    {
        if (annotation is null || annotation.Kind != AnnotationKind.Note)
        {
            return;
        }

        _editingAnnotation = annotation;
        UpdateReaderSelection(
            annotation.Excerpt,
            annotation.Locator,
            annotation.AnchorStart,
            annotation.AnchorLength);
        PendingNoteText = annotation.Note;
        IsNoteComposerOpen = true;
    }

    private void SaveNote()
    {
        if (!HasSelection || string.IsNullOrWhiteSpace(PendingNoteText))
        {
            return;
        }

        var annotation = new AnnotationItemViewModel(
                _editingAnnotation?.Id ?? Guid.NewGuid(),
                AnnotationKind.Note,
                _editingAnnotation?.Locator ?? _selectedReaderLocator,
                "Note",
                _editingAnnotation?.Excerpt ?? SelectionPreview,
                PendingNoteText.Trim(),
                _editingAnnotation?.CreatedAt ?? DateTimeOffset.Now,
                _editingAnnotation?.AnchorStart ?? _selectedReaderStart,
                _editingAnnotation?.AnchorLength ?? _selectedReaderLength);
        if (_editingAnnotation is not null)
        {
            Annotations.Remove(_editingAnnotation);
        }

        Annotations.Insert(0, annotation);
        _ = PersistAnnotationAsync(annotation);
        AnnotationCollectionChanged();
        _editingAnnotation = null;
        IsNoteComposerOpen = false;
        PendingNoteText = string.Empty;
        _editingAnnotation = null;
        ShowStatus("Note saved");
    }

    private void CancelNote()
    {
        IsNoteComposerOpen = false;
        PendingNoteText = string.Empty;
    }

    private void RemoveAnnotation(AnnotationItemViewModel? annotation)
    {
        if (annotation is null)
        {
            return;
        }

        Annotations.Remove(annotation);
        _ = DeleteAnnotationAsync(annotation);
        AnnotationCollectionChanged();
    }

    private void AnnotationCollectionChanged()
    {
        RefreshRenderedHighlights();
        OnPropertyChanged(nameof(HasAnnotations));
    }

    private void RefreshRenderedHighlights()
    {
        foreach (var block in ReaderBlocks.OfType<ReaderTextBlockViewModel>())
        {
            var ranges = Annotations
                .Where(
                    annotation =>
                        annotation.Kind == AnnotationKind.Highlight
                        && string.Equals(
                            ReaderPositionLocator.GetBaseLocator(annotation.Locator),
                            ReaderPositionLocator.GetBaseLocator(block.Locator),
                            StringComparison.Ordinal))
                .Select(
                    annotation => new ReaderHighlightRange(
                        annotation.AnchorStart,
                        annotation.AnchorLength,
                        annotation.HighlightColorName));
            block.SetHighlightRanges(ranges);
        }

        RefreshDisplayedFragmentDecorations();
    }

    private void JumpToToc(TocItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        ReadingProgress = item.Progress;
        _currentReaderLocator = item.Locator;
        NavigateToReaderPage(item.Locator, item.Progress);
        ScheduleReadingProgressSave();
        ReaderNavigationRequested?.Invoke(
            this,
            new ReaderNavigationRequest(item.Locator, item.Progress));
        ShowStatus($"Moved to {item.Title}");
    }

    private void JumpToAnnotation(AnnotationItemViewModel? annotation)
    {
        if (annotation is null)
        {
            return;
        }

        _currentReaderLocator = annotation.Locator;
        NavigateToReaderPage(annotation.Locator, ReadingProgress);
        ScheduleReadingProgressSave();
        ReaderNavigationRequested?.Invoke(
            this,
            new ReaderNavigationRequest(annotation.Locator, ReadingProgress));
        ShowStatus($"Moved to {annotation.Title}");
    }

    private void UpdateImportProgress(ImportProgress progress)
    {
        IsImportIndeterminate = progress.IsScanning || progress.Total == 0;
        ImportProgressValue =
            progress.Total == 0 ? 0 : (double)progress.Completed / progress.Total * 100;
        ImportStatus = progress.IsScanning
            ? progress.CurrentItem
            : $"Importing {progress.CurrentItem}";
        ImportSummary =
            $"{progress.Imported} imported · {progress.Skipped} skipped · {progress.Failed} failed";
    }

    private void OpenPasswordPrompt(ImportResultItemViewModel? result)
    {
        if (result is null || !result.RequiresPassword)
        {
            return;
        }

        _passwordRetryToken = result.PasswordRetryToken;
        PasswordBookName = result.Name;
        ImportPassword = string.Empty;
        IsPasswordPromptOpen = true;
        RetryPasswordCommand.RaiseCanExecuteChanged();
    }

    private void CancelPasswordPrompt()
    {
        ImportPassword = string.Empty;
        _passwordRetryToken = null;
        PasswordBookName = string.Empty;
        IsPasswordPromptOpen = false;
        RetryPasswordCommand.RaiseCanExecuteChanged();
    }

    private async Task RetryPasswordAsync()
    {
        if (string.IsNullOrWhiteSpace(_passwordRetryToken)
            || string.IsNullOrEmpty(ImportPassword)
            || IsImporting)
        {
            return;
        }

        var token = _passwordRetryToken;
        var password = ImportPassword;
        _importCancellation = new CancellationTokenSource();
        IsImporting = true;
        IsImportIndeterminate = true;
        ImportStatus = $"Unlocking {PasswordBookName}…";

        try
        {
            var progress = new Progress<ImportProgress>(UpdateImportProgress);
            var batch = await _services.Library
                .RetryWithPasswordAsync(
                    token,
                    password,
                    progress,
                    _importCancellation.Token);

            foreach (var descriptor in batch.ImportedBooks)
            {
                _books.Add(CreateBookItem(descriptor));
            }

            var existing = ImportResults.FirstOrDefault(
                result => string.Equals(
                    result.PasswordRetryToken,
                    token,
                    StringComparison.Ordinal));
            if (existing is not null)
            {
                ImportResults.Remove(existing);
            }

            foreach (var result in batch.Results)
            {
                ImportResults.Add(result);
            }

            RebuildCategories();
            RefreshVisibleBooks();
            var retryRequired = batch.Results.FirstOrDefault(
                result => result.RequiresPassword);
            if (retryRequired is null)
            {
                _passwordRetryToken = null;
                PasswordBookName = string.Empty;
                IsPasswordPromptOpen = false;
                ImportStatus = "Import complete";
                ShowStatus("Protected book unlocked and imported");
            }
            else
            {
                _passwordRetryToken = retryRequired.PasswordRetryToken;
                PasswordBookName = retryRequired.Name;
                ImportStatus = retryRequired.Message;
            }
        }
        catch (OperationCanceledException)
        {
            ImportStatus = "Password retry cancelled";
        }
        catch (Exception exception)
        {
            ImportStatus = $"Book could not be unlocked: {exception.Message}";
        }
        finally
        {
            password = string.Empty;
            ImportPassword = string.Empty;
            IsImportIndeterminate = false;
            IsImporting = false;
            _importCancellation.Dispose();
            _importCancellation = null;
            RetryPasswordCommand.RaiseCanExecuteChanged();
        }
    }

    private void CancelImport()
    {
        _importCancellation?.Cancel();
    }

    private void CloseImport()
    {
        if (!IsImporting)
        {
            IsImportPanelOpen = false;
        }
    }

    private void CancelAddCategory()
    {
        NewCategoryName = string.Empty;
        IsAddCategoryOpen = false;
    }

    private void SaveCategory()
    {
        var name = NewCategoryName.Trim();
        if (string.IsNullOrWhiteSpace(name)
            || Categories.Any(category =>
                string.Equals(category.Name, name, StringComparison.CurrentCultureIgnoreCase)))
        {
            CancelAddCategory();
            return;
        }

        Categories.Add(new CategoryItemViewModel(name));
        _ = CreateCategoryAsync(name);
        NewCategoryName = string.Empty;
        IsAddCategoryOpen = false;
        OnPropertyChanged(nameof(CategoryNames));
    }

    private void OpenBookEditor(BookItemViewModel? book)
    {
        if (book is null)
        {
            return;
        }

        _editingBook = book;
        EditingTitle = book.Title;
        EditingAuthor = book.Author;
        EditingCategory = book.Category;
        IsBookEditorOpen = true;
        SaveBookEditorCommand.RaiseCanExecuteChanged();
    }

    private void CancelBookEditor()
    {
        _editingBook = null;
        IsBookEditorOpen = false;
        SaveBookEditorCommand.RaiseCanExecuteChanged();
    }

    private void SaveBookEditor()
    {
        if (_editingBook is null || string.IsNullOrWhiteSpace(EditingTitle))
        {
            return;
        }

        _editingBook.Title = EditingTitle.Trim();
        _editingBook.Author = string.IsNullOrWhiteSpace(EditingAuthor)
            ? "Unknown author"
            : EditingAuthor.Trim();
        _editingBook.Category = string.IsNullOrWhiteSpace(EditingCategory)
            ? "Uncategorised"
            : EditingCategory;

        var editedBook = _editingBook;
        _editingBook = null;
        IsBookEditorOpen = false;
        RebuildCategories();
        RefreshVisibleBooks();
        ShowStatus("Book details updated");
        SaveBookEditorCommand.RaiseCanExecuteChanged();
        _ = PersistBookMetadataAsync(editedBook);
    }

    private async Task ToggleSpeechAsync()
    {
        if (IsSpeaking && !IsSpeechPaused)
        {
            _services.Speech.Pause();
            IsSpeechPaused = true;
            return;
        }

        if (IsSpeaking && IsSpeechPaused)
        {
            _services.Speech.Resume();
            IsSpeechPaused = false;
            return;
        }

        if (_speechSegments.Count == 0)
        {
            ShowStatus("There is no readable text at this location");
            return;
        }

        var startIndex = HasSelection
            ? FindSpeechSegmentIndex(
                _selectedReaderLocator,
                _selectedReaderStart)
            : Math.Clamp(
                _speechResumeSegmentIndex,
                0,
                _speechSegments.Count - 1);
        if (startIndex < 0)
        {
            startIndex = 0;
        }

        await StartSpeechAsync(startIndex);
    }

    private async Task StartSpeechAsync(int startIndex)
    {
        if (!IsSpeechAvailable)
        {
            RefreshSpeechAvailability(showFailure: true);
            return;
        }

        if (_speechSegments.Count == 0)
        {
            return;
        }

        _speechCancellation?.Cancel();
        _services.Speech.Stop();
        _speechCancellation?.Dispose();
        var session = new CancellationTokenSource();
        _speechCancellation = session;
        var boundedStart = Math.Clamp(startIndex, 0, _speechSegments.Count - 1);
        IsSpeaking = true;
        IsSpeechPaused = false;
        ActivateSpeechSegment(boundedStart);
        _ = PersistSpeechPositionAsync();

        try
        {
            await _services.Speech.SpeakAsync(
                _speechSegments.Select(segment => segment.Text).ToArray(),
                SelectedVoice?.Id ?? string.Empty,
                SpeechRate,
                (int)Math.Round(SpeechVolume),
                SpeechPitch,
                boundedStart,
                session.Token);
            if (!IsSpeechAvailable)
            {
                RefreshSpeechAvailability(showFailure: true);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_speechCancellation, session))
            {
                IsSpeaking = false;
                IsSpeechPaused = false;
                _speechCancellation = null;
                _activeSpeechSegmentIndex = -1;
                ClearSpokenHighlight();
            }

            session.Dispose();
        }
    }

    private void RefreshSpeechAvailability(bool showFailure)
    {
        OnPropertyChanged(nameof(VoiceOptions));
        OnPropertyChanged(nameof(VoiceProviderStatus));
        OnPropertyChanged(nameof(PeterStatus));
        OnPropertyChanged(nameof(IsSpeechAvailable));
        OnPropertyChanged(nameof(SpeechActionLabel));
        OnPropertyChanged(nameof(SpeechStatus));
        ToggleSpeechCommand.RaiseCanExecuteChanged();

        if (!IsSpeechAvailable)
        {
            SelectedVoice = null;
            if (showFailure)
            {
                ShowStatus(
                    "Text to speech is unavailable. Open Help & Diagnostics for details.");
            }
        }
    }

    private async Task RefreshVoicesAsync()
    {
        if (IsRefreshingVoices || IsSpeaking)
        {
            return;
        }

        var previousId = SelectedVoice?.Id;
        IsRefreshingVoices = true;
        try
        {
            await _services.Speech.RefreshVoicesAsync(CancellationToken.None);
            OnPropertyChanged(nameof(VoiceOptions));
            OnPropertyChanged(nameof(VoiceProviderStatus));
            OnPropertyChanged(nameof(PeterStatus));
            SelectedVoice = VoiceOptions.FirstOrDefault(
                    voice => string.Equals(
                        voice.Id,
                        previousId,
                        StringComparison.OrdinalIgnoreCase))
                ?? VoiceOptions.FirstOrDefault();
            RefreshSpeechAvailability(showFailure: false);
            RefreshDiagnostics();
            ShowStatus(IsSpeechAvailable
                ? $"Voice list refreshed — {VoiceOptions.Count} available"
                : "No text-to-speech voices are available");
        }
        catch (Exception exception)
        {
            ShowStatus($"Voice refresh failed: {exception.Message}");
        }
        finally
        {
            IsRefreshingVoices = false;
        }
    }

    private async Task PreviewVoiceAsync()
    {
        if (SelectedVoice is null || IsSpeaking)
        {
            return;
        }

        _speechCancellation?.Cancel();
        _speechCancellation?.Dispose();
        var session = new CancellationTokenSource();
        _speechCancellation = session;
        IsSpeaking = true;
        IsSpeechPaused = false;
        SpeechProgressLabel = $"Previewing {SelectedVoice.DisplayName}";
        try
        {
            await _services.Speech.PreviewAsync(
                SelectedVoice.Id,
                SpeechRate,
                (int)Math.Round(SpeechVolume),
                SpeechPitch,
                session.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            ShowStatus($"Voice preview failed: {exception.Message}");
        }
        finally
        {
            if (ReferenceEquals(_speechCancellation, session))
            {
                _speechCancellation = null;
                IsSpeaking = false;
                IsSpeechPaused = false;
                SpeechProgressLabel = string.Empty;
            }

            session.Dispose();
        }
    }

    private void StopSpeech()
    {
        _speechCancellation?.Cancel();
        _speechCancellation = null;
        IsSpeaking = false;
        IsSpeechPaused = false;
        SpeechProgressLabel = string.Empty;
        _activeSpeechSegmentIndex = -1;
        ClearSpokenHighlight();
        _services.Speech.Stop();
    }

    private void ShowStatus(string message)
    {
        StatusMessage = message;
        IsStatusVisible = true;
    }

    private void Speech_ProgressChanged(
        object? sender,
        TextToSpeechProgress progress)
    {
        if (Application.Current is not null
            && !Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(
                () => Speech_ProgressChanged(sender, progress));
            return;
        }

        var preview = progress.Text.Length > 72
            ? progress.Text[..72] + "..."
            : progress.Text;
        SpeechProgressLabel =
            $"{progress.SegmentIndex + 1}/{progress.SegmentCount}  {preview}";
        if (IsSpeaking
            && progress.SegmentIndex >= 0
            && progress.SegmentIndex < _speechSegments.Count)
        {
            ActivateSpeechSegment(progress.SegmentIndex);
            _ = PersistSpeechPositionAsync();
        }
    }

    private void ActivateSpeechSegment(int segmentIndex)
    {
        if (segmentIndex < 0 || segmentIndex >= _speechSegments.Count)
        {
            return;
        }

        _activeSpeechSegmentIndex = segmentIndex;
        SetSpeechResumePosition(segmentIndex);
        ApplySpokenHighlight();
        if (FollowSpokenSentence)
        {
            FollowActiveSpeechSegment();
        }
    }

    private void ApplySpokenHighlight()
    {
        ClearSpokenHighlight();
        if (!HighlightSpokenSentence
            || _activeSpeechSegmentIndex < 0
            || _activeSpeechSegmentIndex >= _speechSegments.Count)
        {
            return;
        }

        var segment = _speechSegments[_activeSpeechSegmentIndex];
        var block = ReaderBlocks
            .OfType<ReaderTextBlockViewModel>()
            .FirstOrDefault(
                item => string.Equals(
                    item.Locator,
                    segment.Locator,
                    StringComparison.Ordinal));
        block?.SetSpokenRange(
            new ReaderSpokenRange(segment.CharacterOffset, segment.Text.Length));
        RefreshDisplayedFragmentDecorations();
    }

    private void ClearSpokenHighlight()
    {
        foreach (var block in ReaderBlocks.OfType<ReaderTextBlockViewModel>())
        {
            block.SetSpokenRange(null);
        }

        RefreshDisplayedFragmentDecorations();
    }

    private void FollowActiveSpeechSegment()
    {
        if (!IsReaderOpen
            || _activeSpeechSegmentIndex < 0
            || _activeSpeechSegmentIndex >= _speechSegments.Count)
        {
            return;
        }

        var segment = _speechSegments[_activeSpeechSegmentIndex];
        var block = ReaderBlocks
            .OfType<ReaderTextBlockViewModel>()
            .FirstOrDefault(
                item => string.Equals(
                    item.Locator,
                    segment.Locator,
                    StringComparison.Ordinal));
        if (block is null)
        {
            return;
        }

        var withinBlock = Math.Clamp(
            (double)segment.CharacterOffset / Math.Max(1, block.NormalizedLength),
            0,
            1);
        SetReaderPosition(segment.Locator, withinBlock);
        var positionLocator = _currentReaderLocator;
        if (IsPagedMode)
        {
            NavigateToReaderPage(positionLocator, ReadingProgress);
        }

        ReaderNavigationRequested?.Invoke(
            this,
            new ReaderNavigationRequest(positionLocator, ReadingProgress));
    }

    private void BuildSpeechSegments()
    {
        _speechSegments.Clear();
        foreach (var block in ReaderBlocks)
        {
            var text = block switch
            {
                ReaderHeadingBlockViewModel heading => heading.Text,
                ReaderParagraphBlockViewModel paragraph => paragraph.Text,
                _ => string.Empty
            };
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            AppendSpeechSegments(block.Locator, text, _speechSegments);
        }
    }

    private static void AppendSpeechSegments(
        string locator,
        string text,
        ICollection<ReaderSpeechSegment> target)
    {
        var sentenceStart = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] is not ('.' or '!' or '?'))
            {
                continue;
            }

            var sentenceEnd = index + 1;
            while (sentenceEnd < text.Length
                   && IsSentenceClosingPunctuation(text[sentenceEnd]))
            {
                sentenceEnd++;
            }

            var boundary = sentenceEnd == text.Length
                || char.IsWhiteSpace(text[sentenceEnd]);
            if (!boundary)
            {
                continue;
            }

            AddSpeechSegment(locator, text, sentenceStart, sentenceEnd, target);
            sentenceStart = sentenceEnd;
            index = sentenceEnd - 1;
        }

        AddSpeechSegment(locator, text, sentenceStart, text.Length, target);
    }

    private static bool IsSentenceClosingPunctuation(char value) =>
        value is '"' or '\'' or '\u2019' or '\u201D' or ')' or ']' or '}';

    private static void AddSpeechSegment(
        string locator,
        string source,
        int start,
        int end,
        ICollection<ReaderSpeechSegment> target)
    {
        while (start < end && char.IsWhiteSpace(source[start]))
        {
            start++;
        }

        while (end > start && char.IsWhiteSpace(source[end - 1]))
        {
            end--;
        }

        if (end > start)
        {
            target.Add(
                new ReaderSpeechSegment(
                    locator,
                    start,
                    source[start..end]));
        }
    }

    private int ResolveSpeechResumeIndex(BookItemViewModel book)
    {
        if (_speechSegments.Count == 0)
        {
            return 0;
        }

        var anchoredIndex = FindSpeechSegmentIndex(
            book.SpeechLocator,
            book.SpeechCharacterOffset);
        return anchoredIndex >= 0
            ? anchoredIndex
            : Math.Clamp(book.SpeechSentenceIndex, 0, _speechSegments.Count - 1);
    }

    private int FindSpeechSegmentIndex(string locator, int characterOffset)
    {
        if (string.IsNullOrWhiteSpace(locator))
        {
            return -1;
        }

        var firstMatch = -1;
        var closestMatch = -1;
        for (var index = 0; index < _speechSegments.Count; index++)
        {
            var segment = _speechSegments[index];
            if (!string.Equals(segment.Locator, locator, StringComparison.Ordinal))
            {
                continue;
            }

            firstMatch = firstMatch < 0 ? index : firstMatch;
            if (segment.CharacterOffset <= characterOffset)
            {
                closestMatch = index;
            }
            else
            {
                break;
            }
        }

        return closestMatch >= 0 ? closestMatch : firstMatch;
    }

    private void SetSpeechResumePosition(int segmentIndex)
    {
        if (segmentIndex < 0 || segmentIndex >= _speechSegments.Count)
        {
            return;
        }

        var segment = _speechSegments[segmentIndex];
        _speechResumeSegmentIndex = segmentIndex;
        _speechResumeLocator = segment.Locator;
        _speechResumeCharacterOffset = segment.CharacterOffset;
        if (CurrentBook is not null)
        {
            CurrentBook.SpeechSentenceIndex = segmentIndex;
            CurrentBook.SpeechLocator = segment.Locator;
            CurrentBook.SpeechCharacterOffset = segment.CharacterOffset;
        }
    }

    private async Task PersistSpeechPositionAsync()
    {
        try
        {
            await SaveSpeechPositionCoreAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            ShowStatus($"Text-to-speech position could not be saved: {exception.Message}");
        }
    }

    private void ScheduleUiSettingsSave()
    {
        if (!_settingsLoaded)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        var cancellationToken = cancellation.Token;
        var previousSave = Interlocked.Exchange(
            ref _settingsSaveCancellation,
            cancellation);
        CancelPendingSave(previousSave);
        _ = SaveUiSettingsAfterDelayAsync(cancellation, cancellationToken);
    }

    private async Task SaveUiSettingsAfterDelayAsync(
        CancellationTokenSource cancellation,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(750), cancellationToken);
            await SaveUiSettingsCoreAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            ShowStatus($"Appearance settings could not be saved: {exception.Message}");
        }
        finally
        {
            Interlocked.CompareExchange(
                ref _settingsSaveCancellation,
                null,
                cancellation);
            cancellation.Dispose();
        }
    }

    private Task SaveUiSettingsCoreAsync(CancellationToken cancellationToken)
    {
        var settings = new UiApplicationSettings(
            SelectedLibraryTheme,
            SelectedReaderTheme,
            SelectedReaderLayoutMode,
            SelectedFontFamily,
            ReaderFontSize,
            ReaderLineSpacing,
            ReaderParagraphSpacing,
            ReaderMargin,
            ReaderContentWidth,
            SelectedTextAlignment,
            SelectedVoice?.Id,
            Math.Clamp(
                (int)Math.Round(175 * SpeechRate),
                80,
                450),
            HighlightSpokenSentence,
            FollowSpokenSentence,
            Math.Clamp((int)Math.Round(SpeechVolume), 0, 100),
            Math.Clamp(SpeechPitch, 0.5, 2),
            PersonalReadingWordsPerMinute);
        return _services.Library.SaveUiSettingsAsync(
            settings,
            cancellationToken);
    }

    private void ScheduleReadingProgressSave()
    {
        if (CurrentBook is null || !IsReaderOpen)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        var cancellationToken = cancellation.Token;
        var previousSave = Interlocked.Exchange(
            ref _progressSaveCancellation,
            cancellation);
        CancelPendingSave(previousSave);
        _ = SaveReadingProgressAfterDelayAsync(cancellation, cancellationToken);
    }

    private async Task SaveReadingProgressAfterDelayAsync(
        CancellationTokenSource cancellation,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(1.5), cancellationToken);
            await SaveReadingProgressCoreAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            ShowStatus($"Reading progress could not be saved: {exception.Message}");
        }
        finally
        {
            Interlocked.CompareExchange(
                ref _progressSaveCancellation,
                null,
                cancellation);
            cancellation.Dispose();
        }
    }

    private static void CancelPendingSave(CancellationTokenSource? cancellation)
    {
        if (cancellation is null)
        {
            return;
        }

        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The delayed task owns disposal and may have completed concurrently.
        }
    }

    private async Task PersistFavoriteAsync(BookItemViewModel book)
    {
        try
        {
            await _services.Library
                .SetFavoriteAsync(book.Id, book.IsFavorite);
        }
        catch (Exception exception)
        {
            ShowStatus($"Favourite could not be saved: {exception.Message}");
        }
    }

    private async Task CreateCategoryAsync(string name)
    {
        try
        {
            await _services.Library
                .CreateCategoryAsync(name);
        }
        catch (Exception exception)
        {
            ShowStatus($"Category could not be saved: {exception.Message}");
        }
    }

    private async Task PersistBookMetadataAsync(BookItemViewModel book)
    {
        try
        {
            await _services.Library
                .UpdateMetadataAsync(
                    book.Id,
                    book.Title,
                    book.Author,
                    book.Category);
        }
        catch (Exception exception)
        {
            ShowStatus($"Book details could not be saved: {exception.Message}");
        }
    }

    private async Task PersistAnnotationAsync(AnnotationItemViewModel annotation)
    {
        if (CurrentBook is null)
        {
            return;
        }

        try
        {
            await _services.Library
                .SaveAnnotationAsync(CurrentBook.Id, annotation);
        }
        catch (Exception exception)
        {
            ShowStatus($"Annotation could not be saved: {exception.Message}");
        }
    }

    private async Task DeleteAnnotationAsync(AnnotationItemViewModel annotation)
    {
        try
        {
            await _services.Library
                .DeleteAnnotationAsync(annotation.Id);
        }
        catch (Exception exception)
        {
            ShowStatus($"Annotation could not be removed: {exception.Message}");
        }
    }

    private static IBrush CreateBrush(string hex)
    {
        return new SolidColorBrush(Color.Parse(hex));
    }

    private sealed record ReaderSpeechSegment(
        string Locator,
        int CharacterOffset,
        string Text);

    private enum ReaderPanel
    {
        TableOfContents,
        Annotations,
        Appearance
    }
}
