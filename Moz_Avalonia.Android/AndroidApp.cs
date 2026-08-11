using System;
using Android.App;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;

namespace Moz_Avalonia.Android;

[Application]
public class AndroidApp : AvaloniaAndroidApplication<Moz_Avalonia.App>
{
    public AndroidApp(IntPtr javaReference, JniHandleOwnership transfer) 
        : base(javaReference, transfer) 
    {
    }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        return base.CustomizeAppBuilder(builder)
            .WithInterFont();
    }
}
