using Avalonia;
using System;
using HotAvalonia;

namespace Moz_Avalonia;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>();
#if !ANDROID
        builder = builder.UsePlatformDetect();
#endif
        return builder
#if DEBUG
            .WithDeveloperTools()
            .UseHotReload()
#endif
            .WithInterFont()
            .LogToTrace();
    }
}