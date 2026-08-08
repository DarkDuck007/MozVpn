using MozUtil;
using System.Buffers;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;
namespace DirtySocksASP
{
   public class TcpToUDPPipe
   {
      public static async Task<Socket> PunchSockAsync(HttpContext context)
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
            Logger.Log("tcp sock accepted.");
            //ClientSocket.SendAsync(ServerPunch, SocketFlags.None);
         }
         catch (Exception ex)
         {
            Logger.Log(ex.Message + Environment.NewLine + ex.StackTrace);
         }
         //for (int i = 0; i < 10; i++)
         //{
         //   try
         //   {
         //      Sock.Send(ServerPunch);
         //   }
         //   catch (Exception ex)
         //   {
         //      Logger.Log(ex.Message + Environment.NewLine + ex.StackTrace);
         //   }
         //}
         //var Buffer = ArrayPool<byte>.Shared.Rent(4096 * 2);
         //int read = await Sock.ReceiveAsync(Buffer);
         //if (read == 0)
         //{
         //   Logger.Log("Received zero.");
         //}
         //else
         //{
         //   string readS = Encoding.ASCII.GetString(Buffer, 0, read);
         //   Logger.Log(readS);
         //}
         //ArrayPool<byte>.Shared.Return(Buffer);
         return Sock;
      }
      Socket Sock;
      public TcpToUDPPipe(Socket ConnectedSocket)
      {
         Sock = ConnectedSocket;
      }
      public async Task RunTunnel()
      {
         var Buffer = ArrayPool<byte>.Shared.Rent(4096);
         //for (int i = 0; i < 11; i++)
         //{
         //   int read = await Sock.ReceiveAsync(Buffer);
         //   if (read == 0)
         //   {
         //      return;
         //   }
         //   else
         //   {
         //      string readS = Encoding.ASCII.GetString(Buffer, 0, read);
         //      if (!readS.Equals("BEGIN"))
         //      {
         //         await Sock.SendAsync(Encoding.ASCII.GetBytes("please send a valid request.\n"));
         //         continue;
         //      }
         //      else if (readS.Equals("BEGIN"))
         //      {
         //         break;
         //      }
         //   }
         //   if (i == 10)
         //   {
         //      return;
         //   }
         //}
         //using (NetworkStream NetPipe = new NetworkStream(Sock,true))
         //using (NegotiateStream NetPipe = new NegotiateStream(NetStr))
         {
            //await NetPipe.AuthenticateAsServerAsync();
            //await NetPipe.AuthenticateAsServerAsync(new NetworkCredential("SRVU", "SRVP"), ProtectionLevel.EncryptAndSign, System.Security.Principal.TokenImpersonationLevel.Anonymous);
            int read = await Sock.ReceiveAsync(Buffer);
            //read = await NetPipe.ReadAsync(Buffer);
            string ReadReq = Encoding.ASCII.GetString(Buffer, 0, read);
            string[] SplitReq = ReadReq.Split('\n');
            string DestinationAddress = string.Empty;
            int DestinationPort = 0;
            bool isIPv4 = false;
            if (SplitReq[0].Trim() == "HOST")//1 is host or ip and 2 is port
            {
               DestinationAddress = SplitReq[1].Trim();
               DestinationPort = int.Parse(SplitReq[2].Trim());
               isIPv4 = bool.Parse(SplitReq[3].Trim());
            }
            IPAddress DestinationIP;
            if (isIPv4)
            {
               DestinationIP = (await Dns.GetHostEntryAsync(DestinationAddress)).AddressList.First(x => x.AddressFamily == AddressFamily.InterNetwork);
            }
            else
            {
               DestinationIP = (await Dns.GetHostEntryAsync(DestinationAddress)).AddressList.First();
            }
            IPEndPoint DestinationEndpoint = new IPEndPoint(DestinationIP, DestinationPort);
            await Sock.SendAsync(Encoding.ASCII.GetBytes("SUCCESS"));
            IPEndPoint? ClientIPEP = null;
            //await NetPipe.FlushAsync();
            using (UdpClient UCLI = new UdpClient(0, isIPv4 ? AddressFamily.InterNetwork : AddressFamily.InterNetworkV6))
            {
               Logger.Log("StartedUdpClient");
               Task ReaderTask = Task.Run(async () =>
               {
                  try
                  {
                     int ReadBytes = 0;
                     while ((ReadBytes = await Sock.ReceiveAsync(Buffer)) > 0)
                     {
                        //Logger.Log("Received " + ReadBytes + "From a client");
                        await UCLI.SendAsync(Buffer, ReadBytes, DestinationEndpoint);
                     }
                     Logger.Log("Exited rec loop");
                  }
                  catch (Exception ex)
                  {
                     Logger.Log(ex);
                  }
               });
               Task WriterTask = Task.Run(async () =>
               {
                  try
                  {
                     UdpReceiveResult RecRes;
                     while (true)
                     {
                        RecRes = await UCLI.ReceiveAsync();
                        await Sock.SendAsync(RecRes.Buffer);
                        //Logger.Log("Sent " + RecRes.Buffer.Length + "to a client");
                        if (RecRes.Buffer.Length == 0)
                        {
                           Logger.Log("Broken (Empty) Buffer?");
                           break;
                        }
                     }
                     Logger.Log("Exited Send loop");

                  }
                  catch (Exception ex)
                  {
                     Logger.Log(ex);
                  }
               });

               await ReaderTask;
               await WriterTask;
            }
         }
         Logger.Log("Client DC");
         ArrayPool<byte>.Shared.Return(Buffer);
      }
   }
}
