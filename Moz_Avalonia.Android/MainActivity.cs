using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Net;
using Android.OS;
using Avalonia.Android;
using Moz_Avalonia.Services;

namespace Moz_Avalonia.Android;

[Activity(
    Label = "MozVPN",
    Theme = "@style/AvaloniaTheme",
    Icon = "@drawable/icon",
    MainLauncher = true,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode)]
public class MainActivity : AvaloniaMainActivity
{
    private int _pendingSocksPort = 64900;
    private string? _pendingProfileName;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        // Register the Android implementation of our VPN controller with this Activity context
        Moz_Avalonia.App.VpnManager = new AndroidVpnServiceManager(this);

        base.OnCreate(savedInstanceState);

        if (global::Android.OS.Build.VERSION.SdkInt >= global::Android.OS.BuildVersionCodes.Tiramisu)
        {
            if (CheckSelfPermission(global::Android.Manifest.Permission.PostNotifications) != global::Android.Content.PM.Permission.Granted)
            {
                RequestPermissions(new[] { global::Android.Manifest.Permission.PostNotifications }, 101);
            }
        }

        var intent = VpnService.Prepare(this);
        if (intent != null)
        {
            StartActivityForResult(intent, 99);
        }
    }

    public void StartVpnWithPermission(string profileName, int socksPort)
    {
        var intent = VpnService.Prepare(this);
        if (intent != null)
        {
            _pendingProfileName = profileName;
            _pendingSocksPort = socksPort;
            StartActivityForResult(intent, 0);
            return;
        }

        // Already has permission, start service directly
        var manager = Moz_Avalonia.App.VpnManager as AndroidVpnServiceManager;
        manager?.StartServiceDirectly(profileName, socksPort);
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode == 0 && resultCode == Result.Ok && _pendingProfileName != null)
        {
            var manager = Moz_Avalonia.App.VpnManager as AndroidVpnServiceManager;
            manager?.StartServiceDirectly(_pendingProfileName, _pendingSocksPort);
            _pendingProfileName = null;
        }
    }
}
