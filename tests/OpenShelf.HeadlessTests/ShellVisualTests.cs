using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenShelf.App.Services;
using OpenShelf.App.ViewModels;
using OpenShelf.App.Views;
using Xunit;

namespace OpenShelf.HeadlessTests;

public sealed class ShellVisualTests
{
    [AvaloniaFact]
    public void Close_confirmation_renders_the_save_message_and_three_choices()
    {
        var dialog = new CloseConfirmationDialog();
        dialog.Show();
        Dispatcher.UIThread.RunJobs();

        var visibleText = dialog
            .GetVisualDescendants()
            .OfType<TextBlock>()
            .Where(control => control.IsVisible)
            .Select(control => control.Text ?? string.Empty)
            .ToArray();
        var buttonLabels = dialog
            .GetVisualDescendants()
            .OfType<Button>()
            .Where(button => button.IsVisible)
            .Select(button => button.Content?.ToString())
            .ToArray();

        Assert.Contains(
            "Do you want to close? All progress will be saved.",
            visibleText);
        Assert.Contains("Yes", buttonLabels);
        Assert.Contains("No", buttonLabels);
        Assert.Contains("Cancel", buttonLabels);

        var frame = Assert.IsType<WriteableBitmap>(dialog.CaptureRenderedFrame());
        SaveSnapshot(frame, "close-confirmation.png");
        dialog.Close();
    }

    [AvaloniaFact]
    public async Task Library_cards_reflow_between_wide_and_narrow_windows()
    {
        await using var services = AppCompositionRoot.CreatePreview();
        var viewModel = new ShellViewModel(services);
        viewModel.LoadSampleLibraryCommand.Execute(null);
        var window = CreateWindow(viewModel, width: 1280, height: 820);

        window.Show();
        var wideFrame = window.CaptureRenderedFrame();
        Assert.NotNull(wideFrame);
        SaveSnapshot(wideFrame, "library-wide.png");
        var wideColumns = CountVisibleCardColumns(window);

        window.Width = 680;
        Dispatcher.UIThread.RunJobs();
        var narrowFrame = window.CaptureRenderedFrame();
        Assert.NotNull(narrowFrame);
        SaveSnapshot(narrowFrame, "library-narrow.png");
        var narrowColumns = CountVisibleCardColumns(window);

        Assert.True(wideColumns >= 3, $"Expected at least three wide columns, found {wideColumns}.");
        Assert.True(narrowColumns >= 1, "The narrow layout must keep a visible card column.");
        Assert.True(narrowColumns < wideColumns, $"Expected fewer narrow columns ({narrowColumns}) than wide columns ({wideColumns}).");
        window.Close();
    }

    [AvaloniaFact]
    public async Task Library_theme_choices_apply_and_render_distinct_frames()
    {
        await using var services = AppCompositionRoot.CreatePreview();
        var viewModel = new ShellViewModel(services);
        viewModel.LoadSampleLibraryCommand.Execute(null);
        var window = CreateWindow(viewModel, width: 1080, height: 760);
        window.Show();

        viewModel.SelectedLibraryTheme = "Dark";
        Dispatcher.UIThread.RunJobs();
        var darkFrame = Assert.IsType<WriteableBitmap>(window.CaptureRenderedFrame());
        SaveSnapshot(darkFrame, "library-dark.png");
        var darkHash = HashPixels(darkFrame);
        Assert.Equal(ThemeVariant.Dark, Application.Current?.RequestedThemeVariant);

        viewModel.SelectedLibraryTheme = "Light";
        Dispatcher.UIThread.RunJobs();
        var lightFrame = Assert.IsType<WriteableBitmap>(window.CaptureRenderedFrame());
        SaveSnapshot(lightFrame, "library-light.png");
        var lightHash = HashPixels(lightFrame);
        Assert.Equal(ThemeVariant.Light, Application.Current?.RequestedThemeVariant);
        Assert.NotEqual(darkHash, lightHash);

        viewModel.SelectedLibraryTheme = "System";
        Assert.Equal(ThemeVariant.Default, Application.Current?.RequestedThemeVariant);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Reader_renders_selectable_native_text_with_accessible_controls()
    {
        await using var services = AppCompositionRoot.CreatePreview();
        var viewModel = new ShellViewModel(services);
        viewModel.LoadSampleLibraryCommand.Execute(null);
        var window = CreateWindow(viewModel, width: 1180, height: 820);
        window.Show();

        viewModel.OpenBookCommand.Execute(viewModel.VisibleBooks[0]);
        await WaitForAsync(() => viewModel.IsReaderOpen);
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        SaveSnapshot(frame, "reader.png");

        var selectableText = window
            .GetVisualDescendants()
            .OfType<SelectableTextBlock>()
            .Where(control => control.IsVisible)
            .ToArray();
        Assert.NotEmpty(selectableText);
        Assert.Contains(
            selectableText,
            control => AutomationProperties.GetName(control) is "Book heading" or "Book paragraph");

        foreach (var highlighted in selectableText.OfType<HighlightedSelectableTextBlock>())
        {
            var renderedText = string.Concat(
                highlighted.Inlines?.OfType<Run>().Select(run => run.Text)
                    ?? Enumerable.Empty<string>());
            Assert.Equal(highlighted.SourceText, renderedText);
            Assert.True(string.IsNullOrEmpty(highlighted.Text));
        }

        var accessibleNames = window
            .GetVisualDescendants()
            .OfType<Control>()
            .Select(AutomationProperties.GetName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("Back to library", accessibleNames);
        Assert.Contains("Table of contents", accessibleNames);
        Assert.Contains("Add bookmark", accessibleNames);
        Assert.Contains("Toggle fullscreen", accessibleNames);
        Assert.Contains("Text to speech speed", accessibleNames);
        Assert.Contains(
            window.GetVisualDescendants().OfType<Button>(),
            button => button.IsVisible
                && button.IsEnabled
                && string.Equals(
                    button.Content?.ToString(),
                    "Read",
                    StringComparison.Ordinal));

        var visibleText = window
            .GetVisualDescendants()
            .OfType<TextBlock>()
            .Where(control => control.IsVisible)
            .Select(control => control.Text ?? string.Empty);
        Assert.DoesNotContain(visibleText, text => text.Contains("Priority Support", StringComparison.OrdinalIgnoreCase));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Paged_epub_renders_a_two_page_spread_with_centred_navigation_icons()
    {
        await using var services = AppCompositionRoot.CreatePreview();
        var viewModel = new ShellViewModel(services);
        viewModel.LoadSampleLibraryCommand.Execute(null);
        var window = CreateWindow(viewModel, width: 1600, height: 900);
        window.Show();

        viewModel.OpenBookCommand.Execute(viewModel.VisibleBooks[0]);
        await WaitForAsync(() => viewModel.IsReaderOpen);
        viewModel.SelectedReaderLayoutMode = "Paged";
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(2, viewModel.ReaderPageColumns);
        Assert.Equal(2, viewModel.DisplayedReaderPages.Count);
        var visiblePages = window
            .GetVisualDescendants()
            .OfType<Border>()
            .Where(border => border.IsVisible)
            .Select(AutomationProperties.GetName)
            .Where(name => name?.StartsWith("Reading page ", StringComparison.Ordinal) == true)
            .ToArray();
        Assert.Equal(2, visiblePages.Length);

        var pageButtons = window
            .GetVisualDescendants()
            .OfType<Button>()
            .Where(
                button => AutomationProperties.GetName(button) is
                    "Previous reading page" or "Next reading page")
            .ToArray();
        Assert.Equal(2, pageButtons.Length);
        Assert.All(
            pageButtons,
            button =>
            {
                Assert.Equal(HorizontalAlignment.Center, button.HorizontalContentAlignment);
                Assert.Equal(VerticalAlignment.Center, button.VerticalContentAlignment);
            });

        var frame = Assert.IsType<WriteableBitmap>(window.CaptureRenderedFrame());
        SaveSnapshot(frame, "reader-paged-spread.png");
        window.Close();
    }

    [AvaloniaFact]
    public async Task Settings_drawer_renders_each_primary_section_with_accessible_tabs()
    {
        await using var services = AppCompositionRoot.CreatePreview();
        var viewModel = new ShellViewModel(services);
        viewModel.LoadSampleLibraryCommand.Execute(null);
        var window = CreateWindow(viewModel, width: 1180, height: 820);
        window.Show();

        viewModel.OpenBookCommand.Execute(viewModel.VisibleBooks[0]);
        await WaitForAsync(() => viewModel.IsReaderOpen);
        viewModel.ToggleAppearanceCommand.Execute(null);
        await WaitForAsync(() => viewModel.IsAppearanceOpen);

        var settings = Assert.Single(
            window.GetVisualDescendants().OfType<TabControl>(),
            control =>
                AutomationProperties.GetName(control) == "Settings sections");
        var expectedTabNames = new[]
        {
            "System settings tab",
            "Reading settings tab",
            "Text to speech settings tab",
            "Hotkeys settings tab"
        };
        var accessibleTabNames = window
            .GetLogicalDescendants()
            .OfType<TabItem>()
            .Select(AutomationProperties.GetName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.Ordinal);
        Assert.Equal(4, expectedTabNames.Count(accessibleTabNames.Contains));

        SelectSettingsTabAndSave(window, settings, 0, "settings-system.png");
        SelectSettingsTabAndSave(window, settings, 1, "settings-reading.png");
        SelectSettingsTabAndSave(window, settings, 2, "settings-speech.png");

        var speechAccessibleNames = window
            .GetLogicalDescendants()
            .OfType<Control>()
            .Select(AutomationProperties.GetName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("Preview selected text to speech voice", speechAccessibleNames);
        Assert.Contains("Refresh installed text to speech voices", speechAccessibleNames);
        Assert.Contains("Text to speech reading volume", speechAccessibleNames);
        Assert.Contains("Text to speech reading pitch", speechAccessibleNames);

        var speechToggles = window
            .GetLogicalDescendants()
            .OfType<ToggleSwitch>()
            .Where(
                toggle => AutomationProperties.GetName(toggle) is
                    "Highlight spoken sentence" or "Follow spoken sentence")
            .ToArray();
        Assert.Equal(
            new[] { "Follow spoken sentence", "Highlight spoken sentence" },
            speechToggles
                .Select(AutomationProperties.GetName)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal));
        Assert.All(speechToggles, toggle => Assert.True(toggle.IsChecked));

        var settingsCopy = window
            .GetLogicalDescendants()
            .OfType<TextBlock>()
            .Select(control => control.Text ?? string.Empty)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .ToArray();
        var forbiddenPhrases = new[]
        {
            "Upgrade to PRO",
            "Priority Support",
            "Check for updates",
            "updates automatically",
            "anonymous usage",
            "usage stats",
            "external devices"
        };
        foreach (var phrase in forbiddenPhrases)
        {
            Assert.DoesNotContain(
                settingsCopy,
                text => text.Contains(phrase, StringComparison.OrdinalIgnoreCase));
        }

        window.Close();
    }

    private static ShellWindow CreateWindow(
        ShellViewModel viewModel,
        double width,
        double height) =>
        new()
        {
            Width = width,
            Height = height,
            DataContext = viewModel,
            IsCloseConfirmationEnabled = false
        };

    private static int CountVisibleCardColumns(ShellWindow window)
    {
        var xCoordinates = window
            .GetVisualDescendants()
            .OfType<Button>()
            .Where(button => button.IsVisible && button.Classes.Contains("card"))
            .Select(button => button.TranslatePoint(default, window)?.X)
            .Where(position => position.HasValue)
            .Select(position => Math.Round(position!.Value, 0))
            .Distinct()
            .ToArray();
        return xCoordinates.Length;
    }

    private static void SelectSettingsTabAndSave(
        ShellWindow window,
        TabControl settings,
        int selectedIndex,
        string fileName)
    {
        settings.SelectedIndex = selectedIndex;
        Dispatcher.UIThread.RunJobs();
        var frame = Assert.IsType<WriteableBitmap>(
            window.CaptureRenderedFrame());
        SaveSnapshot(frame, fileName);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100 && !condition(); attempt++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Yield();
        }

        Assert.True(condition(), "The expected UI state was not reached.");
    }

    private static string HashPixels(WriteableBitmap bitmap)
    {
        using var framebuffer = bitmap.Lock();
        var pixels = new byte[checked(framebuffer.RowBytes * framebuffer.Size.Height)];
        Marshal.Copy(framebuffer.Address, pixels, 0, pixels.Length);
        return Convert.ToHexString(SHA256.HashData(pixels));
    }

    private static void SaveSnapshot(WriteableBitmap bitmap, string fileName)
    {
        var outputDirectory = Environment.GetEnvironmentVariable("OPENSHELF_SNAPSHOT_DIR");
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            outputDirectory = Path.Combine(
                Environment.CurrentDirectory,
                "artifacts",
                "headless");
        }

        Directory.CreateDirectory(outputDirectory);
        bitmap.Save(
            Path.Combine(outputDirectory, fileName),
            new PngBitmapEncoderOptions());
    }
}
