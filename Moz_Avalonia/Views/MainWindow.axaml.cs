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
    private bool _trayHintShown;
    private bool _isClosingDialogOpen;

    private enum CloseActionChoice
    {
        Cancel,
        MinimizeToTray,
        ExitEntirely
    }

    public MainWindow()
    {
        InitializeComponent();
        Closing += OnClosing;
        PropertyChanged += (_, change) =>
        {
            if (change.Property != WindowStateProperty || DataContext is not MainViewModel viewModel) return;

            // Do not call Hide() while KDE/Windows is transitioning the native surface to a
            // minimized state. Mixing both transitions can restore an empty compositor surface.
            // A minimized window remains owned by the window manager; only Close hides to tray.
            if (WindowState == WindowState.Minimized) viewModel.SetUiVisible(false);
            else if (IsVisible) viewModel.SetUiVisible(true);
        };
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
        return await dialog.ShowDialog<bool>(IsVisible ? this : null!);
    }

    public void ShowFromTray()
    {
        if (_shutdownReady) return;

        try
        {
            if (!IsVisible)
            {
                Show();
                WindowState = WindowState.Normal;
            }
            else if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }

            Activate();
            if (DataContext is MainViewModel viewModel) viewModel.SetUiVisible(true);
        }
        catch (System.InvalidOperationException ex)
        {
            MozUtil.Logger.Log($"[MainWindow] Cannot show window: {ex.Message}");
        }
    }

    private void HideToTray()
    {
        Hide();
        if (DataContext is not MainViewModel viewModel) return;
        viewModel.SetUiVisible(false);
        if (_trayHintShown) return;
        _trayHintShown = true;
        viewModel.NotifyBackgroundMode();
    }

    public async Task ExitAsync()
    {
        if (_shutdownReady) return;
        _shutdownReady = true;

        if (DataContext is MainViewModel viewModel)
        {
            await viewModel.DisposeAsync();
        }

        // Hide and clear tray icons immediately so DBus / Taskbar tray icon vanishes instantly
        try
        {
            if (Application.Current != null)
            {
                var trayIcons = TrayIcon.GetIcons(Application.Current);
                if (trayIcons != null)
                {
                    foreach (var icon in trayIcons)
                    {
                        icon.IsVisible = false;
                    }
                    trayIcons.Clear();
                }
            }
        }
        catch {}

        if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
        else
        {
            Close();
        }
    }

    private async Task<CloseActionChoice> PromptCloseActionAsync()
    {
        var result = CloseActionChoice.Cancel;
        var minimizeBtn = new Button { Content = "Minimize to Tray", Classes = { "accent" }, MinWidth = 120 };
        var exitBtn = new Button { Content = "Close Entirely", Classes = { "danger" }, MinWidth = 110 };
        var cancelBtn = new Button { Content = "Cancel", MinWidth = 80 };

        var dialog = new Window
        {
            Title = "Close MozVPN",
            Width = 460,
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
                    new StackPanel
                    {
                        VerticalAlignment = VerticalAlignment.Center,
                        Spacing = 6,
                        Children =
                        {
                            new TextBlock { Text = "What would you like to do?", FontWeight = FontWeight.Bold, FontSize = 15 },
                            new TextBlock { Text = "You can minimize MozVPN to the system tray to keep active connections alive, or close it entirely.", TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#B8CAD9"), FontSize = 12 }
                        }
                    },
                    new StackPanel
                    {
                        [Grid.RowProperty] = 1,
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { cancelBtn, minimizeBtn, exitBtn }
                    }
                }
            }
        };

        cancelBtn.Click += (_, _) =>
        {
            result = CloseActionChoice.Cancel;
            dialog.Close();
        };
        minimizeBtn.Click += (_, _) =>
        {
            result = CloseActionChoice.MinimizeToTray;
            dialog.Close();
        };
        exitBtn.Click += (_, _) =>
        {
            result = CloseActionChoice.ExitEntirely;
            dialog.Close();
        };

        await dialog.ShowDialog(IsVisible ? this : null!);
        return result;
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_shutdownReady) return;
        e.Cancel = true;

        if (_isClosingDialogOpen) return;
        _isClosingDialogOpen = true;

        try
        {
            var choice = await PromptCloseActionAsync();
            if (choice == CloseActionChoice.MinimizeToTray)
            {
                HideToTray();
            }
            else if (choice == CloseActionChoice.ExitEntirely)
            {
                await ExitAsync();
            }
        }
        finally
        {
            _isClosingDialogOpen = false;
        }
    }
}
