using Android;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Net;
using Android.OS;
using Android.Systems;
using AndroidX.Core.App;
using Java.Lang;
using JavaProcess = Java.Lang.Process;
using Microsoft.Maui.Storage;
using MozVpnMAUI;
using MozUtil;
using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MozVpnMAUI.Platforms.Android
{
   [Service(Exported = false,
      Permission = Manifest.Permission.BindVpnService,
      ForegroundServiceType = ForegroundService.TypeDataSync)]
   [IntentFilter(new[] { "android.net.VpnService" })]
   internal class XrayVpnService : VpnService
   {
      public const string ActionStart = "MozVpnMAUI.action.START_XRAY_VPN";
      public const string ActionStop = "MozVpnMAUI.action.STOP_XRAY_VPN";
      public const string ExtraVlessUri = "vless_uri";
      public const int VpnRequestCode = 4242;

      private const string NotificationChannelId = "xray_vpn";
      private const int NotificationId = 4242;
      private const string NotificationChannelName = "MozVPN VPN Mode";

      private ParcelFileDescriptor? tunInterface;
      private ParcelFileDescriptor? tunPfd;
      private int tunFd = -1;
      private JavaProcess? xrayProcess;
      private bool isRunning;

      public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
      {
         string? action = intent?.Action;
         if (string.Equals(action, ActionStop, StringComparison.Ordinal))
         {
            StopVpn();
            return StartCommandResult.NotSticky;
         }

         StartForegroundService();
         StaticInformation.StopServiceEvent += StaticInformation_StopServiceEvent;

         string? vlessUri = intent?.GetStringExtra(ExtraVlessUri);
         if (string.IsNullOrWhiteSpace(vlessUri))
         {
            Logger.Log("VPN mode requires a valid VLESS config.");
            StopSelf();
            return StartCommandResult.NotSticky;
         }

         if (!isRunning)
         {
            StartVpn(vlessUri);
         }

         return StartCommandResult.Sticky;
      }

      private void StartForegroundService()
      {
         var notificationManager = GetSystemService(Context.NotificationService) as NotificationManager;
         if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
         {
            var channel = new NotificationChannel(NotificationChannelId, NotificationChannelName, NotificationImportance.Low);
            notificationManager?.CreateNotificationChannel(channel);
         }

         var notification = new NotificationCompat.Builder(this, NotificationChannelId)
            .SetAutoCancel(false)
            .SetOngoing(true)
            .SetSmallIcon(Resource.Mipmap.appicon)
            .SetContentTitle("MozVPN")
            .SetContentText("VPN mode is running")
            .Build();

         if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
         {
            StartForeground(NotificationId, notification, ForegroundService.TypeDataSync);
         }
         else
         {
            StartForeground(NotificationId, notification);
         }
      }

      private void StartVpn(string vlessUri)
      {
         if (isRunning)
         {
            return;
         }

         Builder builder = new Builder(this);
         builder.SetSession("MozVPN");
         builder.AddAddress("10.0.0.2", 32);
         builder.AddRoute("0.0.0.0", 0);
         builder.AddDnsServer("1.1.1.1");
         builder.AddDnsServer("8.8.8.8");
         try
         {
            builder.AddAddress("fd00:1:fd00:1::1", 128);
            builder.AddRoute("::", 0);
         }
         catch (System.Exception ex)
         {
            Logger.Log(ex.Message + ex.StackTrace);
         }
         try
         {
            builder.SetMtu(1500);
         }
         catch (System.Exception ex)
         {
            Logger.Log(ex.Message + ex.StackTrace);
         }
         try
         {
            builder.AddDisallowedApplication(PackageName);
         }
         catch (global::Android.Content.PM.PackageManager.NameNotFoundException)
         {
         }

         tunInterface = builder.Establish();
         if (tunInterface == null)
         {
            Logger.Log("Failed to establish VPN interface.");
            StopSelf();
            return;
         }
         try
         {
            tunFd = tunInterface.DetachFd();
            tunPfd = global::Android.OS.ParcelFileDescriptor.AdoptFd(tunFd);
            const int F_GETFD = 1;
            const int F_SETFD = 2;
            int flags = Os.FcntlInt(tunPfd.FileDescriptor, F_GETFD, 0);
            const int FD_CLOEXEC = 1;
            Os.FcntlInt(tunPfd.FileDescriptor, F_SETFD, flags & ~FD_CLOEXEC);
            Logger.Log($"Tun fd: {tunFd}, flags: {flags}");
         }
         catch (System.Exception ex)
         {
            Logger.Log(ex.Message + ex.StackTrace);
            StopSelf();
            return;
         }

         string? xrayPath = EnsureXrayBinary();
         if (string.IsNullOrWhiteSpace(xrayPath))
         {
            Logger.Log("Xray binary not found in app package.");
            StopSelf();
            return;
         }

         string appData = FileSystem.AppDataDirectory;
         string configPath;
         try
         {
            configPath = BuildConfig(vlessUri, tunFd);
         }
         catch (System.Exception ex)
         {
            Logger.Log(ex.Message + ex.StackTrace);
            StopSelf();
            return;
         }
         if (!StartXrayProcess(xrayPath, configPath, appData))
         {
            Logger.Log("Failed to start Xray process.");
            StopSelf();
            return;
         }
         Logger.Log($"Xray config: {configPath}");
         Logger.Log($"Xray assets: {appData}");

         isRunning = true;
      }

      private void StopVpn()
      {
         isRunning = false;
         try
         {
            xrayProcess?.Destroy();
            xrayProcess = null;
         }
         catch (System.Exception ex)
         {
            Logger.Log(ex.Message + ex.StackTrace);
         }

         try
         {
            tunPfd?.Dispose();
            tunPfd = null;
            tunInterface = null;
         }
         catch (System.Exception ex)
         {
            Logger.Log(ex.Message + ex.StackTrace);
         }

         StopForeground(true);
         StopSelf();
      }

      private void StaticInformation_StopServiceEvent(object? sender, EventArgs e)
      {
         StopVpn();
      }

      public override void OnDestroy()
      {
         StaticInformation.StopServiceEvent -= StaticInformation_StopServiceEvent;
         StopVpn();
         base.OnDestroy();
      }

      private string? EnsureXrayBinary()
      {
         try
         {
            string? nativeDir = ApplicationInfo?.NativeLibraryDir;
            if (string.IsNullOrWhiteSpace(nativeDir))
            {
               return null;
            }
            string soPath = Path.Combine(nativeDir, "libxray.so");
            if (global::System.IO.File.Exists(soPath))
            {
               return soPath;
            }
            string exePath = Path.Combine(nativeDir, "xray");
            if (global::System.IO.File.Exists(exePath))
            {
               return exePath;
            }
         }
         catch (System.Exception ex)
         {
            Logger.Log(ex.Message + ex.StackTrace);
         }
         return null;
      }

      private string BuildConfig(string vlessUri, int tunFd)
      {
         string appData = FileSystem.AppDataDirectory;
         string configPath = Path.Combine(appData, "xray_tun.json");
         EnsureXrayAssets(appData);
         try
         {
            string json;
            using (Stream src = FileSystem.OpenAppPackageFileAsync("Config.json").GetAwaiter().GetResult())
            using (StreamReader reader = new StreamReader(src))
            {
               json = reader.ReadToEnd();
            }

            JsonNode? root = JsonNode.Parse(json);
            if (root is not JsonObject obj)
            {
               throw new InvalidDataException("Config.json root must be a JSON object.");
            }

            JsonArray inbounds;
            if (obj["inbounds"] is JsonArray arr)
            {
               inbounds = arr;
            }
            else
            {
               inbounds = new JsonArray();
               obj["inbounds"] = inbounds;
            }

            JsonObject tunInbound = new JsonObject
            {
               ["tag"] = "tun-in",
               ["protocol"] = "tun",
               ["settings"] = new JsonObject
               {
                  ["fd"] = tunFd,
                  ["mtu"] = 1500,
                  ["auto_route"] = false,
                  ["stack"] = "system"
               }
            };
            inbounds.Insert(0, tunInbound);

            string accessLog = Path.Combine(appData, "xray_access.log").Replace("\\", "/");
            string errorLog = Path.Combine(appData, "xray_error.log").Replace("\\", "/");
            if (obj["log"] is not JsonObject logObj)
            {
               logObj = new JsonObject();
               obj["log"] = logObj;
            }
            logObj["loglevel"] = "debug";
            logObj["access"] = accessLog;
            logObj["error"] = errorLog;

            string output = obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            global::System.IO.File.WriteAllText(configPath, output);
         }
         catch (System.Exception ex)
         {
            Logger.Log(ex.Message + ex.StackTrace);
            throw;
         }
         return configPath;
      }

      private string BuildConfigLegacy(string vlessUri, int tunFd)
      {
         string appData = FileSystem.AppDataDirectory;
         string configPath = Path.Combine(appData, "xray_tun.json");
         EnsureXrayAssets(appData);
         VlessOutboundConfig vless = ParseVlessUri(vlessUri);
      
         using var stream = FileSystem.OpenAppPackageFileAsync("Config.json").GetAwaiter().GetResult();
         using var reader = new StreamReader(stream);

         string config_vless = reader.ReadToEnd();
         File.WriteAllText(configPath, config_vless);
         return configPath;
      }

      private void EnsureXrayAssets(string appData)
      {
         CopyAssetIfMissing("geoip.dat", Path.Combine(appData, "geoip.dat"));
         CopyAssetIfMissing("geosite.dat", Path.Combine(appData, "geosite.dat"));
         CopyAssetIfMissing("Config.json", Path.Combine(appData, "Config.json"));
      }

      private void CopyAssetIfMissing(string assetName, string destPath)
      {
         try
         {
            if (global::System.IO.File.Exists(destPath))
            {
               return;
            }
            using Stream src = FileSystem.OpenAppPackageFileAsync(assetName).GetAwaiter().GetResult();
            using FileStream dst = global::System.IO.File.Create(destPath);
            src.CopyTo(dst);
         }
         catch (System.Exception ex)
         {
            Logger.Log(ex.Message + ex.StackTrace);
         }
      }

      private bool StartXrayProcess(string xrayPath, string configPath, string assetDir)
      {
         try
         {
            string[] envp = new string[] { "XRAY_LOCATION_ASSET=" + assetDir };
            xrayProcess = Runtime.GetRuntime().Exec(new string[] { xrayPath, "run", "-config", configPath }, envp);
            StartLogPipes();
            return true;
         }
         catch (System.Exception ex)
         {
            Logger.Log(ex.Message + ex.StackTrace);
            return false;
         }
      }

      private sealed class VlessOutboundConfig
      {
         public string UserId { get; set; } = string.Empty;
         public string Address { get; set; } = string.Empty;
         public int Port { get; set; }
         public string Encryption { get; set; } = "none";
         public string Network { get; set; } = "tcp";
         public string Security { get; set; } = "none";
         public string Path { get; set; } = "/";
         public string HostHeader { get; set; } = string.Empty;
         public string? ServerName { get; set; }
         public string TlsSettingsJson { get; set; } = string.Empty;
      }

      private VlessOutboundConfig ParseVlessUri(string vlessUri)
      {
         if (!global::System.Uri.TryCreate(vlessUri, global::System.UriKind.Absolute, out global::System.Uri? uri))
         {
            throw new ArgumentException("Invalid VLESS URI.");
         }
         if (!string.Equals(uri.Scheme, "vless", StringComparison.OrdinalIgnoreCase))
         {
            throw new ArgumentException("VLESS URI must start with vless://");
         }

         string userId = uri.UserInfo;
         if (string.IsNullOrWhiteSpace(userId))
         {
            throw new ArgumentException("VLESS user ID is missing.");
         }

         string path = "/";
         string hostHeader = uri.Host;
         string security = "none";
         string encryption = "none";
         string network = "tcp";
         string? sni = null;

         string query = uri.Query;
         if (!string.IsNullOrWhiteSpace(query))
         {
            string[] pairs = query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
            foreach (string pair in pairs)
            {
               string[] kv = pair.Split('=', 2);
               string key = global::System.Uri.UnescapeDataString(kv[0]);
               string value = kv.Length > 1 ? global::System.Uri.UnescapeDataString(kv[1]) : string.Empty;
               switch (key)
               {
                  case "path":
                     path = value;
                     break;
                  case "security":
                     security = value;
                     break;
                  case "encryption":
                     encryption = value;
                     break;
                  case "host":
                     hostHeader = value;
                     break;
                  case "type":
                     network = value;
                     break;
                  case "sni":
                     sni = value;
                     break;
               }
            }
         }

         VlessOutboundConfig config = new VlessOutboundConfig
         {
            UserId = userId,
            Address = uri.Host,
            Port = uri.Port > 0 ? uri.Port : 443,
            Encryption = string.IsNullOrWhiteSpace(encryption) ? "none" : encryption,
            Network = string.IsNullOrWhiteSpace(network) ? "tcp" : network,
            Security = string.IsNullOrWhiteSpace(security) ? "none" : security,
            Path = string.IsNullOrWhiteSpace(path) ? "/" : path,
            HostHeader = string.IsNullOrWhiteSpace(hostHeader) ? uri.Host : hostHeader,
            ServerName = sni
         };

         if (!string.Equals(config.Security, "none", StringComparison.OrdinalIgnoreCase))
         {
            string serverName = string.IsNullOrWhiteSpace(config.ServerName) ? config.HostHeader : config.ServerName;
            config.TlsSettingsJson = "        \"tlsSettings\": { \"serverName\": \"" + serverName + "\" },\n";
         }
         else
         {
            config.TlsSettingsJson = string.Empty;
         }

         if (config.Network != "ws")
         {
            config.Network = "ws";
         }

         return config;
      }

      private void StartLogPipes()
      {
         if (xrayProcess == null)
         {
            return;
         }

         string appData = FileSystem.AppDataDirectory;
         string stdoutPath = Path.Combine(appData, "xray_stdout.log");
         string stderrPath = Path.Combine(appData, "xray_stderr.log");

         Task.Run(() => CopyStreamToFile(xrayProcess.InputStream, stdoutPath));
         Task.Run(() => CopyStreamToFile(xrayProcess.ErrorStream, stderrPath));
      }

      private void CopyStreamToFile(Stream input, string path)
      {
         try
         {
            using FileStream fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            byte[] buffer = new byte[4096];
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
               fs.Write(buffer, 0, read);
               fs.Flush();
            }
         }
         catch (System.Exception ex)
         {
            Logger.Log(ex.Message + ex.StackTrace);
         }
      }
   }
}
