using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using MozUtil.NatUtils;
using STUN;

namespace MozUtil.Clients
{
   public class NormalMozClient : IMozClient
   {
      private readonly string _StunServerAddress;
      private volatile Dictionary<ushort, MozConnection> Connections = new Dictionary<ushort, MozConnection>();
      private TcpListener TcpServer;

      public NormalMozClient(IPEndPoint LocalEP, IPEndPoint ServerEP, string StunSrv,
         int TcpListenPort = 64900, int _HttpListenPort = 64901, int maxConRetires = 10)
      {
         LocalEndPoint = LocalEP;
         ServerEndPoint = ServerEP;
         HttpListenPort = _HttpListenPort;
         this.TcpListenPort = TcpListenPort;
         MaxConnectionRetries = maxConRetires;
         _StunServerAddress = StunSrv;
      }

      public CancellationTokenSource CancellationTS { get; } = new CancellationTokenSource();

      public IPEndPoint LocalEndPoint { get; }

      public IPEndPoint ServerEndPoint { get; }

      public int TcpListenPort { get; }


      public int MaxConnectionRetries { get; set; } = 20;

      public int HttpListenPort { get; set; }

      public async Task Start()
      {
         byte[] ConAck = { 0x00, 0x00, 0x00, 0x00, 0x00, 0x01 };
         Logger.WriteLineWithColor("Creating new udp client...", ConsoleColor.White);
         UdpClient client = new UdpClient(LocalEndPoint);
         Logger.WriteLineWithColor("New udp client created.", ConsoleColor.Green);
         Logger.WriteLineWithColor("using STUN on the new udp client...", ConsoleColor.Green);
         STUNQueryResult Stunres = MozStun.GetStunResult(client.Client, _StunServerAddress);
         int Timeout = 700;
         for (int i = 0; i < 10; i++)
            if (Stunres.QueryError != STUNQueryError.Success)
            {
               await Task.Delay(Timeout * i);
               Stunres = MozStun.GetStunResult(client.Client, _StunServerAddress, Timeout * i);
            }

         if (Stunres.QueryError != STUNQueryError.Success) return;
         Console.WriteLine($"LocalEP: {Stunres.LocalEndPoint} PubEP: {Stunres.PublicEndPoint} Nat:{Stunres.NATType}");
         bool ConnectionEstablished = false;
         _ = Task.Run(async () =>
         {
            ushort ConID = 0;
            UdpReceiveResult udpres;
            while (!CancellationTS.IsCancellationRequested)
            {
               udpres = await client.ReceiveAsync();
               ConID = BitConverter.ToUInt16(udpres.Buffer, 0);
               if (ConID == 0)
               {
                  ConID = BitConverter.ToUInt16(udpres.Buffer, 2);
                  if (ConID == 0)
                  {
                     ConID = BitConverter.ToUInt16(udpres.Buffer, 4);
                     if (ConID == 256)
                        //Test packet
                        ConnectionEstablished = true;
                  }

                  if (Connections.ContainsKey(ConID))
                  {
                     Connections[ConID].Close();
                     Console.WriteLine($"Connection {ConID} Closed by the server.");
                  }

                  continue;
               }

               _ = HandleIncomingUdpDataAsync(udpres.Buffer[2..], ServerEndPoint, client, ConID);
            }
         }, CancellationTS.Token);
         int retries = 0;
         while (!ConnectionEstablished)
         {
            Console.WriteLine("Sending hello packet...");
            await client.SendAsync(ConAck, ConAck.Length, ServerEndPoint);
            if (retries == MaxConnectionRetries)
            {
               Logger.WriteLineWithColor("Client hello failed.", ConsoleColor.Red);
               throw new TimeoutException("Handshake Timed out");
            }

            retries++;
            await Task.Delay(100);
         }

         Console.WriteLine("Connection Established");
         TcpServer = new TcpListener(IPAddress.Any, TcpListenPort);
         TcpServer.Server.NoDelay = true;
         TcpServer.Start();
         Logger.WriteLineWithColor($"Socks5 server started on port {TcpListenPort}", ConsoleColor.Green);
         ushort ConnectionID = 1;
         while (!CancellationTS.IsCancellationRequested)
         {
            HandleClientAsync(await TcpServer.AcceptTcpClientAsync(), ConnectionID, ServerEndPoint, client);
            ConnectionID++;
            //HandleClient(await Srv.AcceptTcpClientAsync());
         }
      }

      private void HandleClientAsync(TcpClient Client, ushort id, IPEndPoint ServerUDPEP, UdpClient udpCli)
      {
         Task.Run(async () =>
         {
            MozConnection Con = new MozConnection(Client, id, ServerUDPEP, udpCli);

            Con.ConnectionClosed += Con_ConnectionClosed;
            lock (Connections)
            {
               try
               {
                  Connections.Add(id, Con);
               }
               catch (Exception)
               {
                  Console.WriteLine("Could not add new connection {0}", id);
               }
            }

            await Con.HandleReadConnectionAsync();
         });
      }

      private void Con_ConnectionClosed(object sender, ushort e)
      {
         lock (Connections)
         {
            Connections.Remove(e);
         }
      }

      private async ValueTask HandleIncomingUdpDataAsync(byte[] Data, IPEndPoint ServerEP, UdpClient udpCli,
         ushort ConID)
      {
         try
         {
            //var RecvRes = await udpClient.ReceiveAsync();

            //byte[] RecvRes = await Encryptor.DecryptAsync(EncRes.Buffer);

            //Console.WriteLine("Client received {0} bytes from the server", res.Buffer.Length);
            //if (DestConID == 0)
            //{
            //   //Server command. Unused for now
            //   //continue;
            //   return;
            //}
            //else
            //{
            //var seg = new ArraySegment<byte>(RecvRes.Buffer, 2, RecvRes.Buffer.Length - 2);
            //var seg = Data.Slice(2).ToArray();
            await Connections[ConID].SendDataWriteAsync(Data);
            //}
         }
         catch (KeyNotFoundException)
         {
            Console.WriteLine("a Connection is missing: " + ConID);
            try
            {
               byte[] SendData = new byte[4];
               BitConverter.GetBytes(ConID).CopyTo(SendData, 2);
               udpCli.Send(SendData, 4, ServerEP);
               //Rufclient.SendUnconnected(SendData, ServerEP);
               //RufCon.Send(SendData, 0, true, NKey.NotificationKey);
               //Interlocked.Increment(ref NKey.NotificationKey);
            }
            catch (Exception ex)
            {
               Console.WriteLine(ex.Message);
            }
            //At the server side, if the connection id was 0, remove the connection with the id of the following bytes to shorts
         }
         catch (SocketException)
         {
            Console.WriteLine("Socket exception occured for udpreceive async");
            throw;
         }
         catch (ObjectDisposedException)
         {
            Console.WriteLine("WTF WHICH OBJECT IS DISPOSED????!!!");
         }
         catch (Exception ex)
         {
            Console.WriteLine(ex.Message);
            //Unknown exception when writing to tcpclient. aborting.
         }
      }
   }
}