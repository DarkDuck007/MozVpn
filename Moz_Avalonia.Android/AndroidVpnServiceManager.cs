using Android.App;
using Android.Content;
using Android.Net;
using Moz_Avalonia.Services;

namespace Moz_Avalonia.Android;

public class AndroidVpnServiceManager : IVpnServiceManager
{
    private readonly Activity _activity;
    
    public bool IsVpnRunning { get; private set; }
    public string? ActiveProfileName { get; private set; }

    public AndroidVpnServiceManager(Activity activity)
    {
        _activity = activity;
    }

    public void StartVpn(string profileName, int socksPort)
    {
        if (_activity is MainActivity mainActivity)
        {
            mainActivity.StartVpnWithPermission(profileName, socksPort);
        }
    }

    public void StartServiceDirectly(string profileName, int socksPort)
    {
        var serviceIntent = new Intent(_activity, typeof(MozVpnService));
        serviceIntent.PutExtra("SOCKS_PORT", socksPort);
        
        if (global::Android.OS.Build.VERSION.SdkInt >= global::Android.OS.BuildVersionCodes.O)
        {
            _activity.StartForegroundService(serviceIntent);
        }
        else
        {
            _activity.StartService(serviceIntent);
        }

        IsVpnRunning = true;
        ActiveProfileName = profileName;
    }

    public void StopVpn()
    {
        var serviceIntent = new Intent(_activity, typeof(MozVpnService));
        serviceIntent.SetAction("STOP_VPN");
        _activity.StartService(serviceIntent);

        IsVpnRunning = false;
        ActiveProfileName = null;
    }
}
