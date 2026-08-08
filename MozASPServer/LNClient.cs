using LiteNetLib;
using LiteNetLib.Utils;
using Microsoft.AspNetCore.Hosting.Server;
using MozUtil;
using MozUtil.NatUtils;
using MozUtil.Types;
using MTProtoProxy;
using STUN;
using System.Collections.Concurrent;
using System;
using System.Buffers.Text;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Net;
using System.Net.Sockets;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using System.Timers;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace DirtySocksASP
{
   public class LNClient : INetEventListener, IDisposable
   {
      int _disposed;
      int IdlingTImer = 1;
      int SrvLocalPort { get; set; }
      private byte MaxChannels = 64;
      NetManager? _Server;
      public NetManager? LiteNetManager { get { return _Server; } }
      volatile List<NetPeer> _Peers = new List<NetPeer>();
      HttpContext? _context;
      STUNQueryResult? _StunResult;
      string? LNConnectionKey;
      public string? ConnectionKey { get { return LNConnectionKey; } }
      volatile Dictionary<int, int> PeerLatencies = new Dictionary<int, int>();
      volatile Dictionary<ushort, LNConnection?> connections = new Dictionary<ushort, LNConnection?>();
      System.Timers.Timer ServerStatsUpdateTimer = new System.Timers.Timer();
      readonly System.Timers.Timer IdleTransportGuardTimer = new(5000) { AutoReset = true };
      List<int> ServerStatsUpdatePeers = new List<int>();
      ServerStatusInformation SSI = new ServerStatusInformation();
      readonly ConcurrentDictionary<int, (ushort Version, ushort Minimum, MozProtocolCapabilities Capabilities)> PeerProtocols = new();
      readonly bool HttpProtocolCompatible;
      public ushort HttpClientProtocolVersion { get; }
      public ushort HttpClientMinimumProtocolVersion { get; }
      public MozProtocolCapabilities HttpClientCapabilities { get; }
      MTProtoProxyServer? _mtProtoServer;
      long _lastTransportBytesSent;
      long _idleTransportBytesSent;
      int _idleTransportGuardRunning;
      const long MinSuspiciousIdleBytesPerSample = 64L * 1024;
      const long MaxIdleTransportBytesPerSample = 2L * 1024 * 1024;
      const long MaxIdleTransportBytesTotal = 4L * 1024 * 1024;
      public LNClient(HttpContext context)
      {
         _context = context;
         ServerStatsUpdateTimer.Elapsed += ServerStatsUpdateTimer_Elapsed;
         IdleTransportGuardTimer.Elapsed += IdleTransportGuardTimer_Elapsed;

         HttpProtocolCompatible = ReadHttpProtocolHeaders(context.Request.Headers,
            out ushort version, out ushort minimum, out MozProtocolCapabilities capabilities);
         HttpClientProtocolVersion = version;
         HttpClientMinimumProtocolVersion = minimum;
         HttpClientCapabilities = capabilities;
         context.Response.Headers[MozProtocol.VersionHeader] = MozProtocol.CurrentVersion.ToString();
         context.Response.Headers[MozProtocol.MinimumVersionHeader] = MozProtocol.MinimumVersion.ToString();
         context.Response.Headers[MozProtocol.CapabilitiesHeader] = ((uint)MozProtocol.ServerCapabilities).ToString();
      }

      private void IdleTransportGuardTimer_Elapsed(object? sender, ElapsedEventArgs e)
      {
         if (Interlocked.Exchange(ref _idleTransportGuardRunning, 1) != 0)
            return;

         try
         {
            NetManager? server = _Server;
            if (server == null || !server.IsRunning)
               return;

            long bytesSent = server.Statistics.BytesSent;
            long delta = Math.Max(0, bytesSent - Interlocked.Exchange(ref _lastTransportBytesSent, bytesSent));
            if (connections.Count != 0 || delta < MinSuspiciousIdleBytesPerSample)
            {
               // Do not accumulate normal transport keep-alives forever. Only consecutive
               // samples containing meaningful idle traffic contribute to the guard.
               Interlocked.Exchange(ref _idleTransportBytesSent, 0);
               return;
            }

            long idleTotal = Interlocked.Add(ref _idleTransportBytesSent, delta);
            if (delta < MaxIdleTransportBytesPerSample && idleTotal < MaxIdleTransportBytesTotal)
               return;

            foreach (NetPeer peer in server.ConnectedPeerList.ToArray())
            {
               int orderedQueued = 0;
               int unorderedQueued = 0;
               for (byte channel = 0; channel < MaxChannels; channel++)
               {
                  orderedQueued += peer.GetPacketsCountInReliableQueue(channel, true);
                  unorderedQueued += peer.GetPacketsCountInReliableQueue(channel, false);
               }

               Logger.Log($"Disconnecting idle peer {peer.Id}: transport sent {delta} bytes in 5 seconds " +
                          $"with zero proxy connections ({orderedQueued} ordered and {unorderedQueued} unordered queued packets).");
               peer.Disconnect();
            }
            Interlocked.Exchange(ref _idleTransportBytesSent, 0);
         }
         finally
         {
            Volatile.Write(ref _idleTransportGuardRunning, 0);
         }
      }

      private static bool ReadHttpProtocolHeaders(IHeaderDictionary headers, out ushort version, out ushort minimum,
         out MozProtocolCapabilities capabilities)
      {
         version = MozProtocol.LegacyVersion;
         minimum = MozProtocol.LegacyVersion;
         capabilities = MozProtocolCapabilities.None;
         if (!headers.ContainsKey(MozProtocol.VersionHeader))
            return true; // No advertisement means a legacy v1 client.

         if (!ushort.TryParse(headers[MozProtocol.VersionHeader], out version) ||
             !ushort.TryParse(headers[MozProtocol.MinimumVersionHeader], out minimum))
            return false;

         if (uint.TryParse(headers[MozProtocol.CapabilitiesHeader], out uint capabilityMask))
            capabilities = (MozProtocolCapabilities)capabilityMask;

         bool compatible = MozProtocol.IsCompatible(version, minimum);
         Logger.Log($"HTTP client advertised Moz protocol v{version} (minimum v{minimum}); " +
                    $"capabilities={capabilities}; compatible={compatible}.");
         return compatible;
      }

      private async Task<DisconnectionReason?> AttemptHolePunchingAsync(HolePunchPeerInfo PeerInfo)
      {
         byte[] PunchPacket = new byte[2] { 255, 255 };
         IPEndPoint ClientEndpoint = new IPEndPoint(PeerInfo.ipAddress, PeerInfo.Port);
         if (PeerInfo.ipAddress.ToString() == "0.0.0.0")
         {
            if (_context.Connection.RemoteIpAddress is not null)
               PeerInfo.ipAddress = _context.Connection.RemoteIpAddress;
            if (_context.Connection.RemoteIpAddress is null)
            {
               throw new NullReferenceException("Remote IP Address was null.");
            }
         }
         if (_StunResult.NATType == STUNNATType.Symmetric && PeerInfo.NatType == STUNNATType.Symmetric)
         {
            return DisconnectionReason.ServerException;
         }
         if (PeerInfo.NatType == STUNNATType.Symmetric)
         {
            IPEndPoint TempEP;
            for (int j = 0; j < 100; j++)
            {
               for (int i = PeerInfo.Port; i < PeerInfo.PortsCount + PeerInfo.Port; i++)
               {
                  TempEP = new IPEndPoint(PeerInfo.ipAddress, i);
                  _Server.SendUnconnectedMessage(PunchPacket, TempEP);
               }
               if (_Peers.Count == 0)
               {
                  await Task.Delay(100);
               }
               else
               {
                  break;
               }
            }
         }
         else
         {
            for (int i = 0; i < 100; i++)
            {
               for (int j = 0; j < 5; j++)
               {
                  _Server.SendUnconnectedMessage(PunchPacket, ClientEndpoint);
               }
               if (_Peers.Count == 0)
               {
                  await Task.Delay(100);
               }
               else
               {
                  break;
               }
            }
         }
         return null;
      }
      private void ServerStatsUpdateTimer_Elapsed(object? sender, ElapsedEventArgs? e)
      {
         if (ReferenceEquals(_Server, null))
         {
            ServerStatsUpdateTimer.Stop();
            return;
         }
         GCMemoryInfo inf = GC.GetGCMemoryInfo();
         SSI.LastTcpSocketCreationLatency = (int)StaticData.LastTcpClientMakeDelayTicks;
         SSI.AverageTcpSocketCreationLatency = (int)StaticData.GetAvgTcpClientCreationDelayTicks();
         SSI.TotalTcpSocketCreations = (int)StaticData.TotalLocalTcpClientsMade;
         SSI.ActiveTcpSockets = (int)StaticData.TotalActiveConnections;
         SSI.TotalLNServers = ClientManager.ReliableClients.Count();
         SSI.TotalThreads = Process.GetCurrentProcess().Threads.Count;
         SSI.GCTotalMemory = GC.GetTotalMemory(false);
         SSI.GCMemoryLoadBytes = inf.MemoryLoadBytes;
         SSI.GCHeapSizeBytes = inf.HeapSizeBytes;
         SSI.GCTotalCommittedBytes = inf.TotalCommittedBytes;
         SSI.TotalUpstreamBytes = -999;
         SSI.TotalDownstreamBytes = -999;
         SSI.Uptime = DateTime.UtcNow.Ticks - StaticData.ServerStartDateTime.Ticks;
         SSI.TotalKeepAliveHttpConnections = StaticData.HttpKeepAliveConnectionsCount;
         foreach (int item in ServerStatsUpdatePeers)
         {
            NetPeer? Peer = _Server.GetPeerById(item);
            if (Peer != null)
            {
               SSI.CurrentClientChannelsCount = Peer.NetManager.ChannelsCount;
               SSI.CurrentClientConnectionsCount = (ushort)connections.Count;
               SSI.CurrentClientTotalUpstream = Peer.NetManager.Statistics.BytesSent;
               SSI.CurrentClientTotalDownstream = Peer.NetManager.Statistics.BytesReceived;
               SSI.CurrentClientPacketLossPercent = Peer.NetManager.Statistics.PacketLossPercent;
               if (PeerLatencies.ContainsKey(Peer.Id))
               {
                  SSI.CurrentClientLatencyMiliseconds = PeerLatencies[Peer.Id];
               }
               else
               {
                  SSI.CurrentClientLatencyMiliseconds = -999;
               }
               byte[] Packet = ServerCommandUtils.BuildServerStatusInformation(SSI, 6);
               byte[] ServerCommandBytes = BitConverter.GetBytes((ushort)ServerCommands.ServerStatusUpdate);
               Array.Copy(ServerCommandBytes, 0, Packet, 4, ServerCommandBytes.Length);
               Peer.Send(Packet, DeliveryMethod.ReliableUnordered);
            }
         }
      }
      public async Task AttemptReconnectAsync(HttpContext context)
      {
         if (context.Request.Headers.Keys.Contains("newcontext"))//Discard Old Context
         {
            if (context.Request.Headers["newcontext"].ToString().ToLower() == "true")
            {
               _context?.Abort();
               _context = context;
               await StartAsync();
            }
            else
            {
               context.Response.StatusCode = (int)HttpStatusCode.NotImplemented;
               return;
            }
         }
         else
         {
            context.Response.StatusCode = (int)HttpStatusCode.NotImplemented;
            return;
         }
      }
      public async Task<DisconnectionReason> StartAsync(bool Reconnect = false)
      {
         int padding = 0;
         if (_context == null)
         {
            return DisconnectionReason.ServerException;
         }
         if (!HttpProtocolCompatible)
         {
            _context.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
            await _context.Response.WriteAsync("Incompatible Moz protocol version.");
            return DisconnectionReason.ServerException;
         }
         if (_context.Request.Headers.ContainsKey("isConnected"))
         {
            if (_context.Request.Headers["isConnected"] == "True")
            {
               await KeepAliveAsync();
               return DisconnectionReason.UserDisconnected;
            }
         }
         if (_context.Request.Headers.ContainsKey("PD"))
         {
            if (int.TryParse(_context.Request.Headers["PD"], out int PD))
            {
               padding = PD;
            }
            else
            {
               Logger.Log($"Invalid value in padding header {_context.Request.Headers["PD"]}");
            }
         }
         try
         {
            Random RND = new Random();
            if (Reconnect)
            {
               byte[] PBuffer = new byte[1024];
               int PRead = await _context.Request.Body.ReadAsync(PBuffer);
               HolePunchPeerInfo ReconPeerInfo = MozStatic.DeserializePunchInfo(PBuffer[0..PRead], padding);

               var PRes = await AttemptHolePunchingAsync(ReconPeerInfo);
               if (PRes != null)
                  return (DisconnectionReason)PRes;

               using (MemoryStream Mem = new MemoryStream())
               {
                  if (padding != 0)
                  {
                     byte[] PaddingBytes = new byte[padding];
                     RND.NextBytes(PaddingBytes);
                     await Mem.WriteAsync(PaddingBytes);
                  }
                  byte[] ConnKeyBytes2 = new byte[16];
                  if (LNConnectionKey == null)
                  {
                     Logger.Log("We're fucked again. why indeed...");
                     RND.NextBytes(ConnKeyBytes2);
                     LNConnectionKey = Convert.ToBase64String(ConnKeyBytes2);
                  }
                  byte[] ConnKeyBytes = Convert.FromBase64String(LNConnectionKey);
                  Mem.WriteByte((byte)((int)ServerCommands.BeginUdpClient));
                  byte[] ConInfo = MozStatic.SerializeUdpConnectionInfo(TransportMode.LiteNet, _StunResult.PublicEndPoint.Address,
                  _StunResult.PublicEndPoint.Port, ConnKeyBytes);
                  await Mem.WriteAsync(ConInfo);
                  await Mem.FlushAsync();
                  await _context.Response.Body.WriteAsync(Mem.ToArray());
               }
            }
            else
            {
               byte[] Buffer = new byte[1024];
               Logger.Log("Reading request body...");
               int Read = await _context.Request.Body.ReadAsync(Buffer);
               HolePunchPeerInfo PeerInfo = MozStatic.DeserializePunchInfo(Buffer[0..Read], padding);
               UdpClient stunClient = new UdpClient();
               if (StaticServers.LocalServerMode)
               {
                  await stunClient.SendAsync(new byte[] { 0 }, 1, new IPEndPoint(IPAddress.Loopback, 65535));
                  _StunResult = new STUNQueryResult() { LocalEndPoint = new IPEndPoint(IPAddress.Parse("127.0.0.1"), (stunClient.Client.LocalEndPoint as IPEndPoint).Port), PublicEndPoint = new IPEndPoint(IPAddress.Parse("127.0.0.1"), (stunClient.Client.LocalEndPoint as IPEndPoint).Port) };

               }
               else if (StaticData.CurrentServerAddress is not null)
               {
                  await stunClient.SendAsync(new byte[] { 0 }, 1, new IPEndPoint(IPAddress.Loopback, 65535));
                  _StunResult = new()
                  {
                     LocalEndPoint = new IPEndPoint(IPAddress.Parse("0.0.0.0"),
                       (stunClient.Client.LocalEndPoint as IPEndPoint).Port),
                     PublicEndPoint = new IPEndPoint(IPAddress.Parse(StaticData.CurrentServerAddress),
                       (stunClient.Client.LocalEndPoint as IPEndPoint).Port),
                     NATType = STUNNATType.PortRestricted
                  };
               }
               else
               {
                  Logger.Log("Doing alternative stun result manipulation method.");
                  if (string.IsNullOrWhiteSpace(StaticData.CurrentServerAddress))
                  {
                     StaticData.CurrentServerAddress = await StunHelpers.GetPublicIPAsync();
                  }
                  await stunClient.SendAsync(new byte[] { 0 }, 1, new IPEndPoint(IPAddress.Loopback, 65535));
                  _StunResult = new()
                  {
                     LocalEndPoint = new IPEndPoint(IPAddress.Parse("0.0.0.0"),
                       (stunClient.Client.LocalEndPoint as IPEndPoint).Port),
                     PublicEndPoint = new IPEndPoint(IPAddress.Parse(StaticData.CurrentServerAddress),
                       (stunClient.Client.LocalEndPoint as IPEndPoint).Port),
                     NATType = STUNNATType.PortRestricted
                  };
                  Logger.Log($"Alternative stun result: {_StunResult.PublicEndPoint}");

                  //_StunResult = await StunHelpers.ForceStunAsync(stunClient.Client);
               }
               //if (StaticServers.LocalServerMode)
               //   _StunResult.PublicEndPoint = new IPEndPoint(IPAddress.Parse("127.0.0.1"), _StunResult.LocalEndPoint.Port);


               byte[] ConnKeyBytes = new byte[16];
               RND.NextBytes(ConnKeyBytes);
               LNConnectionKey = Convert.ToBase64String(ConnKeyBytes);
               //if (PeerInfo.NatType == STUNNATType.Symmetric)
               //{
               //   throw new NotImplementedException();
               //}
               using (MemoryStream Mem = new MemoryStream())
               {
                  if (padding != 0)
                  {
                     byte[] PaddingBytes = new byte[padding];
                     RND.NextBytes(PaddingBytes);
                     await Mem.WriteAsync(PaddingBytes);
                  }
                  Mem.WriteByte((byte)((int)ServerCommands.BeginUdpClient));
                  byte[] ConInfo = MozStatic.SerializeUdpConnectionInfo(TransportMode.LiteNet, _StunResult.PublicEndPoint.Address,
                  _StunResult.PublicEndPoint.Port, ConnKeyBytes);
                  await Mem.WriteAsync(ConInfo);
                  await Mem.FlushAsync();
                  await _context.Response.Body.WriteAsync(Mem.ToArray());
               }
               await _context.Response.Body.FlushAsync();
               int ListenPort = _StunResult.LocalEndPoint.Port;
               stunClient.Dispose();
               _Server = new NetManager(this)
               {
                  ChannelsCount = MaxChannels,
                  AutoRecycle = true,
                  UpdateTime = 1,
                  SimulatePacketLoss = false,
                  SimulateLatency = false,
                  EnableStatistics = true,
                  UnsyncedEvents = true,
                  DisconnectTimeout = 20000,
                  PingInterval = 5000,
               };
               _Server.Start(ListenPort);
               _lastTransportBytesSent = _Server.Statistics.BytesSent;
               IdleTransportGuardTimer.Start();

               ClientManager.AddReliableClient(_Server.LocalPort, this);
               SrvLocalPort = _Server.LocalPort;
               Logger.Log("Beginning hole punching");
               var res = await AttemptHolePunchingAsync(PeerInfo);
               if (res != null)
                  return (DisconnectionReason)res;
            }
            int LooperI = 0;
            while (_Peers.Count == 0 || LooperI < 40)
            {
               LooperI++;
               await Task.Delay(50);
            }
            Logger.Log($"Peers = {_Peers.Count}");
            if (_Peers.Count != 0)
            {
               int ConnectedPeerID = 0;
               for (int i = 0; i < _Peers.Count(); i++)
               {
                  if (_Peers[i].ConnectionState == ConnectionState.Connected)
                  {
                     ConnectedPeerID = i; break;
                  }
               }
               if (_Peers[ConnectedPeerID].ConnectionState == ConnectionState.Connected)
               {
                  Logger.Log($"PeerStatus: {_Peers[ConnectedPeerID].ConnectionState.ToString()}, Keeping alive...");
                  if (_context.Request.Headers.ContainsKey("KeepAlive"))
                  {
                     if (_context.Request.Headers["KeepAlive"] == "true")
                     {
                        await KeepAliveAsync();

                        Logger.Log($"PeerStatus: {_Peers[ConnectedPeerID].ConnectionState.ToString()}, KeepAlive loop broke.");
                     }
                     else
                     {
                        //await _context.Response.Body.WriteAsync(new byte[4096].ToArray());
                     }
                  }
                  else
                  {
                     //await _context.Response.Body.WriteAsync(new byte[4096].ToArray());
                  }

                  Logger.Log($"PeerStatus: {_Peers[ConnectedPeerID].ConnectionState.ToString()}, KeepAlive was disabled.");
               }
            }
            else
            {
               _ = Task.Run(async () =>
               {
                  while (IdlingTImer >= 0)
                  {
                     IdlingTImer++;
                     if (IdlingTImer > 30)
                     {
                        this.Dispose();
                        IdlingTImer = -60;
                        break;
                     }
                     await Task.Delay(1000);
                  }
               });
            }

            return DisconnectionReason.UserDisconnected;
         }
         catch (Exception ex)
         {
            this.Dispose();
            return DisconnectionReason.ServerException;
         }
      }

      public void Dispose()
      {
         if (Interlocked.Exchange(ref _disposed, 1) == 0)
         {
            try
            {
               _Server?.Stop();
               IdleTransportGuardTimer.Stop();
               ServerStatsUpdateTimer.Stop();
               _context?.Connection.RequestClose();
               foreach (var item in connections.Values)
               {
                  item?.Close();
               }
               _mtProtoServer?.Dispose();
            }
            catch (Exception)
            {

            }
            finally
            {
               ClientManager.RemoveReliableClient(SrvLocalPort);
               IdleTransportGuardTimer.Dispose();
               ServerStatsUpdateTimer.Dispose();
            }
         }
      }

      public void OnConnectionRequest(ConnectionRequest request)
      {
         Logger.Log("Connection request received.");
         _Peers.Add(request.AcceptIfKey(LNConnectionKey));
      }

      public void OnNetworkError(IPEndPoint endPoint, SocketError socketError)
      {
         Logger.Log(socketError.ToString());
      }

      public void OnNetworkLatencyUpdate(NetPeer peer, int latency)
      {
         if (PeerLatencies.ContainsKey(peer.Id))
         {
            PeerLatencies[peer.Id] = latency * 2;
         }
         else
         {
            PeerLatencies.Add(peer.Id, latency * 2);
         }
         if ((DateTime.UtcNow - StaticData.ServerStartDateTime).Minutes % 5 == 0)
         {
            if (StaticData.HttpKeepAliveConnectionsCount == 0)
            {
               try
               {
                  byte[] SendBuffer = new byte[6];
                  byte[] CommandBytes = BitConverter.GetBytes((ushort)ServerCommands.KeepAlive);
                  Array.Copy(CommandBytes, 0, SendBuffer, 4, CommandBytes.Length);
                  peer.Send(SendBuffer, DeliveryMethod.ReliableUnordered);
               }
               catch (Exception ex)
               {
                  Logger.LogException(ex);
                  throw;
               }
            }
         }
         //throw new NotImplementedException();
      }
      //ArraySegment<byte> ReceiveBuffer = new ArraySegment<byte>();
      public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod)
      {
         if (deliveryMethod == DeliveryMethod.Unreliable)
         {
            //True udp, use those channels now.


            return;
         }
         //System.Threading.Thread.Sleep(10);
         //Process packets here, assign channels, etc...
         //ReceiveBuffer = reader.RawData;
         byte[] RecData = reader.RawData[reader.UserDataOffset..(reader.UserDataSize + reader.UserDataOffset)];
         if (RecData.Length < 2)
         {
            Logger.Log("Ignored malformed LiteNet packet shorter than a connection id.");
            return;
         }
         //Console.WriteLine($"Server received {RecData.Length - 2} ({RecData.Length}) on chan {channelNumber}");
         ushort ConnectionID = BitConverter.ToUInt16(RecData, 0);
         try
         {
            //if (ConnectionID == ushort.MaxValue)
            //{
            //   ConnectionID = BitConverter.ToUInt16(RecData, 2);
            //   long StopwatchTicks = Stopwatch.GetTimestamp();
            //   TcpClient Cli = new TcpClient("127.0.0.1", StaticServers.MTServerPort);
            //   long StopwatchAfterTicks = Stopwatch.GetTimestamp();
            //   StaticData.AddTcpClientCreationDelayTick(StopwatchAfterTicks - StopwatchTicks);
            //   //ReliableConnection Con = new ReliableConnection(ConnectionID, Cli, ClientEP, RufConm, KeyHol);
            //   LNConnection Con = new LNConnection(ConnectionID, channelNumber, Cli, peer.Id, ref _Server, 100);
            //   //Logger.Log($"Peer ID: {PeerID}, Connection ID: {ConnectionID}, ChannelID {ChannelID}");
            //   //Con.PeerID = PeerID;
            //   Con.BoundChannelID = channelNumber;
            //   Con.ConnectionClosed += Con_ConnectionClosed;
            //   Con.DataAvailable += Con_DataAvailable;
            //   connections.Add(ConnectionID, Con);
            //   Interlocked.Increment(ref StaticData._TotalActiveConnections);
            //   _ = Con.StartConnectionAsync();
            //   _ = Con.SendDataToServerAsync(Data);
            //   return;
            //}
            if (ConnectionID == 0)
            {
               if (RecData.Length < 4)
                  return;
               ushort ConIDToRemove = BitConverter.ToUInt16(RecData, 2);
               if (ConIDToRemove == 0)//Custom client commands over udp
               {
                  if (RecData.Length < 6)
                     return;
                  ushort Command = BitConverter.ToUInt16(RecData, 4);
                  ClientCommands CommandType = (ClientCommands)((int)Command);
                  switch (CommandType)
                  {
                     case ClientCommands.RequestServerStats:
                        int interval = ClientCommandUtils.ReadServerStatsRequestCommand(RecData, 6);
                        if (interval > 0)
                        {
                           ServerStatsUpdatePeers.Add(peer.Id);
                           ServerStatsUpdateTimer.Interval = interval;
                           ServerStatsUpdateTimer_Elapsed(this, null);
                           ServerStatsUpdateTimer.Start();
                        }
                        else
                        {
                           ServerStatsUpdatePeers.Remove(peer.Id);
                           ServerStatsUpdateTimer.Stop();
                        }
                        break;
                     case ClientCommands.OpenEndToEndCustomPipe:

                        break;
                     case ClientCommands.NewMtProtoPipe:
                        if (RecData.Length < 8)
                        {
                           SendMtProtoResult(peer, false);
                           break;
                        }

                        try
                        {
                           _mtProtoServer ??= StaticServers.StartMtProtoServer();
                        }
                        catch (Exception ex)
                        {
                           Logger.Log($"Unable to start MTProto server: {ex.Message}");
                           SendMtProtoResult(peer, false);
                           break;
                        }

                        ConnectionID = BitConverter.ToUInt16(RecData, 6);
                        if (connections.ContainsKey(ConnectionID))
                        {
                           SendMtProtoResult(peer, false);
                           break;
                        }

                        long StopwatchTicks = Stopwatch.GetTimestamp();
                        TcpClient Cli = new("127.0.0.1", _mtProtoServer.Port);
                        long StopwatchAfterTicks = Stopwatch.GetTimestamp();
                        StaticData.AddTcpClientCreationDelayTick(StopwatchAfterTicks - StopwatchTicks);
                        LNConnection Con = new(ConnectionID, channelNumber, Cli, peer.Id, ref _Server, 100);
                        Con.ConnectionClosed += Con_ConnectionClosed;
                        Con.DataAvailable += Con_DataAvailable;
                        connections.Add(ConnectionID, Con);
                        Interlocked.Increment(ref StaticData._TotalActiveConnections);
                        _ = Con.StartConnectionAsync();
                        SendMtProtoResult(peer, true);
                        break;
                     case ClientCommands.CustomUdpRelay:
                        _ = CreateNewUdpRelayAsync(RecData, peer.Id);

                        break;
                     case ClientCommands.ProtocolHello:
                        if (!MozProtocol.TryReadHello(RecData, 6, out ushort version, out ushort minimum,
                            out MozProtocolCapabilities capabilities))
                        {
                           Logger.Log("Ignored malformed Moz protocol hello from client.");
                           break;
                        }
                        if (!MozProtocol.IsCompatible(version, minimum))
                        {
                           Logger.Log($"Rejected incompatible client protocol range v{minimum}-v{version}.");
                           peer.Disconnect();
                           break;
                        }

                        PeerProtocols[peer.Id] = (version, minimum, capabilities);
                        peer.Send(ServerCommandUtils.BuildProtocolHelloCommand(MozProtocol.ServerCapabilities),
                           DeliveryMethod.ReliableUnordered);
                        Logger.Log($"Negotiated Moz protocol v{version} with client {peer.Id}; capabilities: {capabilities}.");
                        break;
                     default:
                        break;
                  }
                  return;
               }
               else if (RecData.Length == 4)
               {
                  if (connections.ContainsKey(ConIDToRemove))
                  {
                     connections[ConIDToRemove].Close();
                  }
                  if (PeerSupports(peer.Id, MozProtocolCapabilities.StreamCloseAcknowledgement))
                     peer.Send(RecData, DeliveryMethod.ReliableUnordered);
                  return;
               }

            }
            else
            {
               _ = HandlePacketAsync(ConnectionID, channelNumber, RecData[2..], peer.Id);
            }
         }
         catch (Exception ex)
         {
            Logger.LogException(ex);
         }

         //throw new NotImplementedException();
      }

      private static void SendMtProtoResult(NetPeer peer, bool succeeded)
      {
         byte[] SendBuffer = new byte[7];
         byte[] OutCmd = BitConverter.GetBytes((ushort)ServerCommands.EndToEndPipeCreationResult);
         Array.Copy(OutCmd, 0, SendBuffer, 4, OutCmd.Length);
         SendBuffer[6] = succeeded ? (byte)255 : (byte)0;
         peer.Send(SendBuffer, DeliveryMethod.ReliableUnordered);
      }

      private bool PeerSupports(int peerId, MozProtocolCapabilities capability)
      {
         return PeerProtocols.TryGetValue(peerId, out var protocol) &&
                (protocol.Capabilities & capability) == capability;
      }

      private volatile Dictionary<byte, SubTunInfo> UdpRelaysDictionary = new Dictionary<byte, SubTunInfo>();//key is ID.

      private async Task CreateNewUdpRelayAsync(byte[] CommandBytes, int PeerID, int CommandOffset = 6)
      {
         SubTunInfo TunInformation = ClientCommandUtils.ReadRelayRequestCommand(CommandBytes, CommandOffset);
         TunInformation.PeerID = PeerID;
         bool res = UdpRelaysDictionary.TryAdd(TunInformation.ID, TunInformation);
         byte[] SendPacket = new byte[CommandOffset + 2];
         if (res)
         {
            //from 4 to 5 is command
            BitConverter.GetBytes((ushort)ServerCommands.UdpRelayResult).CopyTo(SendPacket, 4);
            //byte 6 is relay ID
            SendPacket[6] = TunInformation.ID;
            //Byte 7 is result. 255 for success.
            SendPacket[7] = 255;
            _Server.GetPeerById(PeerID).Send(SendPacket, DeliveryMethod.ReliableUnordered);
         }
         else
         {
            //Byte 7 0 for failure
            SendPacket[7] = 0;
         }
      }
      private async Task HandlePacketAsync(ushort ConnectionID, byte ChannelID, ArraySegment<byte> Data, int PeerID)
      {
         try
         {
            if (connections.ContainsKey(ConnectionID))
            {
               await connections[ConnectionID].SendDataToServerAsync(Data);
            }
            else
            {
               //IPEndPoint DestinationIPEndPoint = new IPEndPoint(IPAddress.Loopback)
               long StopwatchTicks = Stopwatch.GetTimestamp();
               //if (ConnectionID == ushort.MaxValue)
               //{
               //   DestinationPort = StaticServers.MTServerPort;
               //}
               TcpClient Cli = new TcpClient(StaticServers.DestServerIP, StaticServers.DestServerPort);
               long StopwatchAfterTicks = Stopwatch.GetTimestamp();
               StaticData.AddTcpClientCreationDelayTick(StopwatchAfterTicks - StopwatchTicks);
               //ReliableConnection Con = new ReliableConnection(ConnectionID, Cli, ClientEP, RufConm, KeyHol);
               LNConnection Con = new LNConnection(ConnectionID, ChannelID, Cli, PeerID, ref _Server, 100);
               //Logger.Log($"Peer ID: {PeerID}, Connection ID: {ConnectionID}, ChannelID {ChannelID}");
               //Con.PeerID = PeerID;
               Con.BoundChannelID = ChannelID;
               Con.ConnectionClosed += Con_ConnectionClosed;
               Con.DataAvailable += Con_DataAvailable;
               connections.Add(ConnectionID, Con);
               Interlocked.Increment(ref StaticData._TotalActiveConnections);
               _ = Con.StartConnectionAsync();
               await Con.SendDataToServerAsync(Data);
            }
         }
         catch (ObjectDisposedException)
         {
            Logger.Log($"ObjectDisposedException {ConnectionID}");
            connections[ConnectionID].Close();
         }
      }

      private void Con_DataAvailable(object? sender, MozPacket e)
      {
         NetPeer? peer = _Server?.GetPeerById(e.PeerID);
         if (peer == null)
            return;

         bool isClose = e.Length == 4 && BitConverter.ToUInt16(e.RawData, e.StartIndex) == 0;
         if (isClose)
         {
            DeliveryMethod closeDelivery = PeerSupports(e.PeerID,
               MozProtocolCapabilities.ReliableUnorderedStreamClose)
               ? DeliveryMethod.ReliableUnordered
               : DeliveryMethod.ReliableOrdered;
            peer.Send(e.RawData, e.StartIndex, e.Length, e.ChannelID, closeDelivery);
            return;
         }

         while (peer.GetPacketsCountInReliableQueue(e.ChannelID, true) > 100 && peer.ConnectionState == ConnectionState.Connected)
            System.Threading.Thread.Sleep(2);
         if (peer.ConnectionState == ConnectionState.Connected)
            peer.Send(e.RawData, e.StartIndex, e.Length, e.ChannelID, DeliveryMethod.ReliableOrdered);
      }

      private void Con_ConnectionClosed(object? sender, ushort e)
      {
         try
         {
            if (connections.ContainsKey(e))
            {
               connections[e] = null;
               if (connections.Remove(e))
               {
                  Interlocked.Decrement(ref StaticData._TotalActiveConnections);
               }
               else
               {
                  Logger.Log("ConnectionClosed event was called more than once on a single connection.");
               }
            }
         }
         catch (Exception ex)
         {
            Logger.Log(ex.Message + Environment.NewLine + ex.StackTrace);
         }
      }

      public void OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType)
      {

         //throw new NotImplementedException();
         //Packet is true udp, for shadowsocks, etc.
      }

      public void OnPeerConnected(NetPeer peer)
      {
         //if (peer == _Peer)
         //{
         IdlingTImer = -60;
         Logger.Log("LN Peer connected.");
         //}
      }
      public void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
      {
         _Peers.Remove(peer);
         PeerLatencies.Remove(peer.Id);
         PeerProtocols.TryRemove(peer.Id, out _);
         _ = Task.Run(async () =>
         {
            byte[] PunchPacket = new byte[2] { 255, 255 };
            IPEndPoint PeerEndpoint = peer.EndPoint;
            for (int i = 0; i < 20 * 20; i++)//20 20-second Recon attempts?
            {
               _Server.SendUnconnectedMessage(PunchPacket, PeerEndpoint);
               await Task.Delay(1000);
               if (_Server.ConnectedPeerList.Where(x => x.EndPoint.Equals(PeerEndpoint) && x.ConnectionState == ConnectionState.Connected).Count() > 0)
               {
                  break;
               }
            }
         });
         if (_Peers.Count == 0)
         {
            IdlingTImer = 0;
            if (disconnectInfo.Reason == DisconnectReason.DisconnectPeerCalled)
            {
               this.Dispose();
            }
            else
            {
               _ = Task.Run(async () =>
               {
                  while (IdlingTImer >= 0)
                  {
                     IdlingTImer++;
                     if (IdlingTImer > 45)
                     {
                        this.Dispose();
                        break;
                     }
                     await Task.Delay(1000);
                  }
               });
            }
            Logger.Log($"LN Peer disconnected: {disconnectInfo.Reason.ToString()}");
         }
      }

      public async Task<DisconnectionReason> KeepAliveAsync()
      {
         if (_context == null)
         {
            return DisconnectionReason.ServerException;
         }
         bool Aborted = false;
         _context.RequestAborted.Register(() =>
         {
            Aborted = true;
         });
         while (!Aborted)
         {
            try
            {
               await BodyWriter.WriteStringAsync(_context.Response.Body, "KAP");
               System.Threading.Thread.Sleep(30000);
            }
            catch (Exception)
            {
               return DisconnectionReason.ServerException;
            }
         }
         return DisconnectionReason.UserDisconnected;
      }
   }
}
