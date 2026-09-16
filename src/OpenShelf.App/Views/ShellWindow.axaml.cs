using System;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenShelf.App.Services;
using OpenShelf.App.ViewModels;

namespace OpenShelf.App.Views;

public sealed partial class ShellWindow : Window
{
    private WindowState _windowStateBeforeFullscreen = WindowState.Normal;
    private ShellViewModel? _subscribedViewModel;
    private bool _closeConfirmed;
    private bool _closeConfirmationOpen;

    public ShellWindow()
    {
        InitializeComponent();
    }

    public bool IsCloseConfirmationEnabled { get; set; } = true;

    private ShellViewModel? ViewModel => DataContext as ShellViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_subscribedViewModel is not null)
        {
            _subscribedViewModel.ReaderNavigationRequested -=
                ViewModel_ReaderNavigationRequested;
            _subscribedViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        }

        base.OnDataContextChanged(e);
        _subscribedViewModel = ViewModel;
        if (_subscribedViewModel is not null)
        {
            _subscribedViewModel.ReaderNavigationRequested +=
                ViewModel_ReaderNavigationRequested;
            _subscribedViewModel.PropertyChanged += ViewModel_PropertyChanged;
        }
    }

    private async void ImportFiles_Click(object? sender, RoutedEventArgs e)
    {
        if (!StorageProvider.CanOpen)
        {
            ViewModel?.OpenImportPanel();
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title = "Add ebooks to OpenShelf",
                AllowMultiple = true,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Supported ebooks")
                    {
                        Patterns = new[]
                        {
                            "*.epub",
                            "*.pdf",
                            "*.mobi",
                            "*.azw3",
                            "*.fb2",
                            "*.txt",
                            "*.rtf",
                            "*.docx"
                        }
                    },
                    FilePickerFileTypes.All
                }
            });

        var paths = files
            .Select(file => file.TryGetLocalPath())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Cast<string>()
            .ToArray();

        if (paths.Length == 0)
        {
            ViewModel?.OpenImportPanel();
            return;
        }

        if (ViewModel is not null)
        {
            await ViewModel.ImportAsync(paths);
        }
    }

    private async void ImportFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (!StorageProvider.CanPickFolder)
        {
            ViewModel?.OpenImportPanel();
            return;
        }

        var folders = await StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions
            {
                Title = "Choose an ebook folder",
                AllowMultiple = true
            });

        var paths = folders
            .Select(folder => folder.TryGetLocalPath())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Cast<string>()
            .ToArray();

        if (paths.Length == 0)
        {
            ViewModel?.OpenImportPanel();
            return;
        }

        if (ViewModel is not null)
        {
            await ViewModel.ImportAsync(paths);
        }
    }

    private void Fullscreen_Click(object? sender, RoutedEventArgs e)
    {
        ToggleFullscreen();
    }

    private void ToggleFullscreen()
    {
        if (WindowState == WindowState.FullScreen)
        {
            WindowState = _windowStateBeforeFullscreen;
            return;
        }

        _windowStateBeforeFullscreen = WindowState;
        WindowState = WindowState.FullScreen;
    }

    private async void Window_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11)
        {
            ToggleFullscreen();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            if (ImageZoomOverlay.IsVisible)
            {
                CloseImageZoom();
            }
            else if (WindowState == WindowState.FullScreen)
            {
                WindowState = _windowStateBeforeFullscreen;
            }
            else if (ViewModel?.IsNoteComposerOpen == true)
            {
                ViewModel.CancelNoteCommand.Execute(null);
            }
            else if (ViewModel?.IsReaderPasswordPromptOpen == true)
            {
                ViewModel.CancelReaderPasswordCommand.Execute(null);
            }
            else if (ViewModel?.IsBookEditorOpen == true)
            {
                ViewModel.CancelBookEditorCommand.Execute(null);
            }
            else if (ViewModel?.IsHelpOpen == true)
            {
                ViewModel.ToggleHelpCommand.Execute(null);
            }
            else if (ViewModel?.IsTocOpen == true)
            {
                ViewModel.ToggleTocCommand.Execute(null);
            }
            else if (ViewModel?.IsAnnotationsOpen == true)
            {
                ViewModel.ToggleAnnotationsCommand.Execute(null);
            }
            else if (ViewModel?.IsAppearanceOpen == true)
            {
                ViewModel.ToggleAppearanceCommand.Execute(null);
            }

            e.Handled = true;
            return;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.O)
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            {
                ImportFolder_Click(this, new RoutedEventArgs());
            }
            else
            {
                ImportFiles_Click(this, new RoutedEventArgs());
            }

            e.Handled = true;
            return;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.B)
        {
            ViewModel?.AddBookmarkCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.C)
        {
            await CopyCurrentSelectionAsync();
            e.Handled = true;
            return;
        }

        if (ViewModel?.IsReaderOpen == true
            && ViewModel.IsPagedMode
            && e.KeyModifiers == KeyModifiers.None
            && e.Source is not TextBox
            && e.Source is not ComboBox
            && e.Source is not Slider
            && e.Source is not Button)
        {
            if (e.Key is Key.Left or Key.Up or Key.PageUp or Key.Back)
            {
                if (ViewModel.PreviousPageCommand.CanExecute(null))
                {
                    ViewModel.PreviousPageCommand.Execute(null);
                }

                e.Handled = true;
                return;
            }

            if (e.Key is Key.Right or Key.Down or Key.PageDown or Key.Space)
            {
                if (ViewModel.NextPageCommand.CanExecute(null))
                {
                    ViewModel.NextPageCommand.Execute(null);
                }

                e.Handled = true;
                return;
            }
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt)
            && e.Key == Key.Left
            && ViewModel?.IsReaderOpen == true)
        {
            ViewModel.CloseReaderCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void SearchBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && ViewModel is not null)
        {
            ViewModel.SearchText = string.Empty;
            e.Handled = true;
        }
    }

    private void ReaderText_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is not SelectableTextBlock textBlock || ViewModel is null)
        {
            return;
        }

        var sourceText = textBlock is HighlightedSelectableTextBlock highlighted
            ? highlighted.SourceText
            : textBlock.Text ?? string.Empty;
        var start = ReadSelectionIndex(textBlock, "SelectionStart");
        var end = ReadSelectionIndex(textBlock, "SelectionEnd");

        if (start < 0 || end < 0 || start == end || sourceText.Length == 0)
        {
            ViewModel.ClearReaderSelection();
            return;
        }

        var lower = Math.Clamp(Math.Min(start, end), 0, sourceText.Length);
        var upper = Math.Clamp(Math.Max(start, end), 0, sourceText.Length);
        var selectedText = sourceText.Substring(lower, upper - lower);
        var sourceOffset = textBlock.Tag is ReaderTextBlockViewModel textModel
            ? textModel.SourceOffset
            : 0;
        ViewModel.UpdateReaderSelection(
            selectedText,
            textBlock.Tag is ReaderTextBlockViewModel selectionModel
                ? selectionModel.Locator
                : textBlock.Tag?.ToString() ?? string.Empty,
            sourceOffset + lower,
            upper - lower);
    }

    private void ReaderText_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not SelectableTextBlock textBlock || ViewModel is null)
        {
            return;
        }

        // SelectableTextBlock applies its double-click word selection during the
        // same input pass. Reading it on the next dispatcher turn preserves the
        // normal selection/context-menu behavior while locating the sentence.
        Dispatcher.UIThread.Post(
            async () =>
            {
                var start = ReadSelectionIndex(textBlock, "SelectionStart");
                if (start < 0)
                {
                    return;
                }

                var sourceOffset = textBlock.Tag is ReaderTextBlockViewModel textModel
                    ? textModel.SourceOffset
                    : 0;
                await ViewModel.StartSpeechAtAsync(
                    textBlock.Tag is ReaderTextBlockViewModel speechModel
                        ? speechModel.Locator
                        : textBlock.Tag?.ToString() ?? string.Empty,
                    sourceOffset + start);
            },
            DispatcherPriority.Input);
        e.Handled = true;
    }

    private static int ReadSelectionIndex(SelectableTextBlock textBlock, string propertyName)
    {
        var property = textBlock
            .GetType()
            .GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
        var value = property?.GetValue(textBlock);
        return value is int index ? index : -1;
    }

    private async void CopySelection_Click(object? sender, RoutedEventArgs e)
    {
        await CopyCurrentSelectionAsync();
    }

    private async Task CopyCurrentSelectionAsync()
    {
        if (ViewModel is null || !ViewModel.HasSelection || Clipboard is null)
        {
            return;
        }

        await Clipboard.SetTextAsync(ViewModel.SelectedReaderText);
        ViewModel.NotifyCopyCompleted();
    }

    private void HighlightSelection_Click(object? sender, RoutedEventArgs e)
    {
        ViewModel?.HighlightSelectionCommand.Execute(null);
    }

    private void AddNote_Click(object? sender, RoutedEventArgs e)
    {
        ViewModel?.OpenNoteComposerCommand.Execute(null);
        Dispatcher.UIThread.Post(() => NoteTextBox.Focus());
    }

    private void ReaderImage_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border { Tag: ReaderImageBlockViewModel imageBlock }
            || imageBlock.Image is null)
        {
            return;
        }

        ZoomedReaderImage.Source = imageBlock.Image;
        ImageZoomOverlay.IsVisible = true;
        e.Handled = true;
    }

    private void ReaderImage_AttachedToVisualTree(
        object? sender,
        VisualTreeAttachmentEventArgs e)
    {
        Dispatcher.UIThread.Post(
            () => _ = LoadVisibleReaderImagesAsync(),
            DispatcherPriority.Loaded);
    }

    private async Task LoadVisibleReaderImagesAsync()
    {
        var viewportHeight = Math.Max(ReaderScroller.Viewport.Height, Bounds.Height);
        var pending = ReaderScroller
            .GetVisualDescendants()
            .OfType<Border>()
            .Where(border => border.Tag is ReaderImageBlockViewModel)
            .Where(
                border =>
                {
                    var origin = border.TranslatePoint(new Point(0, 0), ReaderScroller);
                    return origin is { } point
                        && point.Y + border.Bounds.Height >= -viewportHeight
                        && point.Y <= viewportHeight * 2;
                })
            .Select(border => ((ReaderImageBlockViewModel)border.Tag!).LoadAsync())
            .ToArray();
        if (pending.Length > 0)
        {
            await Task.WhenAll(pending);
        }
    }

    private void ImageZoomOverlay_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        CloseImageZoom();
        e.Handled = true;
    }

    private void CloseImageZoom_Click(object? sender, RoutedEventArgs e)
    {
        CloseImageZoom();
        e.Handled = true;
    }

    private void CloseImageZoom()
    {
        ImageZoomOverlay.IsVisible = false;
        ZoomedReaderImage.Source = null;
    }

    private void ReaderScroller_ScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (ViewModel is null || ViewModel.IsPagedMode)
        {
            return;
        }

        _ = LoadVisibleReaderImagesAsync();
        var scrollableHeight = Math.Max(
            0,
            ReaderScroller.Extent.Height - ReaderScroller.Viewport.Height);
        if (scrollableHeight > 0
            && ReaderScroller.Offset.Y >= scrollableHeight - 1
            && ViewModel.ReaderBlocks.Count > 0)
        {
            var finalBlock = ViewModel.ReaderBlocks[^1];
            ViewModel.SetReaderPosition(finalBlock.Locator, 1);
            return;
        }

        if (ViewModel.IsFixedPageDocument
            && TryGetVisiblePdfPosition(out var pdfLocator, out var withinPage))
        {
            ViewModel.SetReaderPosition(pdfLocator, withinPage);
            return;
        }

        if (TryGetVisibleReaderPosition(out var locator, out var withinBlock))
        {
            ViewModel.SetReaderPosition(locator, withinBlock);
        }
    }

    private void ReaderSurface_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        ViewModel?.UpdateReaderViewport(e.NewSize.Width, e.NewSize.Height);
    }

    private bool TryGetVisibleReaderPosition(
        out string locator,
        out double withinBlock)
    {
        locator = string.Empty;
        withinBlock = 0;
        var viewportHeight = Math.Max(1, ReaderScroller.Viewport.Height);
        var candidate = ReaderScroller
            .GetVisualDescendants()
            .OfType<Control>()
            .Select(
                control => new
                {
                    Control = control,
                    Locator = GetControlLocator(control),
                    Origin = control.TranslatePoint(new Point(0, 0), ReaderScroller)
                })
            .Where(item => !string.IsNullOrWhiteSpace(item.Locator) && item.Origin is not null)
            .Where(
                item => item.Origin!.Value.Y + item.Control.Bounds.Height >= 0
                    && item.Origin.Value.Y <= viewportHeight)
            .OrderBy(
                item => item.Origin!.Value.Y <= 0
                    && item.Origin.Value.Y + item.Control.Bounds.Height > 0
                        ? 0
                        : 1)
            .ThenBy(item => Math.Abs(item.Origin!.Value.Y))
            .FirstOrDefault();
        if (candidate is null || candidate.Origin is null)
        {
            return false;
        }

        var height = Math.Max(1, candidate.Control.Bounds.Height);
        locator = candidate.Locator;
        withinBlock = Math.Clamp(-candidate.Origin.Value.Y / height, 0, 1);
        return true;
    }

    private bool TryGetVisiblePdfPosition(
        out string locator,
        out double withinPage)
    {
        locator = string.Empty;
        withinPage = 0;
        var viewportHeight = Math.Max(1, ReaderScroller.Viewport.Height);
        var page = ReaderScroller
            .GetVisualDescendants()
            .OfType<Control>()
            .Select(
                control => new
                {
                    Control = control,
                    Locator = GetControlLocator(control),
                    Origin = control.TranslatePoint(new Point(0, 0), ReaderScroller)
                })
            .Where(
                item => item.Origin is not null
                    && ReaderPositionLocator.TryParsePdf(
                        item.Locator,
                        out _,
                        out _))
            .GroupBy(item => ReaderPositionLocator.GetBaseLocator(item.Locator))
            .Select(
                group => new
                {
                    Locator = group.Key,
                    Top = group.Min(item => item.Origin!.Value.Y),
                    Bottom = group.Max(
                        item => item.Origin!.Value.Y + item.Control.Bounds.Height)
                })
            .Where(item => item.Bottom >= 0 && item.Top <= viewportHeight)
            .OrderBy(
                item => item.Top <= 0 && item.Bottom > 0 ? 0 : 1)
            .ThenBy(item => Math.Abs(item.Top))
            .FirstOrDefault();
        if (page is null)
        {
            return false;
        }

        locator = page.Locator;
        withinPage = Math.Clamp(-page.Top / Math.Max(1, page.Bottom - page.Top), 0, 1);
        return true;
    }

    private static string GetControlLocator(Control control)
    {
        return control.Tag switch
        {
            ReaderTextBlockViewModel text =>
                ReaderPositionLocator.FormatReflowable(
                    text.Locator,
                    text.SourceOffset),
            ReaderBlockViewModel block => block.Locator,
            string value => value,
            _ => string.Empty
        };
    }

    private async void CopyDiagnostics_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null || Clipboard is null)
        {
            return;
        }

        await Clipboard.SetTextAsync(ViewModel.DiagnosticsText);
    }

    private void RefreshDiagnostics_Click(object? sender, RoutedEventArgs e)
    {
        ViewModel?.RefreshDiagnostics();
    }

    private void DismissStatus_Click(object? sender, RoutedEventArgs e)
    {
        ViewModel?.DismissStatus();
    }

    private void ViewModel_ReaderNavigationRequested(
        object? sender,
        ReaderNavigationRequest request)
    {
        Dispatcher.UIThread.Post(
            () => NavigateReader(request),
            DispatcherPriority.Loaded);
    }

    private void ViewModel_PropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.IsPasswordPromptOpen)
            && ViewModel?.IsPasswordPromptOpen == true)
        {
            Dispatcher.UIThread.Post(
                () => ImportPasswordBox.Focus(),
                DispatcherPriority.Loaded);
        }
        else if (e.PropertyName == nameof(ShellViewModel.IsReaderPasswordPromptOpen)
                 && ViewModel?.IsReaderPasswordPromptOpen == true)
        {
            Dispatcher.UIThread.Post(
                () => ReaderPasswordBox.Focus(),
                DispatcherPriority.Loaded);
        }
    }

    private void NavigateReader(ReaderNavigationRequest request)
    {
        if (ViewModel is null)
        {
            return;
        }

        var positionLocator = string.IsNullOrWhiteSpace(request.Locator)
            ? ViewModel.GetReaderLocatorAtProgress(request.Progress)
            : request.Locator;
        if (!string.IsNullOrWhiteSpace(positionLocator))
        {
            var targetLocator = ReaderPositionLocator.GetBaseLocator(positionLocator);
            var isSectionTarget = targetLocator.EndsWith('|');
            var target = ReaderScroller
                .GetVisualDescendants()
                .OfType<Control>()
                .FirstOrDefault(
                    control =>
                        string.Equals(
                            ReaderPositionLocator.GetBaseLocator(
                                GetControlLocator(control)),
                            targetLocator,
                            StringComparison.Ordinal)
                        || isSectionTarget
                        && ReaderPositionLocator.GetBaseLocator(
                                GetControlLocator(control))
                            .StartsWith(targetLocator, StringComparison.Ordinal));
            if (target is not null)
            {
                target.BringIntoView();
                var withinPosition = ViewModel.GetReaderPositionFraction(positionLocator);
                if (withinPosition > 0)
                {
                    Dispatcher.UIThread.Post(
                        () =>
                        {
                            var matchingControls = ReaderScroller
                                .GetVisualDescendants()
                                .OfType<Control>()
                                .Where(
                                    control => string.Equals(
                                            ReaderPositionLocator.GetBaseLocator(
                                                GetControlLocator(control)),
                                            targetLocator,
                                            StringComparison.Ordinal)
                                        || isSectionTarget
                                        && ReaderPositionLocator.GetBaseLocator(
                                                GetControlLocator(control))
                                            .StartsWith(
                                                targetLocator,
                                                StringComparison.Ordinal))
                                .Select(
                                    control => new
                                    {
                                        Control = control,
                                        Origin = control.TranslatePoint(
                                            new Point(0, 0),
                                            ReaderScroller)
                                    })
                                .Where(item => item.Origin is not null)
                                .ToArray();
                            var span = matchingControls.Length == 0
                                ? Math.Max(1, target.Bounds.Height)
                                : Math.Max(
                                    1,
                                    matchingControls.Max(
                                        item => item.Origin!.Value.Y
                                            + item.Control.Bounds.Height)
                                    - matchingControls.Min(
                                        item => item.Origin!.Value.Y));
                            ReaderScroller.Offset = new Vector(
                                ReaderScroller.Offset.X,
                                ReaderScroller.Offset.Y
                                    + Math.Clamp(withinPosition, 0, 1) * span);
                        },
                        DispatcherPriority.Loaded);
                }

                return;
            }
        }

        ReaderScroller.Offset = new Vector(ReaderScroller.Offset.X, 0);
    }

    private async void Window_Closing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeConfirmed)
        {
            return;
        }

        if (!IsCloseConfirmationEnabled)
        {
            await FlushStateBeforeClosingAsync();
            return;
        }

        e.Cancel = true;
        if (_closeConfirmationOpen)
        {
            return;
        }

        _closeConfirmationOpen = true;
        try
        {
            var confirmation = new CloseConfirmationDialog();
            var result = await confirmation
                .ShowDialog<CloseConfirmationResult>(this);
            if (result != CloseConfirmationResult.Yes)
            {
                return;
            }

            if (!await FlushStateBeforeClosingAsync())
            {
                return;
            }

            _closeConfirmed = true;
            Close();
        }
        finally
        {
            _closeConfirmationOpen = false;
        }
    }

    private async Task<bool> FlushStateBeforeClosingAsync()
    {
        try
        {
            if (ViewModel is not null)
            {
                await ViewModel.FlushAllStateAsync();
            }

            return true;
        }
        catch (Exception exception)
        {
            ViewModel?.ReportStateSaveFailure(exception.Message);
            return false;
        }
    }
}
