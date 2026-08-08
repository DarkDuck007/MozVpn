using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Moz_Avalonia.Services;
using Moz_Avalonia.ViewModels;

namespace Moz_Avalonia.Views;

public partial class MainWindow : Window
{
    private bool _shutdownReady;

    public MainWindow()
    {
        InitializeComponent();
        Closing += OnClosing;
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel viewModel) viewModel.ConfirmAsync = ShowConfirmationAsync;
            if (DataContext is MainViewModel stunViewModel)
                stunViewModel.ChooseStunNatGroupAsync = ShowStunNatGroupDialogAsync;
        };
    }

    private async Task<StunNatGroup?> ShowStunNatGroupDialogAsync(
        System.Collections.Generic.IReadOnlyList<StunNatGroup> groups)
    {
        var dialog = new StunNatChoiceDialog(groups);
        return await dialog.ShowDialog<StunNatGroup?>(this);
    }

    private async Task<bool> ShowConfirmationAsync(string title, string message, string acceptText)
    {
        var accept = new Button { Content = acceptText, Classes = { "danger" }, MinWidth = 88 };
        var cancel = new Button { Content = "Cancel", MinWidth = 88 };
        var dialog = new Window
        {
            Title = title,
            Width = 420,
            Height = 180,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brush.Parse("#101D29"),
            Content = new Grid
            {
                RowDefinitions = RowDefinitions.Parse("*,Auto"),
                Margin = new Thickness(20),
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center },
                    new StackPanel
                    {
                        [Grid.RowProperty] = 1,
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { cancel, accept }
                    }
                }
            }
        };
        cancel.Click += (_, _) => dialog.Close(false);
        accept.Click += (_, _) => dialog.Close(true);
        return await dialog.ShowDialog<bool>(this);
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_shutdownReady || DataContext is not MainViewModel viewModel) return;
        e.Cancel = true;
        try { await viewModel.DisposeAsync(); }
        finally
        {
            _shutdownReady = true;
            Close();
        }
    }
}
