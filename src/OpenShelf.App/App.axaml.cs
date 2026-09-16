using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using OpenShelf.App.Services;
using OpenShelf.App.ViewModels;
using OpenShelf.App.Views;

namespace OpenShelf.App;

public sealed partial class App : Application
{
    private AppServices? _services;
    private IClassicDesktopStyleApplicationLifetime? _desktopLifetime;
    private Window? _startupWindow;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktopLifetime = desktop;
            _startupWindow = new Window
            {
                Title = "OpenShelf Reader",
                Width = 420,
                Height = 180,
                CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Content = new TextBlock
                {
                    Text = "Opening OpenShelf Reader…",
                    FontSize = 18,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            _startupWindow.Opened += StartupWindow_Opened;
            desktop.MainWindow = _startupWindow;
            desktop.Exit += OnDesktopExit;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async void StartupWindow_Opened(object? sender, EventArgs e)
    {
        var startupWindow = _startupWindow;
        var desktopLifetime = _desktopLifetime;
        if (startupWindow is null || desktopLifetime is null)
        {
            return;
        }

        startupWindow.Opened -= StartupWindow_Opened;
        try
        {
            _services = await AppCompositionRoot.CreateForApplicationAsync();
            var viewModel = new ShellViewModel(_services);
            await viewModel.InitializeAsync();

            var mainWindow = new ShellWindow
            {
                DataContext = viewModel,
                WindowState = WindowState.Maximized
            };
            desktopLifetime.MainWindow = mainWindow;
            mainWindow.Show();
            startupWindow.Close();
            _startupWindow = null;
        }
        catch (Exception exception)
        {
            if (startupWindow.Content is TextBlock status)
            {
                status.Text = $"OpenShelf Reader could not start.\n\n{exception.Message}";
                status.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
                status.Margin = new Thickness(24);
            }
        }
    }

    private void OnDesktopExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        _services?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _services = null;
    }
}
