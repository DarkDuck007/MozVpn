using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.Extensions.FileSystemGlobbing.Internal.PatternContexts;
using MozUtil;
using MozUtil.Types;
using STUN;
using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection.Metadata.Ecma335;
using System.Security.Principal;
using System.Text;

namespace DirtySocksASP
{
   public class Program
   {
      public static void Main(string[] args)
      {
         var builder = WebApplication.CreateBuilder(args);
         builder.Services.AddRazorPages();
         builder.Services.AddSignalR().AddJsonProtocol();
         //builder.Services.AddDirectoryBrowser();
         var app = builder.Build();
         StaticData.ServerStartDateTime = DateTime.UtcNow;
         //app.Urls.Add("http://0.0.0.0:5000");
         //app.UseStaticFiles();
         //app.UseDirectoryBrowser();

         //app.MapPost("/VerificationCode", async Context =>
         //{
         //   if (Context.Connection.RemoteIpAddress?.ToString() == "127.0.0.1"/*server IP address*/)
         //   {
         //      //do stuff
         //      string? OTP = Context.Request.Headers["otp"];
         //      string? User = Context.Request.Headers["user"];
         //      Console.WriteLine(OTP);
         //      var s = Context.Response.WriteAsync($"{OTP} was verified by {User}");
         //   }
         //   else
         //   {
         //      Context.Response.StatusCode = ((int)HttpStatusCode.Forbidden);
         //   }
         //});
         app.MapGet("/neverssl", async context =>
         {
            HttpWebRequest Req = WebRequest.CreateHttp("http://neverssl.com");
            Req.KeepAlive = false;
            HttpWebResponse resp = (HttpWebResponse)await Req.GetResponseAsync();
            await resp.GetResponseStream().CopyToAsync(context.Response.Body);

         });
         app.MapGet("/ToggleLocal", async context =>
         {
            StaticServers.LocalServerMode = !StaticServers.LocalServerMode;
            await context.Response.WriteAsync($"Toggled. New localservermode value: {StaticServers.LocalServerMode}");
         });
         app.MapGet("/GetEP", async context =>
         {
            await context.Response.WriteAsync(context.Connection.RemoteIpAddress.ToString() + ":" + context.Connection.RemotePort.ToString());
         });

         app.MapGet("/GetLog", () => { return Logger.GetLog(); });
         app.MapGet("/PollLog", async context =>
         {
            Logger.RegisterLogStream(context.Response.Body);
            await MozStatic.KeepStreamAliveAsync(context.Response.Body, 30000, context.RequestAborted);
            Logger.UnregisterLogStream(context.Response.Body);
         });
         app.MapGet("/StartSocks", () => { return StaticServers.StartSocksServer().ToString(); });
         app.MapGet("/Status", () =>
         {
            GCMemoryInfo inf = GC.GetGCMemoryInfo();

            string Returnstring = $"Last: {StaticData.LastTcpClientMakeDelayTicks} ticks{Environment.NewLine}" +
            $"avg {StaticData.GetAvgTcpClientCreationDelayTicks()} ticks.{Environment.NewLine}" +
            $"Total: {StaticData.TotalLocalTcpClientsMade} {Environment.NewLine}" +
            $"Active: {StaticData.TotalActiveConnections}{Environment.NewLine}" +
            $"Threads: {Process.GetCurrentProcess().Threads.Count.ToString()}{Environment.NewLine}" +
            $"LNServers: {ClientManager.ReliableClients.Count().ToString()}{Environment.NewLine}" +
            $"Mem: {GC.GetTotalMemory(false) / 1024} KB MemLoad: {inf.MemoryLoadBytes / 1024} KB HeapSizeBytes: {inf.HeapSizeBytes / 1024} KB TotalCommittedBytes: {inf.TotalCommittedBytes / 1024} KB";
            return Returnstring;
         });
         app.MapGet("/CollectGC", () => { GC.Collect(); return (GC.GetTotalMemory(true).ToString() + " Bytes"); });
         app.MapGet("/TestCon", async context =>
         {
            await context.Response.WriteAsync($"Moz server connection test {DateTime.UtcNow:yyyy} UTC");
         });
         //app.MapPost("/InitTC", async context =>
         //{
         //   try
         //   {
         //      //LNClient LClient = new LNClient(context);
         //      TCClient TClient = new TCClient(context);


         //      StaticServers.StartSocksServer();
         //      //await LClient.StartAsync();
         //      //Client client = new Client(ref context);
         //      //byte[] Buffer = new byte[1024];
         //      //int Read = await context.Request.Body.ReadAsync(Buffer);
         //      //HolePunchPeerInfo PeerInfo = MozStatic.DeserializePunchInfo(Buffer[0..Read], 0);
         //      //await client.InitUserConnection(PeerInfo);
         //      Logger.Log("Exited user TCP connection loop, NOT disposing client.");
         //      //client.Dispose();
         //      //client.CloseAllConnections();
         //      //Console.WriteLine("Client disposed.");
         //      //ClientManager.AddClient(client);
         //   }
         //   catch (Exception ex)
         //   {
         //      Logger.Log(ex.Message + Environment.NewLine + ex.StackTrace);
         //   }
         //});
         app.MapPost("/ReconLN", async context =>
         {
            //ClientManager.ReliableClients

         });
         app.MapPost("/InitLN", async context =>
         {
            try
            {
               Interlocked.Increment(ref StaticData.HttpKeepAliveConnectionsCount);
               Logger.Log("LN Req received...");
               LNClient LClient = new LNClient(context);
               StaticServers.StartSocksServer();
               await LClient.StartAsync();
               //Client client = new Client(ref context);
               //byte[] Buffer = new byte[1024];
               //int Read = await context.Request.Body.ReadAsync(Buffer);
               //HolePunchPeerInfo PeerInfo = MozStatic.DeserializePunchInfo(Buffer[0..Read], 0);
               //await client.InitUserConnection(PeerInfo);
               Logger.Log("Exited user TCP connection loop, NOT disposing client.");
               //client.Dispose();
               //client.CloseAllConnections();
               //Console.WriteLine("Client disposed.");
               //ClientManager.AddClient(client);
            }
            catch (Exception ex)
            {
               Logger.Log(ex.Message + Environment.NewLine + ex.StackTrace);
            }
            Interlocked.Decrement(ref StaticData.HttpKeepAliveConnectionsCount);
         });
         app.MapPost("/LiteNetSpeedTest", async context =>
         {
            Interlocked.Increment(ref StaticData.HttpKeepAliveConnectionsCount);

            LiteNetBench LNB = new LiteNetBench(context);
            await LNB.BeginBenchAsync();
            Interlocked.Decrement(ref StaticData.HttpKeepAliveConnectionsCount);

         });
         app.MapGet("/nat", async () =>
         {
            try
            {
               using (var udp = new UdpClient())
               {
                  string? ErrorMessage = "Unknown Error";
                  STUNQueryResult? res = null;
                  await Task.Run(() =>
                  {
                     try
                     {
                        res = StunHelpers.GetStunResult(udp.Client);
                     }
                     catch (Exception ex)
                     {
                        ErrorMessage = ex.StackTrace;
                     }
                  });
                  string output = (res == null) ? ErrorMessage : $"{(int)res.NATType} {res.NATType.ToString()}";
                  return output;
               }
            }
            catch (Exception ex)
            {
               return ex.StackTrace;
            }
         });
         app.MapGet("/isElevated", async () =>
         {
            try
            {
               await Task.Delay(0);
               bool isElevated = false;
#if WINDOWS
               using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
               {
                  WindowsPrincipal principal = new WindowsPrincipal(identity);
                  isElevated = principal.IsInRole(WindowsBuiltInRole.Administrator);
               }
#endif
               string output = isElevated.ToString();
               return output;
            }
            catch (Exception ex)
            {
               return ex.StackTrace;
            }
         });
         app.MapPost("/UDPRL", async context =>
         {
            UdpRelayClient RelayClient = new UdpRelayClient(context);
            if (context.Request.Headers.Connection.ToString().ToLower() == "keep-alive")
            {
               await RelayClient.StartRelay();
            }
            else
            {
               _ = RelayClient.StartRelay();
            }
         });
         app.MapGet("/WGT", async context =>
         {
            TcpToUDPPipe TTUP = new TcpToUDPPipe(await TcpToUDPPipe.PunchSockAsync(context));
            if (context.Request.Headers.Connection.ToString().ToLower().Equals("keep-alive"))
            {
               await TTUP.RunTunnel();
            }
            else
            {
               _ = TTUP.RunTunnel();
            }
         });
         app.MapGet("/TCPPunch", async context =>
         {
            string IPString = context.Connection.RemoteIpAddress.ToString();
            string PortString = context.Request.Headers["port"];
            TcpClient OrigCli = new TcpClient();
            //Socket Sock = new Socket(SocketType.Stream, ProtocolType.Tcp);
            var Sock = OrigCli.Client;
            Sock.Bind(new IPEndPoint(IPAddress.Any, 0));
            await context.Response.WriteAsync((Sock.LocalEndPoint as IPEndPoint).Port.ToString() + "\n");
            await context.Response.Body.FlushAsync();
            byte[] ServerPunch = Encoding.ASCII.GetBytes("Server Punchy");
            byte[] ServerGotPunch = Encoding.ASCII.GetBytes("Server Received Client Punchy");
            Sock.SendTimeout = 7000;
            TcpClient NCli = new TcpClient();

            Task T = Task.Run(async () =>
            {
               bool Punched = false;
               for (int i = 0; i < 2; i++)
               {
                  try
                  {
                     await Sock.ConnectAsync(IPString, int.Parse(PortString));
                  }
                  catch (Exception ex)
                  {
                     Logger.Log(ex.Message + Environment.NewLine + ex.StackTrace);
                  }
               }
               try
               {
                  Sock.Listen();
                  var ClientSocket = await Sock.AcceptAsync();
                  NCli.Client = ClientSocket;
                  Logger.Log("tcp sock accepted.");
                  ClientSocket.SendAsync(ServerPunch, SocketFlags.None);
               }
               catch (Exception ex)
               {
                  Logger.Log(ex.Message + Environment.NewLine + ex.StackTrace);
               }
               for (int i = 0; i < 10; i++)
               {
                  try
                  {
                     Sock.Send(ServerPunch);
                  }
                  catch (Exception ex)
                  {
                     Logger.Log(ex.Message + Environment.NewLine + ex.StackTrace);
                  }
               }
               _ = Task.Run(async () =>
                 {
                    using (StreamWriter Sw = new StreamWriter(OrigCli.GetStream()))
                    {
                       for (int i = 0; i < 10; i++)
                       {
                          await Sw.WriteLineAsync("WL FROM Original client in stream.");
                          await Sw.FlushAsync();
                       }
                    }
                 });
               _ = Task.Run(async () =>
               {
                  using (StreamWriter Sw = new StreamWriter(NCli.GetStream()))
                  {
                     for (int i = 0; i < 10; i++)
                     {
                        await Sw.WriteLineAsync("WL FROM New client in stream.");
                        await Sw.FlushAsync();
                     }
                  }
               });
               byte[] Buffer = ArrayPool<byte>.Shared.Rent(4096 * 2);
               _ = Task.Run(async () =>
               {
                  while (true)
                  {
                     int read = await Sock.ReceiveAsync(Buffer);
                     if (read == 0)
                     {
                        break;
                     }
                     string readS = Encoding.ASCII.GetString(Buffer, 0, read);
                     Logger.Log(readS);
                  }
               });
               //while (true)
               //{
               //   try
               //   {
               //      //int rec = Sock.Receive(Buffer);
               //      //if (Encoding.ASCII.GetString(Buffer, 0, rec) == "Client Punchy")
               //      //{
               //      Sock.Send(ServerGotPunch);
               //      //}
               //      //Logger.Log(Encoding.ASCII.GetString(Buffer, 0, rec));
               //   }
               //   catch (Exception ex)
               //   {
               //      Logger.Log(ex.Message + Environment.NewLine + ex.StackTrace);
               //   }
               //}

            });
            string? s = null;
            _ = Task.Run(async () =>
              {
                 using (StreamReader SR = new StreamReader(OrigCli.GetStream()))
                 {
                    while ((s = await SR.ReadLineAsync()) != null)
                    {
                       Logger.Log("orig sock " + s);
                    }
                 }
              });
            _ = Task.Run(async () =>
             {
                using (StreamReader SR = new StreamReader(NCli.GetStream()))
                {
                   while ((s = await SR.ReadLineAsync()) != null)
                   {
                      Logger.Log("new sock " + s);
                   }
                }
             });
            if (context.Request.Headers.KeepAlive == "Keep-Alive")
            {
               await T;
            }
         });

         app.MapRazorPages();
         //app.MapHub<>
         app.Run();
      }
   }
}
