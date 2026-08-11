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
        var prefs = _activity.GetSharedPreferences("vpn_prefs", FileCreationMode.Private);
        if (prefs != null)
        {
            IsVpnRunning = prefs.GetBoolean("is_running", false);
            ActiveProfileName = prefs.GetString("active_profile", null);
        }
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
        serviceIntent.PutExtra("PROFILE_NAME", profileName);
        
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

        var prefs = _activity.GetSharedPreferences("vpn_prefs", FileCreationMode.Private);
        prefs?.Edit().PutBoolean("is_running", true).PutString("active_profile", profileName).Commit();
    }

    public void StopVpn()
    {
        var serviceIntent = new Intent(_activity, typeof(MozVpnService));
        serviceIntent.SetAction("STOP_VPN");
        _activity.StartService(serviceIntent);

        IsVpnRunning = false;
        ActiveProfileName = null;

        var prefs = _activity.GetSharedPreferences("vpn_prefs", FileCreationMode.Private);
        prefs?.Edit().PutBoolean("is_running", false).PutString("active_profile", null).Commit();
    }
}
