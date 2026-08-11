using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Moz_Avalonia.Services;
using Moz_Avalonia.ViewModels;
using Moz_Avalonia.Views;

namespace Moz_Avalonia;

public partial class App : Application
{
    private MainViewModel? _mainViewModel;
    public static IVpnServiceManager? VpnManager { get; set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        _mainViewModel = new MainViewModel();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            VpnManager = new DesktopVpnServiceManager();
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            desktop.Exit += (_, _) => DesktopIntegrationService.ClearWindowsSystemProxyOnExit();
            desktop.MainWindow = new MainWindow
            {
                DataContext = _mainViewModel,
            };
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleViewPlatform)
        {
            singleViewPlatform.MainView = new MainContainerView
            {
                DataContext = _mainViewModel
            };
        }

        _ = _mainViewModel.InitializeAsync();

        base.OnFrameworkInitializationCompleted();
    }

    private void TrayIconClicked(object? sender, EventArgs e) => ShowMainWindow();

    private void OpenMenuClicked(object? sender, EventArgs e) => ShowMainWindow();

    private void ShowMainWindow()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: MainWindow window })
            window.ShowFromTray();
    }

    private async void ExitMenuClicked(object? sender, EventArgs e)
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;
        if (desktop.MainWindow is MainWindow window) await window.ExitAsync();
        desktop.Shutdown();
    }
}
