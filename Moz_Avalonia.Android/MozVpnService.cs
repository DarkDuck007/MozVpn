using System;
using System.IO;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Net;
using Android.OS;
using Libcore;

namespace Moz_Avalonia.Android;

[Service(Name = "com.duckyvpn.mozvpn.MozVpnService", Permission = "android.permission.BIND_VPN_SERVICE", Exported = false, ForegroundServiceType = global::Android.Content.PM.ForegroundService.TypeSpecialUse)]
public class MozVpnService : VpnService, IBoxPlatformInterface, INB4AInterface
{
    private static MozVpnService? _instance;
    private const string ChannelId = "mozvpn_service_channel";
    private const int NotificationId = 1001;

    private ParcelFileDescriptor? _tunInterface;
    private BoxInstance? _singBoxInstance;

    public override void OnCreate()
    {
        base.OnCreate();
        _instance = this;
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        string? action = intent?.Action;
        if (action == "STOP_VPN")
        {
            StopVpn();
            return StartCommandResult.NotSticky;
        }

        int socksPort = intent?.GetIntExtra("SOCKS_PORT", 64900) ?? 64900;
        string? profileName = intent?.GetStringExtra("PROFILE_NAME");
        if (profileName != null)
        {
            var prefs = GetSharedPreferences("vpn_prefs", FileCreationMode.Private);
            prefs?.Edit().PutBoolean("is_running", true).PutString("active_profile", profileName).Commit();
        }

        // 1. Instantly start foreground status notification to satisfy OS startup check
        CreateNotificationChannel();
        var notification = BuildNotification();
        if (Build.VERSION.SdkInt >= (BuildVersionCodes)34)
        {
            StartForeground(NotificationId, notification, global::Android.Content.PM.ForegroundService.TypeSpecialUse);
        }
        else
        {
            StartForeground(NotificationId, notification);
        }

        // 2. Offload blocking Libcore/sing-box startup to background thread to prevent ANR ("not responding")
        Task.Run(() => StartVpn(socksPort));

        return StartCommandResult.Sticky;
    }

    private void CreateNotificationChannel()
    {
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
        {
            var channel = new NotificationChannel(
                ChannelId,
                "MozVPN Service",
                NotificationImportance.Low
            )
            {
                Description = "Ongoing VPN device tunnel status"
            };
            var manager = (NotificationManager)GetSystemService(NotificationService)!;
            manager.CreateNotificationChannel(channel);
        }
    }

    private Notification BuildNotification(string status = "Device traffic is tunneled through MozVPN.")
    {
        var builder = new Notification.Builder(this, ChannelId)
            .SetContentTitle("MozVPN Tunnel Active")
            .SetContentText(status)
            .SetSmallIcon(Resource.Drawable.icon)
            .SetOngoing(true)
            .SetCategory(Notification.CategoryService);

        var intent = new Intent(this, typeof(MainActivity));
        var pendingIntent = PendingIntent.GetActivity(this, 0, intent, PendingIntentFlags.Immutable);
        builder.SetContentIntent(pendingIntent);

        return builder.Build();
    }

    private void StartVpn(int socksPort)
    {
        try
        {
            var cachePath = CacheDir!.AbsolutePath + "/";
            var filesPath = FilesDir!.AbsolutePath + "/";
            
            // Initialize Libcore
            Libcore.Libcore.InitCore(
                "mozvpn",
                cachePath,
                filesPath,
                filesPath,
                100,
                true,
                this,
                this,
                new DummyLocalDNSTransport()
            );

            // sing-box config JSON
            string configJson = $$"""
            {
              "log": {
                "level": "info"
              },
              "inbounds": [
                {
                  "type": "tun",
                  "tag": "tun-in",
                  "stack": "gvisor",
                  "auto_route": true,
                  "strict_route": true,
                  "inet4_address": "172.19.0.1/30"
                }
              ],
              "outbounds": [
                {
                  "type": "socks",
                  "tag": "proxy",
                  "server": "127.0.0.1",
                  "server_port": {{socksPort}}
                }
              ],
              "route": {
                "rules": [
                  {
                    "outbound": "proxy"
                  }
                ]
              }
            }
            """;

            _singBoxInstance = Libcore.Libcore.NewSingBoxInstance(configJson, new DummyLocalDNSTransport());
            _singBoxInstance.Start();
        }
        catch (Exception)
        {
            StopSelf();
        }
    }

    private void StopVpn()
    {
        try
        {
            var prefs = GetSharedPreferences("vpn_prefs", FileCreationMode.Private);
            prefs?.Edit().PutBoolean("is_running", false).PutString("active_profile", null).Commit();
        }
        catch {}

        try
        {
            _singBoxInstance?.Close();
        }
        catch {}
        _singBoxInstance = null;
        
        try
        {
            _tunInterface?.Close();
        }
        catch {}
        _tunInterface = null;

        StopForeground(true);
        StopSelf();
    }

    public override void OnDestroy()
    {
        _instance = null;
        StopVpn();
        base.OnDestroy();
    }

    public static void UpdateNotificationStatus(string status)
    {
        _instance?.UpdateNotificationText(status);
    }

    private void UpdateNotificationText(string status)
    {
        var manager = (NotificationManager)GetSystemService(NotificationService)!;
        manager.Notify(NotificationId, BuildNotification(status));
    }

    // --- IBoxPlatformInterface implementation ---

    public void AutoDetectInterfaceControl(int fd)
    {
        Protect(fd);
    }

    public int FindConnectionOwner(int proto, string? srcIp, int srcPort, string? destIp, int destPort) => 0;

    public long OpenTun(string? singTunOptionsJson, string? tunPlatformOptionsJson)
    {
        var builder = new Builder(this);
        builder.SetSession("MozVPN");
        builder.AddAddress("172.19.0.1", 30);
        builder.AddRoute("0.0.0.0", 0);
        builder.AddDnsServer("8.8.8.8");

        // Prevent loopback routing of this app's own sockets
        builder.AddDisallowedApplication(PackageName);

        _tunInterface = builder.Establish();
        return _tunInterface != null ? _tunInterface.Fd : -1;
    }

    public string PackageNameByUid(int uid) => "android";
    public int UidByPackageName(string? pkg) => 0;
    public bool UseProcFS() => false;
    public string WifiState() => "";

    // --- INB4AInterface implementation ---

    public bool UseOfficialAssets() => true;
    public void Selector_OnProxySelected(string? selectorTag, string? tag) {}
}

public class DummyLocalDNSTransport : Java.Lang.Object, ILocalDNSTransport
{
    public void Exchange(ExchangeContext? ctx, byte[]? message)
    {
    }

    public void Lookup(ExchangeContext? ctx, string? network, string? domain)
    {
    }

    public long NetworkHandle() => 0;

    public bool Raw() => false;
}
