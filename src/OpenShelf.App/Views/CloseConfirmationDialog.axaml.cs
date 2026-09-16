using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace OpenShelf.App.Views;

public enum CloseConfirmationResult
{
    Cancel,
    No,
    Yes
}

public sealed partial class CloseConfirmationDialog : Window
{
    public CloseConfirmationDialog()
    {
        InitializeComponent();
    }

    private void Yes_Click(object? sender, RoutedEventArgs e)
    {
        Close(CloseConfirmationResult.Yes);
    }

    private void No_Click(object? sender, RoutedEventArgs e)
    {
        Close(CloseConfirmationResult.No);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        Close(CloseConfirmationResult.Cancel);
    }

    private void Dialog_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        e.Handled = true;
        Close(CloseConfirmationResult.Cancel);
    }
}
