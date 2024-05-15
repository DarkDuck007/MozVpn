using MozUtil.NatUtils;
using MozUtil.Types;
using MozUtil;
using System.Diagnostics;
using System.Net.Sockets;
using STUN;
using System.Net;
using System.Text;
using LiteNetLib;

namespace DownloadSpeedBenchLiteNet
{
   internal class Program
   {
      public static int DataRate = 0;
      bool LocalMode = false;
      string ServerURL = "http://127.0.0.1:5000";
      private event EventHandler<byte[]>? ServerCommandOverHTTP;
      int HolePunchingTimeout = 10000;
      UdpClient? stunClient;
      private STUNQueryResult stunResult;
      byte[] KeepAlivePacket = new byte[] { 255, 255 };
      int ChannelCount = 1;
      int TimeLen = 30000;
      CancellationTokenSource CTS = new CancellationTokenSource();
      string StunServerOrg = "stunserver.stunprotocol.org:3478";
      static void Main(string[] args)
      {
         new Program().Run().Wait();
         Console.ReadLine();
      }
      async Task Run()
      {
         ServerCommandOverHTTP += Program_ServerCommandOverHTTP;
         if (LocalMode)
            ServerURL = "http://localhost:5209/LiteNetSpeedTest";
         else
            ServerURL = "http://dirtypx.somee.com/LiteNetSpeedTest";

         try
         {
            int StunTimeout = 400;
            int StunTimeoutIncrementor = 1000;
            while (true)
            {
               stunClient = new UdpClient();
               stunResult = MozStun.GetStunResult(stunClient.Client, StunServerOrg, StunTimeout);
               if (stunResult.QueryError == STUNQueryError.Success)
               {
                  break;
               }
               else if (stunResult.QueryError == STUNQueryError.Timedout)
               {
                  stunClient.Dispose();
                  StunTimeout += StunTimeoutIncrementor;
                  Logger.Log("Stun query timed out after :" + StunTimeout);
                  if (StunTimeout > 10000)
                  {
                     Logger.Log("Stun query failed due to timeouts");
                     break;
                  }
               }
               else
               {
                  throw new Exception("StunQueryError: " + stunResult.QueryError + stunResult.ServerErrorPhrase);
               }
            }
            if (LocalMode)
            {
               stunResult.PublicEndPoint.Address = IPAddress.Parse("127.0.0.1");
               stunResult.PublicEndPoint.Port = stunResult.LocalEndPoint.Port;
            }
            Logger.Log($"Nat type: {stunResult.NATType} Local EP{stunResult.LocalEndPoint} Pub EP: {stunResult.PublicEndPoint}");

            //udpPuncher Puncher = new udpPuncher();
            if (((int)stunResult.NATType) <= 4)
            {
               //it isn't symmetric.
               byte[] PunchInfoBytes = MozStatic.SerializePunchInfo(stunResult, HolePunchingTimeout);

               await PollHttpServer(PunchInfoBytes);
            }
            else
            {
               //it is symmetric.
               var PRange = MozStun.GetPortRange(10, StunServerOrg, 2000);
               byte[] PunchInfoBytes = MozStatic.SerializePunchInfo(PRange.StunResults[0], PRange.PortStart, PRange.PortsCount, HolePunchingTimeout);
               await PollHttpServer(PunchInfoBytes);

            }
         }
         catch (Exception ex)
         {
            Logger.Log(ex.StackTrace + ex.Message);
         }
      }
      private async Task StartUdpTun(udpConnectionInfo udpInfo)
      {
         int port = (stunClient.Client.LocalEndPoint as IPEndPoint).Port;
         stunClient?.Dispose();
         ClientNetListener NetListener = new ClientNetListener(((byte)ChannelCount), udpInfo.ipAddress.ToString(), udpInfo.Port, port);
         NetListener.Connect();
      }
      private async Task BeginHolePunching(HolePunchPeerInfo PeerInfo)
      {
         if (((int)PeerInfo.NatType) == 5 && ((int)stunResult.NATType) == 5)
         {
            //throw new NotSupportedException("Symmetric To Symmetric hole punching isn't supported. at least one peer must be non-symmetric.");
            Logger.Log("Symmetric To Symmetric hole punching isn't supported. at least one peer must be non-symmetric.");
         }
         udpPuncher Puncher = new udpPuncher();
         if (((int)stunResult.NATType) <= 4 && ((int)PeerInfo.NatType) <= 4)
         {
            await Puncher.PR2PRPunch(stunClient, new IPEndPoint(PeerInfo.ipAddress, PeerInfo.Port), PeerInfo.HolePunchTimeout);
         }
         else
         {
            Logger.Log("Unsupported");
            await Puncher.PR2PRPunch(stunClient, new IPEndPoint(PeerInfo.ipAddress, PeerInfo.Port), PeerInfo.HolePunchTimeout);
         }
         //else if (((int)stunResult.NATType) == 5 && ((int)PeerInfo.NatType) != 5)
         //{
         //   isPunchInProgress = true;
         //   SymPunchedPorts = await Puncher.Symmetric2PRPunch(new IPEndPoint(PeerInfo.ipAddress, PeerInfo.Port));
         //   //SymPunchedPorts = await Puncher.SymmetricNatPunchToPR(new IPEndPoint(PeerInfo.ipAddress, PeerInfo.Port));
         //   isPunchInProgress = false;
         //}
         //else
         //{
         //   isPunchInProgress = true;
         //   SymPunchedPorts = await Puncher.Symmetric2PRPunch(new IPEndPoint(PeerInfo.ipAddress, PeerInfo.Port));
         //   //SymPunchedPorts = await Puncher.SymmetricNatPunchToPR(new IPEndPoint(PeerInfo.ipAddress, PeerInfo.Port));
         //   isPunchInProgress = false;
         //}
      }
      private async void Program_ServerCommandOverHTTP(object? sender, byte[] e)
      {
         ServerCommand Command = (ServerCommand)e[0];
         if (e == KeepAlivePacket || ((int)Command) == 58)
         {
            //yeah, no. do nothing.
         }
         else
         {
            switch (Command)
            {
               case ServerCommand.BeginHolePunching:
                  HolePunchPeerInfo peerInfo = MozStatic.DeserializePunchInfo(e, 1);
                  _ = Task.Run(async () =>
                  {
                     Stopwatch ST = Stopwatch.StartNew();
                     await BeginHolePunching(peerInfo);
                     ST.Stop();
                     Logger.WriteLineWithColor($"Punching took {ST.ElapsedTicks.ToString()} Ticks ({ST.ElapsedMilliseconds.ToString()} ms)", ConsoleColor.Green);
                  });
                  break;
               case ServerCommand.PunchResult:
                  if (e[1] == 0xaa)
                  {
                     //Failed
                  }
                  else if (e[1] == 0xbb)
                  {
                     Logger.WriteLineWithColor("Punched! ", ConsoleColor.Green);
                     if (e.Length > 2)
                     {
                        Program_ServerCommandOverHTTP(sender, e[2..]);
                     }
                     //Success
                  }
                  else
                  {
                     throw new Exception("Unknown punch status received from the server.");
                  }
                  break;
               case ServerCommand.BeginUdpClient:
                  Logger.WriteLineWithColor("Starting udp client...", ConsoleColor.Magenta);
                  udpConnectionInfo udpInfo = MozStatic.DeserializeUdpConnectionInfo(e, 1);
                  _ = StartUdpTun(udpInfo);
                  break;
               case ServerCommand.KeepAlive:
                  //Do nothing
                  break;
               default:
                  break;
            }
         }
      }

      public async Task PollHttpServer(byte[] SendData)
      {
         HttpWebRequest WebReq = WebRequest.CreateHttp(ServerURL);
         WebReq.Method = "POST";
         WebReq.Headers.Add("Channels", ChannelCount.ToString());
         WebReq.Headers.Add("TimeSec", TimeLen.ToString());
         using (Stream S = WebReq.GetRequestStream())
         {
            S.Write(SendData);
            S.Flush();
         }
         var resp = await WebReq.GetResponseAsync();
         //HttpContent Cont = new HttpContent(SendData);
         //var httpResult = await Client.PostAsync(ServerURL,);
         CTS.Token.Register(() => { resp.Close(); WebReq.Abort(); });
         using (var stream = resp.GetResponseStream())
         {
            byte[] ReadBuffer = new byte[4096];
            int BytesRead;
            while ((BytesRead = await stream.ReadAsync(ReadBuffer, 0, ReadBuffer.Length, CTS.Token)) > 0)
            {
               if (BytesRead == 0)
               {
                  //Stream closed
                  break;
               }
               else
               {
                  Console.WriteLine(Encoding.UTF8.GetString(ReadBuffer, 0, BytesRead));
                  ServerCommandOverHTTP?.Invoke(this, ReadBuffer[0..BytesRead]);
               }
            }
            //throw new Exception("Http connection closed.");
            Console.WriteLine("HttpConnection Closed.");
         }
      }
   }
   class ClientNetListener : INetEventListener, IDisposable
   {
      NetManager? _client;
      public NetPeer? _peer;
      public NetStatistics Stats => _client.Statistics;
      byte[] SendData /*= new byte[4096*5]*/;
      string DestinationHost;
      int DestinationPort;
      public ClientNetListener(byte ChannelCount, string destinationHost, int destinationPort, int LocalP)
      {
         DestinationHost = destinationHost;
         DestinationPort = destinationPort;
         _client = new NetManager(this)
         {
            ChannelsCount = ChannelCount,
            UnsyncedEvents = true,
            AutoRecycle = true,
            UpdateTime = 1,
            SimulatePacketLoss = false,
            SimulateLatency = false,
            SimulationPacketLossChance = 5,
            SimulationMinLatency = 200,
            SimulationMaxLatency = 300,
            EnableStatistics = true,
            DisconnectTimeout = 10000,
            PingInterval = 1000,
         };
         _client.Start(LocalP);
         byte[] UnconMes = new byte[2] { 255, 255 };
         for (int i = 0; i < 5; i++)
         {
            _client.SendUnconnectedMessage(UnconMes, new IPEndPoint(IPAddress.Parse(destinationHost), destinationPort));
         }
      }
      public void Connect()
      {
         _peer = _client.Connect(DestinationHost, DestinationPort, "key");
         SendData = new byte[_peer.GetMaxSinglePacketSize(DeliveryMethod.ReliableOrdered) * 4];
         Console.WriteLine(_peer.Mtu);
      }
      public void Dispose()
      {
         _client.Stop();
      }

      public void OnConnectionRequest(ConnectionRequest request)
      {
         request.RejectForce();
      }

      public void OnNetworkError(IPEndPoint endPoint, SocketError socketError)
      {
         //throw new NotImplementedException();
      }

      public void OnNetworkLatencyUpdate(NetPeer peer, int latency)
      {
         //throw new NotImplementedException();
         Console.WriteLine($"Network latency: {latency} ms, Throughput: {Program.DataRate / 1024} KB/s PL{peer.Statistics.PacketLossPercent}");
         Program.DataRate = 0;
      }

      public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod)
      {
         Program.DataRate += reader.RawDataSize;
         //throw new NotImplementedException();
      }

      public void OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType)
      {
         //throw new NotImplementedException();
      }

      public void OnPeerConnected(NetPeer peer)
      {
         //throw new NotImplementedException();
         _peer = peer;
      }

      public void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
      {
         //throw new NotImplementedException();
      }
   }
}