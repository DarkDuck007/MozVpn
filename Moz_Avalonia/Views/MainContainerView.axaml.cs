using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Moz_Avalonia.Views;

public partial class MainContainerView : UserControl
{
    private UserControl? _currentView;

    public MainContainerView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_currentView != null)
            {
                _currentView.DataContext = DataContext;
            }
        };
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateLayoutForSize(e.NewSize.Width);
    }

    private void UpdateLayoutForSize(double width)
    {
        bool isMobile = width < 750;

        if (isMobile && _currentView is not MobileMainView)
        {
            _currentView = new MobileMainView();
            _currentView.DataContext = DataContext;
            Content = _currentView;
        }
        else if (!isMobile && _currentView is not DesktopMainView)
        {
            _currentView = new DesktopMainView();
            _currentView.DataContext = DataContext;
            Content = _currentView;
        }
    }
}
