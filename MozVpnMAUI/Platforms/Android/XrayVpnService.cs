using Android;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Net;
using Android.OS;
using Android.Systems;
using AndroidX.Core.App;
using Java.Lang;
using Microsoft.Maui.Storage;
using MozUtil;
using MozVpnMAUI;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using JavaProcess = Java.Lang.Process;

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

      private const int SocksPort = 6075;
      private ParcelFileDescriptor? tunInterface;
      private ParcelFileDescriptor? tunPfd;
      private int tunFd = -1;
      private int tunFdCopy = -1;
      private JavaProcess? tun2SocksProcess;
      private bool isRunning;

      //[DllImport("tun2socks", EntryPoint = "StartTun2Socks")]
      //public static extern void StartTun2Socks(int fd, string proxyAddr, string dnsServer);

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

      private async Task StartVpn(string vlessUri)
      {
         if (isRunning)
         {
            return;
         }

         Builder builder = new Builder(this);
         builder.SetSession("MozVPN");
         builder.AddAddress("10.0.0.1", 24);
         builder.AddRoute("0.0.0.0", 0);
         //builder.AddDnsServer("1.1.1.1");
         //builder.AddDnsServer("8.8.8.8");
         //try
         //{
         //   builder.AddAddress("fd00:1:fd00:1::1", 128);
         //   builder.AddRoute("::", 0);
         //}
         //catch (System.Exception ex)
         //{
         //   Logger.Log(ex.Message + ex.StackTrace);
         //}
         //try
         //{
         //   builder.SetMtu(1500);
         //}
         //catch (System.Exception ex)
         //{
         //   Logger.Log(ex.Message + ex.StackTrace);
         //}
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
         //try
         //{
         //   tunFd = tunInterface.Fd;
         //   const int F_GETFD = 1;
         //   const int F_SETFD = 2;
         //   int flags = Os.FcntlInt(tunInterface.FileDescriptor, F_GETFD, 0);
         //   const int FD_CLOEXEC = 1;
         //   Os.FcntlInt(tunInterface.FileDescriptor, F_SETFD, flags & ~FD_CLOEXEC);

         //   tunFdCopy = Os.Dup(tunFd); // Duplicate the file descriptor
         //   Logger.Log($"Tun fd: {tunFd}, Tun fd Copy: {tunFdCopy}");
         //}
         //catch (System.Exception ex)
         //{
         //   Logger.Log(ex.Message + ex.StackTrace);
         //   StopSelf();
         //   return;
         //}

         //string? tun2SocksPath = EnsureTun2SocksBinary();
         //if (string.IsNullOrWhiteSpace(tun2SocksPath))
         //{
         //   Logger.Log("tun2socks binary not found in app package.");
         //   StopSelf();
         //   return;
         //}

         //if (!StartTun2Socks(tun2SocksPath, tunFdCopy))
         //{
         //   Logger.Log("Failed to start tun2socks process.");
         //   StopSelf();
         //   return;
         //}
         //Logger.Log($"tun2socks started on fd {tunFdCopy} -> 127.0.0.1:{SocksPort}");


         int fd = tunInterface.Fd;
         try
         {
            await StartSingBox(fd);

         }
         catch (System.Exception ex)
         {
            System.Diagnostics.Debug.WriteLine(ex);
         }
         // 1. Get paths
         //int fd = tunInterface.Fd;
         //Task.Run(() =>
         //{
         //   try
         //   {
         //      StartTun2Socks(fd, "127.0.0.1:6075", "8.8.8.8");

         //   }
         //   catch (System.Exception ex)
         //   {
         //      Logger.Log(ex.Message + ex.StackTrace);
         //   }
         //});
         isRunning = true;
      }
      public async Task StartSingBox(int tunFd)
      {
         try
         {
            string workingConfigPath = Path.Combine(FileSystem.Current.AppDataDirectory, "active_config.json");

            // 1. Read the template safely
            string json;
            using (Stream stream = await FileSystem.Current.OpenAppPackageFileAsync("Config.json"))
            using (StreamReader reader = new StreamReader(stream))
            {
               json = await reader.ReadToEndAsync();
            }

            if (string.IsNullOrWhiteSpace(json))
               throw new System.Exception("Asset 'Config.json' was found but is empty or could not be read.");


            // 3. Write and Force Flush
            await File.WriteAllTextAsync(workingConfigPath, json);

            // 4. Verification
            var info = new FileInfo(workingConfigPath);
            System.Diagnostics.Debug.WriteLine($"Config written to: {workingConfigPath} ({info.Length} bytes)");

            if (info.Length == 0)
               throw new System.Exception("Write failed: working config is 0 bytes.");

            // 5. Launch Sing-Box
            var binPath = GetNativeBinaryPath("sing-box-bin");
            var process = new System.Diagnostics.Process();
            process.StartInfo.FileName = binPath;
            process.StartInfo.Arguments = $"run -c \"{workingConfigPath}\""; // Quotes handle spaces in paths
            process.StartInfo.EnvironmentVariables["ENABLE_DEPRECATED_TUN_ADDRESS_X"] = "true";
            process.StartInfo.EnvironmentVariables["SKIP_PACKAGE_CHECK"] = "1";
            process.StartInfo.EnvironmentVariables["TUN_FD"] = tunFd.ToString();
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.CreateNoWindow = true;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;

            process.OutputDataReceived += (s, e) => {
               if (!string.IsNullOrEmpty(e.Data))
                  System.Diagnostics.Debug.WriteLine($"[Sing-Box] {e.Data}");
            };
            process.ErrorDataReceived += (s, e) => {
               if (!string.IsNullOrEmpty(e.Data))
                  System.Diagnostics.Debug.WriteLine($"[Sing-Box ERROR] {e.Data}");
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
         }
         catch (System.Exception ex)
         {
            System.Diagnostics.Debug.WriteLine($"FATAL: {ex.Message}");
            throw;
         }
      }
      private void StopVpn()
      {
         isRunning = false;
         try
         {
            tun2SocksProcess?.Destroy();
            tun2SocksProcess = null;
         }
         catch (System.Exception ex)
         {
            Logger.Log(ex.Message + ex.StackTrace);
         }

         try
         {
            tunInterface?.Close();
            tunInterface = null;
         }
         catch (System.Exception ex)
         {
            Logger.Log(ex.Message + ex.StackTrace);
         }
         tunFd = -1;
         if (tunFdCopy != -1)
         {
            try
            {
               using (var pfdToClose = global::Android.OS.ParcelFileDescriptor.AdoptFd(tunFdCopy))
               {
                  pfdToClose.Close();
               }
            }
            catch (System.Exception ex)
            {
               Logger.Log(ex.Message + ex.StackTrace);
            }
            tunFdCopy = -1;
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

      private string? EnsureTun2SocksBinary()
      {
         try
         {
            string? nativeDir = ApplicationInfo?.NativeLibraryDir;
            if (string.IsNullOrWhiteSpace(nativeDir))
            {
               return null;
            }
            string soPath = Path.Combine(nativeDir, "libtun2socks.so");
            if (global::System.IO.File.Exists(soPath))
            {
               return soPath;
            }
         }
         catch (System.Exception ex)
         {
            Logger.Log(ex.Message + ex.StackTrace);
         }
         return null;
      }
      public string GetNativeBinaryPath(string binaryName)
      {
         // Android extracts files in the 'lib' folder to the ApplicationInfo.NativeLibraryDir
         // If your file is 'sing-box-bin', Android usually renames it to 'libsing-box-bin.so' 
         // or keeps it if it doesn't have an extension.
         var context = global::Android.App.Application.Context;
         string libDir = context.ApplicationInfo.NativeLibraryDir;

         // Check for common naming patterns
         string[] possibleNames = { binaryName, $"lib{binaryName}.so", binaryName + ".so" };

         foreach (var name in possibleNames)
         {
            string fullPath = Path.Combine(libDir, name);
            if (File.Exists(fullPath)) return fullPath;
         }

         throw new FileNotFoundException($"Could not find native binary {binaryName} in {libDir}");
      }
      public async Task<string> CopyAssetToInternalStorage(string assetName)
      {
         // Path: /data/user/0/com.company.app/files/Config.json
         string targetPath = Path.Combine(FileSystem.Current.AppDataDirectory, assetName);

         if (!File.Exists(targetPath))
         {
            using Stream inputStream = await FileSystem.Current.OpenAppPackageFileAsync(assetName);
            using FileStream outputStream = File.Create(targetPath);
            await inputStream.CopyToAsync(outputStream);
         }
         return targetPath;
      }
      private string BuildConfig(string vlessUri, int tunFd)
      {
         string appData = FileSystem.AppDataDirectory;
         string configPath = Path.Combine(appData, "xray_tun.json");
         EnsureXrayAssets(appData);
         try
         {
            VlessOutboundConfig vless = ParseVlessUri(vlessUri);

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

            JsonArray? outbounds;
            if (obj["outbounds"] is JsonArray arrOut)
            {
               outbounds = arrOut;
            }
            else
            {
               outbounds = new JsonArray();
               obj["outbounds"] = outbounds;
            }

            JsonObject vlessOutbound = new JsonObject
            {
               ["tag"] = "proxy",
               ["protocol"] = "vless",
               ["settings"] = new JsonObject
               {
                  ["vnext"] = new JsonArray
                  {
                     new JsonObject
                     {
                        ["address"] = vless.Address,
                        ["port"] = vless.Port,
                        ["users"] = new JsonArray
                        {
                           new JsonObject
                           {
                              ["id"] = vless.UserId,
                              ["encryption"] = vless.Encryption
                           }
                        }
                     }
                  }
               },
               ["streamSettings"] = new JsonObject
               {
                  ["network"] = vless.Network,
                  ["security"] = vless.Security,
                  ["wsSettings"] = new JsonObject
                  {
                     ["path"] = vless.Path,
                     ["headers"] = new JsonObject
                     {
                        ["Host"] = vless.HostHeader
                     }
                  }
               }
            };

            if (vless.Security != "none")
            {
               (vlessOutbound["streamSettings"] as JsonObject)["tlsSettings"] = new JsonObject { ["serverName"] = vless.ServerName };
            }

            outbounds.Insert(0, vlessOutbound);

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
                  ["auto_route"] = true,
                  ["stack"] = "gvisor"
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

      private bool StartTun2Socks(string tun2SocksPath, int fd)
      {
         try
         {
            Java.Lang.Runtime.GetRuntime().Exec($"chmod 755 {tun2SocksPath}");
            var process = new System.Diagnostics.Process();
            process.StartInfo.FileName = tun2SocksPath;
            // Use the 'fd://' syntax to pass your TUN interface
            process.StartInfo.Arguments = $"-device fd://{tunFd} -proxy socks5://127.0.0.1:6075";
            process.Start();
            //string[] args = new[]

            //{
            //   tun2SocksPath,
            //   "--device=fd://" + fd,
            //   "--proxy=socks5://127.0.0.1:" + SocksPort,
            //   "--loglevel=info"
            //};
            //tun2SocksProcess = Runtime.GetRuntime().Exec(args);
            //StartLogPipes(tun2SocksProcess);
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

      private void StartLogPipes(JavaProcess process)
      {
         string appData = FileSystem.AppDataDirectory;
         string stdoutPath = Path.Combine(appData, "tun2socks_stdout.log");
         string stderrPath = Path.Combine(appData, "tun2socks_stderr.log");

         Task.Run(() => CopyStreamToFile(process.InputStream, stdoutPath));
         Task.Run(() => CopyStreamToFile(process.ErrorStream, stderrPath));
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
