using System.Net;
using MTProtoProxy;
using MozUtil;

namespace DirtySocksASP;

public static class StaticServers
{
   public static bool LocalServerMode { get; set; } =
      Convert.ToBoolean(Environment.GetEnvironmentVariable("IsLocal") ?? "false");

   public static string DestServerIP = Environment.GetEnvironmentVariable("DestIP") ?? "127.0.0.1";
   private static int _socks5ServerPort = int.Parse(Environment.GetEnvironmentVariable("Socks5Port") ?? "36567");
   private static int _mtProtoServerPort = 6577;
   public static bool UseInternalSocks5Proxy =
      Convert.ToBoolean(Environment.GetEnvironmentVariable("UseInternalSocks5Proxy") ?? "true");

   public static int Socks5ServerPort => _socks5ServerPort;
   public static int DestServerPort => Socks5ServerPort;
   public static int MTServerPort => _mtProtoServerPort;

   private static readonly object Sync = new();
   private static SimpleSocks5Server _socks5Server = CreateSocksServer();
   private static bool _serverStarted;

   public static bool StartSocksServer()
   {
      if (!UseInternalSocks5Proxy || Socks5ServerPort == 2080)
         return false;

      lock (Sync)
      {
         if (_serverStarted)
         {
            Logger.Log("Socks server is already running.");
            return false;
         }

         _serverStarted = true;
         _ = Task.Run(RunSocksServerAsync);
         return true;
      }
   }

   private static async Task RunSocksServerAsync()
   {
      Logger.Log($"SOCKS5 server starting on 127.0.0.1:{Socks5ServerPort}.");
      try
      {
         await _socks5Server.StartAsync();
      }
      catch (Exception ex)
      {
         Logger.LogException(ex);
      }
      finally
      {
         lock (Sync)
         {
            _serverStarted = false;
         }
         Logger.Log("SOCKS5 server stopped.");
      }
   }

   private static SimpleSocks5Server CreateSocksServer() =>
      new(new IPEndPoint(IPAddress.Loopback, Socks5ServerPort));

   public static MTProtoProxyServer StartMtProtoServer()
   {
      int nextPort = Interlocked.Increment(ref _mtProtoServerPort);
      if (nextPort > ushort.MaxValue)
         throw new InvalidOperationException("No MTProto listener ports remain.");
      ushort port = (ushort)nextPort;
      MTProtoProxyServer server = new("437574654C6F63616C50726F78792121", port);
      server.Start();
      return server;
   }
}
