using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Moz_Avalonia.ViewModels;
using Moz_Avalonia.Views;

namespace Moz_Avalonia;

public partial class App : Application
{
    private MainViewModel? _mainViewModel;
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _mainViewModel = new MainViewModel();
            desktop.MainWindow = new MainWindow
            {
                DataContext = _mainViewModel,
            };
            _ = _mainViewModel.InitializeAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
