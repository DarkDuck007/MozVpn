using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using LiteNetLib;
using MihaZupan;
using MozUtil.NatUtils;
using MozUtil.Types;
using STUN;

namespace MozUtil.Clients
{
   //public static class NMHolder
   //{
   //   volatile public static List<NetManager> _NetManagers = new List<NetManager>();
   //   //volatile public static List<NetPeer> _Peers = new List<NetPeer>();
   //   //volatile public static List<NetPeer> _Peers = new List<NetPeer>();
   //}
   public class LiteNetMozClient : INetEventListener, IMozClient, IDisposable
   {
      private Task _EventCaller;
      private volatile Dictionary<byte, short> ChannelConnectionCount = new Dictionary<byte, short>();

      private volatile Dictionary<ushort, MozLiteNetReliableConnection> Connections =
         new Dictionary<ushort, MozLiteNetReliableConnection>();

      private HttpToSocks5Proxy HttpProxy;
      private NetManager LiteNetManager;
      private string LNConnectionKey;
      private int ReconRetries;
      private TcpListener TcpServer;
      private TcpListener MtServer;

      public LiteNetMozClient(IPEndPoint LocalEP, IPEndPoint ServerEP, byte MaxChannelCount,
         int TcpListenPort = 64900, int HttpProxyListenPort = 63899, int maxConRetires = 5, int MtProxyListenPort = -1)
      {
         LocalEndPoint = LocalEP;
         ServerEndPoint = ServerEP;
         _TcpListenPort = TcpListenPort;
         _HttpListenPort = HttpProxyListenPort;
         MaxConnectionRetries = maxConRetires;
         ChannelsCount = MaxChannelCount;
         if (MtProxyListenPort == -1)
         {
            MtProxyListenPort = TcpListenPort - 50;
         }
         _MtSrvPort = MtProxyListenPort;
         //MaxOutboundPackets = (int)Math.Ceiling((double)(32 / ChannelsCount));
         MaxOutboundPackets = 50;
      }

      public int DisconnectTimeout { get; set; } = 20000;
      public int symmetricConnectionClientCount { get; set; } = 100;
      public byte ChannelsCount { get; } = 16;

      private int _HttpListenPort;
      public int HttpListenPort { get { return _HttpListenPort; } }

      private int _MtSrvPort;

      public int MtServerPort
      {
         get { return _MtSrvPort; }
         set { _MtSrvPort = value; }
      }

      public bool isRunning => LiteNetManager.IsRunning;
      public NetStatistics NetStats => LiteNetManager.Statistics;
      public int ConnectiounsCount => Connections.Count;
      public int MaxOutboundPackets { get; set; }
      public event EventHandler<int>? LatencyUpdate;
      public event EventHandler<StatusResult>? StatusUpdate;
      public event EventHandler<Tuple<int, int, int>>? PortsChanged;
      public event EventHandler<ServerStatusInformation>? ServerStatusInformationUpdated;
      public event EventHandler? HttpKeepAliveRequested;
      public bool EnableServerStatusInformationStreamingForPeer(int PeerID = -1, int interval = 1000)
      {
         try
         {
            if (PeerID == -1)
            {
               List<NetPeer> ConnectedPeers = new List<NetPeer>();
               LiteNetManager.GetPeersNonAlloc(ConnectedPeers, ConnectionState.Connected);
               PeerID = ConnectedPeers[0].Id;
            }
            byte[] SendBuffer = ClientCommandUtils.BuildServerStatsRequestCommand(interval);
            LiteNetManager.GetPeerById(PeerID).Send(SendBuffer, DeliveryMethod.ReliableUnordered);
         }
         catch (Exception ex)
         {
            Logger.LogException(ex);
            return false;
         }
         return true;
      }

      public void Dispose()
      {
         Task.Run(() =>
         {
            for (int i = 0; i < LiteNetManager.ConnectedPeersCount; i++) LiteNetManager.GetPeerById(i).Disconnect();
            LiteNetManager.Stop();
         });
         foreach (var item in Connections.Values) item.Close();
         if (HttpProxy != null) HttpProxy.StopInternalServer();
         if (TcpServer is not null)
            TcpServer.Stop();
         if (MtServer is not null)
            MtServer.Stop();

         StatusUpdate?.Invoke(this, StatusResult.UDPDisconnected);
      }

      public IPEndPoint LocalEndPoint { get; }

      public IPEndPoint ServerEndPoint { get; }

      private int _TcpListenPort;
      public int TcpListenPort
      {
         get { return _TcpListenPort; }
      }

      //public int UdpListenPort { get; }

      public int MaxConnectionRetries { get; set; } = 20;

      public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber,
         DeliveryMethod deliveryMethod)
      {
         byte[] RecData = reader.RawData[reader.UserDataOffset..(reader.UserDataSize + reader.UserDataOffset)];
         //Logger.Log($"Client received {RecData.Length - 2} ({RecData.Length}) bytes of data from the server");
         ushort ConID = BitConverter.ToUInt16(RecData, 0);
         if (ConID == 0)
         {
            ConID = BitConverter.ToUInt16(RecData, 2);
            if (ConID == 0)//Server commands over UDP
            {
               ushort ServerCommandValue = BitConverter.ToUInt16(RecData, 4);
               ServerCommands ServerCommand = (ServerCommands)((int)ServerCommandValue);
               switch (ServerCommand)
               {
                  case ServerCommands.ServerStatusUpdate:
                     ServerStatusInformation SSI = ServerCommandUtils.ServerStatusInformationFromBytes(RecData, 6);
                     ServerStatusInformationUpdated?.Invoke(this, SSI);
                     break;
                  case ServerCommands.EndToEndPipeCreationResult:
                     if (RecData[6] == 255)//MtProtoDesu
                     {
                        MREForMtProtoCreation.Set();
                        //Console.Beep(2500, 100);

                        //Console.Beep();
                     }
                     break;
                  case ServerCommands.KeepAlive:
                     HttpKeepAliveRequested?.Invoke(this, EventArgs.Empty);
                     break;
                  default:
                     break;
               }
            }
            else if (Connections.ContainsKey(ConID))
            {
               Connections[ConID].Close();
               Logger.Log($"Connection {ConID} Closed by the server.");
            }
            return;
         }
         HandleIncomingUdpDataAsync(ConID, channelNumber, RecData[2..], peer).Wait();
         //throw new NotImplementedException();
      }
      public async void AttemptReconnect(udpConnectionInfo ReconInfo, STUNNATType NatType)
      {
         if (LiteNetManager.ConnectedPeersCount == 0) StatusUpdate?.Invoke(this, StatusResult.UDPReconnecting);
         await Start(ReconInfo, NatType, true);
      }
      public void OnPeerConnected(NetPeer peer)
      {
         //if (!NMHolder._Peers.Contains(peer))
         //{
         //NMHolder._Peers.Add(peer);
         //}
         ReconRetries = 0;
         //Logger.Log($"Connected to the server: {peer.Id} MTU: {peer.Mtu.ToString()}");
         StatusUpdate?.Invoke(this, StatusResult.UDPConnected);
      }

      public void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
      {
         if (ReferenceEquals(LiteNetManager, null)) return;
         if (LiteNetManager.IsRunning)
         {
            if (ReconRetries < MaxConnectionRetries)
            {
               if (LiteNetManager.ConnectedPeersCount == 0) StatusUpdate?.Invoke(this, StatusResult.UDPReconnecting);

               LiteNetManager.Connect(ServerEndPoint, LNConnectionKey);
               ReconRetries++;
            }

            ////NMHolder._Peers.Remove(peer);
            Logger.Log($"Disconnecter from the server: {peer.Id} {disconnectInfo.Reason}");
            if (LiteNetManager.ConnectedPeersCount == 0) StatusUpdate?.Invoke(this, StatusResult.UDPDisconnected);
            //throw new NotImplementedException();
         }
      }

      public void OnNetworkError(IPEndPoint endPoint, SocketError socketError)
      {
         if (socketError == SocketError.TimedOut) StatusUpdate?.Invoke(this, StatusResult.UDPError);
         Logger.Log($"LiteNetError: {socketError}");
         //throw new NotImplementedException();
      }

      public void OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader,
         UnconnectedMessageType messageType)
      {
         //True udp packet (Shadowsocks, wireguard, etc)
         //throw new NotImplementedException();
      }

      public void OnNetworkLatencyUpdate(NetPeer peer, int latency)
      {
         //Logger.Log($"Latency: {latency * 2} PL%:{peer.Statistics.PacketLossPercent}");
         //throw new NotImplementedException();
         LatencyUpdate?.Invoke(peer.Id, latency);
      }

      public void OnConnectionRequest(ConnectionRequest request)
      {
         //throw new NotImplementedException();
      }

      public async Task Start(udpConnectionInfo udpInfo, STUNNATType NatType, bool IsReconnect = false)
      {
         try
         {
            StatusUpdate?.Invoke(this, StatusResult.UDPConnecting);
            if (udpInfo.ConnectionKey == null) throw new ArgumentException("No connection key was supplied.");
            if (NatType == STUNNATType.Symmetric)
               await StartSymmetric(udpInfo, IsReconnect);
            //throw new NotImplementedException();
            else
               await StartNonSymmetric(udpInfo, IsReconnect);
         }
         catch (Exception ex)
         {
            Logger.Log(ex.Message + Environment.NewLine + ex.StackTrace);
         }
      }

      private async Task StartSymmetric(udpConnectionInfo udpInfo, bool isReconnect = false)
      {
         if (udpInfo.ConnectionKey == null) throw new ArgumentException("No connection key was supplied.");
         LNConnectionKey = udpInfo.ConnectionKey;
         List<NetManager> ManagerPool = new List<NetManager>();
         for (int i = 0; i < symmetricConnectionClientCount; i++)
         {
            NetManager LNManager = new NetManager(this)
            {
               ChannelsCount = ChannelsCount,
               UnsyncedEvents = true,
               AutoRecycle = true,
               UpdateTime = 50,
               SimulatePacketLoss = false,
               SimulateLatency = false,
               //SimulationPacketLossChance = 5,
               SimulationMinLatency = 200,
               SimulationMaxLatency = 300,
               EnableStatistics = true,
               DisconnectTimeout = DisconnectTimeout,
               PingInterval = 3000,
               MaxConnectAttempts = MaxConnectionRetries //Change later
            };
            ManagerPool.Add(LNManager);
            //if (i % 10 == 0)
            //{
            //   await Task.Delay(10);
            //}
         }

         List<Task> TasksList = new List<Task>();
         //int k = 0;
         foreach (NetManager manager in ManagerPool)
            //k++;
            //if (k % 10 == 0)
            //{
            //   await Task.Delay(10);
            //}
            TasksList.Add(Task.Run(async () =>
            {
               try
               {
                  manager.StartInManualMode(0);
                  //manager.Start();
                  //Logger.Log("Starting manager");
                  manager.Connect(ServerEndPoint, LNConnectionKey);
                  //manager.PollEvents();
                  //manager.ManualUpdate(0);
                  manager.PollEvents();
                  manager.ManualUpdate(20);

                  //for (int i = 0; i < 4; i++)
                  //{
                  //   await Task.Delay(200);
                  //   manager.PollEvents();
                  //   manager.ManualUpdate(200);
                  //}
                  while (manager.GetPeersCount(ConnectionState.Outgoing) > 0) await Task.Delay(100);
                  //manager.PollEvents();
                  //manager.ManualUpdate(50);
                  //for (int i = 0; i < 1; i++)
                  //{
                  //   await Task.Delay(200);
                  //   manager.PollEvents();
                  //   manager.ManualUpdate(200);
                  //}
               }
               catch (Exception ex)
               {
                  Logger.Log(ex.Message + Environment.NewLine + ex.StackTrace);
               }
               //if (manager.ConnectedPeersCount == 0)
               //{
               //   await Task.Delay(DisconnectTimeout);
               //}
            }));

         Stopwatch ST = new Stopwatch();
         int WaitTime = 0;
         bool isWaiting = true;
         Task T = Task.Run(async () =>
         {
            while (isWaiting)
            {
               ST.Restart();
               foreach (NetManager manager in ManagerPool)
               {
                  manager.PollEvents();
                  manager.ManualUpdate(WaitTime);
               }

               if (WaitTime < 20) await Task.Delay(16);
               ST.Stop();
               WaitTime = (int)ST.ElapsedMilliseconds;
            }
         });
         Task.WaitAny(TasksList.ToArray());
         isWaiting = false;
         await T;
         T.Dispose();

         int ConnectedManagers = 0;
         foreach (NetManager item in ManagerPool)
            if (item.ConnectedPeersCount == 0)
            {
               //_ = Task.Run(() =>
               //{
               //   try
               //   {
               //      item.Stop();
               //   }
               //   catch (Exception)
               //   {
               //   }
               //});
            }
            else
            {
               ConnectedManagers++;
            }

         if (ConnectedManagers == 0)
         {
            Task.WaitAll(TasksList.ToArray());
            foreach (NetManager item in ManagerPool)
               if (item.ConnectedPeersCount > 0)
                  ConnectedManagers++;
            //_ = Task.Run(() => item.Stop());
         }

         if (ConnectedManagers == 0) throw new TimeoutException("Could not connect to the server");

         //NetManager? LiteNetManager = null;
         for (int i = 0; i < ManagerPool.Count; i++)
            if (ManagerPool[i].ConnectedPeersCount != 0)
            {
               LiteNetManager = ManagerPool[i];
               _EventCaller = Task.Run(async () =>
               {
                  while (LiteNetManager.IsRunning)
                  {
                     ST.Stop();
                     LiteNetManager.PollEvents();
                     LiteNetManager.ManualUpdate((int)ST.ElapsedMilliseconds);
                     ST.Restart();
                     await Task.Delay(10);
                  }
               });
               //LiteNetManager.Stop();
               //int p = LiteNetManager.LocalPort;
               //await Task.Delay(10);
               //LiteNetManager.Start(p);
               //LiteNetManager.Connect(ServerEndPoint, LNConnectionKey);
               ManagerPool.Remove(LiteNetManager);
               break;
            }

         _ = Task.Run(() =>
         {
            Task.WaitAll(TasksList.ToArray(), 20000);
            ConnectedManagers = 0;
            foreach (NetManager item in ManagerPool)
               if (item.ConnectedPeersCount > 0)
               {
                  ConnectedManagers++;
                  _ = Task.Run(() => item.Stop());
               }
         });
         //await Task.Delay(10000);
         //if (LiteNetManager.ConnectedPeersCount == 0)
         //{
         //   for (int i = 0; i < ManagerPool.Count; i++)
         //   {
         //      if (ManagerPool[i].ConnectedPeersCount != 0)
         //      {
         //         LiteNetManager = ManagerPool[i];
         //         ManagerPool.RemoveAt(i);
         //         break;
         //      }
         //   }
         //}
         foreach (var item in ManagerPool)
            _ = Task.Run(() =>
            {
               try
               {
                  if (item.IsRunning) item.Stop();
               }
               catch (Exception ex)
               {
                  Logger.Log(ex.Message + Environment.NewLine + ex.StackTrace);
               }
            });
         _ = Task.Run(() =>
         {
            foreach (var item in ManagerPool)
               try
               {
                  if (item.IsRunning) item.Stop();
               }
               catch (Exception ex)
               {
                  Logger.Log(ex.Message + Environment.NewLine + ex.StackTrace);
               }

            ManagerPool.Clear();
         });
         TasksList.Clear();
         GC.Collect();
         GC.Collect(int.MaxValue, GCCollectionMode.Forced);
         if (ReferenceEquals(LiteNetManager, null))
         {
            throw new Exception("Connection Failed.");
         }

         if (LiteNetManager.ConnectedPeersCount > 0)
         {
            Logger.Log("Starting internal server...");
            await RunInternalServerAsync();
         }
      }

      private async Task StartNonSymmetric(udpConnectionInfo udpInfo, bool isReconnect = false)
      {
         if (udpInfo.ConnectionKey == null) throw new ArgumentException("No connection key was supplied.");
         LNConnectionKey = udpInfo.ConnectionKey;

         if (!isReconnect)
         {
            LiteNetManager = new NetManager(this)
            {
               ChannelsCount = ChannelsCount,
               UnsyncedEvents = true,
               AutoRecycle = true,
               UpdateTime = 1,
               SimulatePacketLoss = false,
               SimulateLatency = false,
               SimulationPacketLossChance = 5,
               SimulationMinLatency = 200,
               SimulationMaxLatency = 300,
               EnableStatistics = true,
               DisconnectTimeout = 20000,
               PingInterval = 5000,
               MaxConnectAttempts = MaxConnectionRetries
            };
            LiteNetManager.Start(LocalEndPoint.Port);
         }
         //NMHolder._NetManagers.Add(NM1);
         //NM1.Start(LocalEndPoint.Address, IPAddress.IPv6Any, LocalEndPoint.Port);
         //byte[] UnconMes = new byte[2] { 255, 255 };
         //for (int i = 0; i < 3; i++)
         //{
         //   NM1.SendUnconnectedMessage(UnconMes, ServerEndPoint);
         //}
         LiteNetManager.Connect(ServerEndPoint, LNConnectionKey);
         while (LiteNetManager.ConnectedPeersCount == 0) await Task.Delay(100);
         if (!isReconnect)
         {
            await RunInternalServerAsync();
         }
      }
      ManualResetEvent MREForMtProtoCreation = new ManualResetEvent(false);
      private async Task RunInternalServerAsync()
      {
         for (int i = 0; i < ChannelsCount; i++) ChannelConnectionCount.Add((byte)i, 0);
         Logger.Log("Connection Established");


         for (int i = 0; i <= 50; i++)
         {
            if (i == 50)
            {
               Logger.Log("Couldn't bind the socks server");
               throw new Exception("Couldn't bind the socks server");
            }
            try
            {
               TcpServer = new TcpListener(IPAddress.Any, TcpListenPort);
               TcpServer.Server.NoDelay = true;
               TcpServer.Start();
               break;
            }
            catch (SocketException ex)
            {
               _TcpListenPort++;
               try
               {
                  TcpServer.Stop();
                  TcpServer.Server.Dispose();
               }
               catch (Exception ex2)
               {
                  Logger.Log(ex2.Message + Environment.NewLine + ex2.StackTrace);
               }
               Logger.Log(ex.Message + Environment.NewLine + ex.StackTrace);
            }
         }
         Logger.WriteLineWithColor($"Socks5 server started on {IPAddress.Any}:{TcpListenPort}", ConsoleColor.Green);
         Logger.WriteLineWithColor($"socks5://127.0.0.1:{TcpListenPort}", ConsoleColor.Green);
         //for (int i = 0; i <= 50; i++)
         //{
         //   if (i == 50)
         //   {
         //      Logger.Log("Couldn't bind the MtProto server");
         //      throw new Exception("Couldn't bind the MtProto server");
         //   }
         //   try
         //   {
         //      MtServer = new TcpListener(IPAddress.Any, _MtSrvPort);
         //      MtServer.Server.NoDelay = true;
         //      MtServer.Start();
         //      break;
         //   }
         //   catch (SocketException ex)
         //   {
         //      _MtSrvPort++;
         //      try
         //      {
         //         MtServer.Stop();
         //         MtServer.Server.Dispose();
         //      }
         //      catch (Exception ex2)
         //      {
         //         Logger.Log(ex2.Message + Environment.NewLine + ex2.StackTrace);
         //      }
         //      Logger.Log(ex.Message + Environment.NewLine + ex.StackTrace);
         //   }
         //}
         //Logger.WriteLineWithColor($"MTP server started on {IPAddress.Any}:{_MtSrvPort}", ConsoleColor.Green);
         //Logger.WriteLineWithColor($"mtproto://127.0.0.1:{_MtSrvPort} secret: 437574654C6F63616C50726F78792121", ConsoleColor.Green);

         for (int i = 0; i <= 50; i++)
         {
            if (i == 50)
            {
               Logger.Log("Couldn't bind the HTTP server");
               throw new Exception("Couldn't bind the HTTP server");
            }
            try
            {
               HttpProxy = new HttpToSocks5Proxy("127.0.0.1", TcpListenPort, HttpListenPort);
               break;
            }
            catch (SocketException)
            {
               _HttpListenPort++;
            }

         }
         Logger.WriteLineWithColor($"HTTP server started on {IPAddress.Any}:{HttpListenPort}", ConsoleColor.Green);
         Logger.WriteLineWithColor($"Http://127.0.0.1:{HttpProxy.InternalServerPort}", ConsoleColor.Green);
         //Logger.Log(HttpProxy.ToString());
         PortsChanged?.Invoke(this, new Tuple<int, int, int>(TcpListenPort, HttpListenPort, MtServerPort));
         ushort ConnectionID = 1;
         object _LockObj = new object();
         Task MtProxyTask = Task.Run(async () =>
         {
            while (LiteNetManager.ConnectedPeersCount != 0 || ReconRetries < MaxConnectionRetries)
            {
               var cli = await MtServer.AcceptTcpClientAsync();
               ushort MyConnectionID = 0;
               lock (_LockObj)
               {
                  if (ConnectionID == ushort.MaxValue)
                  {
                     ConnectionID = 1;
                  }
                  while (Connections.ContainsKey((ushort)ConnectionID)) ConnectionID++;

                  MyConnectionID = ConnectionID;
                  ConnectionID++;
               }
               _ = Task.Run(async () =>
                 {
                    try
                    {
                       List<NetPeer> ConnectedPeers = new List<NetPeer>();
                       LiteNetManager.GetPeersNonAlloc(ConnectedPeers, ConnectionState.Connected);
                       int PeerID = ConnectedPeers[0].Id;
                       byte[] SendBuffer = new byte[8];
                       byte[] CMDB = BitConverter.GetBytes((int)ClientCommands.NewMtProtoPipe);
                       Array.Copy(CMDB, 0, SendBuffer, 4, CMDB.Length);
                       byte[] ConID = BitConverter.GetBytes(ConnectionID);
                       Array.Copy(ConID, 0, SendBuffer, 6, ConID.Length);
                       LiteNetManager.GetPeerById(PeerID).Send(SendBuffer, DeliveryMethod.ReliableUnordered);
                       byte SelectedChannel = 0;
                       lock (ChannelConnectionCount)
                       {
                          short Lowest = ChannelConnectionCount.Values.Min();
                          SelectedChannel = ChannelConnectionCount.FirstOrDefault(x => x.Value == Lowest).Key;
                       }
                       //Console.Beep(1000, 100);
                       LiteNetManager.GetPeerById(PeerID).Send(SendBuffer, SelectedChannel, DeliveryMethod.ReliableOrdered);
                       MREForMtProtoCreation.Reset();
                       bool res = MREForMtProtoCreation.WaitOne(5000);
                       //Console.Beep(5000, 100);

                       //if (res == false)
                       //{
                       //   cli.Dispose();
                       //   continue;
                       //}
                    }
                    catch (Exception ex)
                    {
                       Logger.LogException(ex);
                    }
                    HandleClientAsync(cli, (ushort)MyConnectionID);
                 });
               //Interlocked.Increment(ref ConnectionID);
               //HandleClient(await Srv.AcceptTcpClientAsync());
            }
         });
         StatusUpdate?.Invoke(this, StatusResult.InternalServerStarted);
         //while (!CTS.IsCancellationRequested)
         try
         {
            while (LiteNetManager.ConnectedPeersCount != 0 || ReconRetries < MaxConnectionRetries)
            {
               var cli = await TcpServer.AcceptTcpClientAsync();
               lock (_LockObj)
               {
                  if (ConnectionID == ushort.MaxValue)
                  {
                     ConnectionID = 1;
                  }
                  while (Connections.ContainsKey((ushort)ConnectionID)) ConnectionID++;
                  HandleClientAsync(cli, (ushort)ConnectionID);
                  ConnectionID++;
               }
               //Interlocked.Increment(ref ConnectionID);
               //HandleClient(await Srv.AcceptTcpClientAsync());
            }
         }
         catch (Exception)
         {
         }
         StatusUpdate?.Invoke(this, StatusResult.InternalServerStopped);
         Logger.Log("Internal server stopped.");
         StatusUpdate?.Invoke(this, StatusResult.UDPDisconnected);
      }

      private void HandleClientAsync(TcpClient Client, ushort id)
      {
         byte SelectedChannel = 0;
         lock (ChannelConnectionCount)
         {
            short Lowest = ChannelConnectionCount.Values.Min();
            SelectedChannel = ChannelConnectionCount.FirstOrDefault(x => x.Value == Lowest).Key;
            ChannelConnectionCount[SelectedChannel]++;
         }

         Task.Run(async () =>
         {
            MozLiteNetReliableConnection Con = new MozLiteNetReliableConnection(id, SelectedChannel, Client, 0,
               ref LiteNetManager, MaxOutboundPackets);
            //Con.PeerID = 0;
            Con.DataAvailable += Con_DataAvailable;
            Con.ConnectionClosed += Con_ConnectionClosed;
            lock (Connections)
            {
               try
               {
                  Connections.Add(id, Con);
               }
               catch (Exception)
               {
                  Logger.Log($"Could not add new connection {id}");
               }
            }

            await Con.StartConnectionAsync();
         });
      }
      //AutoResetEvent ResetEvent = new AutoResetEvent(true);
      private void Con_DataAvailable(object sender, MozPacket e)
      {
         if (LiteNetManager.IsRunning)
            try
            {
               while (LiteNetManager.GetPeerById(e.PeerID).GetPacketsCountInReliableQueue(e.ChannelID, true) >
                      MaxOutboundPackets) Thread.Sleep(1);
               //Logger.Log($"Client is sending ({e.Length - 2}) {e.Length} bytes to server Con ID {((MozLiteNetReliableConnection)sender).ConnectionID} channel {e.ChannelID}");
               LiteNetManager.GetPeerById(e.PeerID).Send(e.RawData, e.StartIndex, e.Length, e.ChannelID,
                  DeliveryMethod.ReliableOrdered);
               //ResetEvent.Set();
            }
            catch (Exception ex)
            {
               Logger.Log(ex.Message + Environment.NewLine + ex.StackTrace);
            }
      }

      private void Con_ConnectionClosed(object? sender, ushort e)
      {
         lock (Connections)
         {
            Connections.Remove(e);
         }

         lock (ChannelConnectionCount)
         {
            ChannelConnectionCount[((MozLiteNetReliableConnection)sender).BoundChannelID]--;
         }
      }

      private async Task HandleIncomingUdpDataAsync(ushort ConnectionID, byte ChannelID, ArraySegment<byte> Data,
         NetPeer peer)
      {
         try
         {
            await Connections[ConnectionID].SendDataToClientAsync(Data);
         }
         catch (KeyNotFoundException)
         {
            //Logger.Log("a Connection is missing: " + ConnectionID);
            try
            {
               byte[] SendData = new byte[4];
               BitConverter.GetBytes(ConnectionID).CopyTo(SendData, 2);
               peer.Send(SendData, ChannelID, DeliveryMethod.ReliableUnordered);
               //udpCli.Send(SendData, 4, ServerEP);
            }
            catch (Exception ex)
            {
               Logger.Log(ex.Message);
            }
            //At the server side, if the connection id was 0, remove the connection with the id of the following bytes to shorts
         }
         catch (ObjectDisposedException)
         {
            Logger.Log("WTF WHICH OBJECT IS DISPOSED????!!!");
         }
         catch (Exception ex)
         {
            Logger.Log(ex.Message + ex.StackTrace);
            //Unknown exception when writing to tcpclient. aborting.
         }
      }
   }
}