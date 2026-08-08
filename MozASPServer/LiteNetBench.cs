using LiteNetLib;
using MozUtil;
using MozUtil.NatUtils;
using MozUtil.Types;
using STUN;
using System.Net;
using System.Net.Sockets;

namespace DirtySocksASP
{
   public class LiteNetBench : INetEventListener, IDisposable
   {
      NetManager? _server;
      HttpContext? _Context;
      byte ChanCount;
      CancellationTokenSource? CTS;
      public LiteNetBench(HttpContext context)
      {
         _Context = context;
      }

      public async Task<DisconnectionReason> BeginBenchAsync()
      {
         if (_Context == null)
         {
            return DisconnectionReason.ServerException;
         }
         CTS = new CancellationTokenSource();
         byte ChannelCount = ((byte)int.Parse(_Context.Request.Headers["Channels"].ToString()));
         ChanCount = ChannelCount;
         int TimeLen = int.Parse(_Context.Request.Headers["TimeSec"].ToString());
         byte[] Buffer = new byte[1024];
         int Read = await _Context.Request.Body.ReadAsync(Buffer);
         HolePunchPeerInfo PeerInfo = MozStatic.DeserializePunchInfo(Buffer[0..Read], 0);
         UdpClient udpSrv = new UdpClient();
         STUNQueryResult ServerStunResult = StunHelpers.GetStunResult(udpSrv.Client);
         for (int i = 0; i < 10; i++)
         {
            if (ServerStunResult.QueryError != STUNQueryError.Success)
            {
               ServerStunResult = StunHelpers.GetStunResult(udpSrv.Client);
               await Task.Delay(1000 * i);
            }
            else
               break;
         }
         if (StaticServers.LocalServerMode)
            ServerStunResult.PublicEndPoint = new IPEndPoint(IPAddress.Parse("127.0.0.1"), ServerStunResult.LocalEndPoint.Port);
         IPEndPoint ClientEndpoint = new IPEndPoint(PeerInfo.ipAddress, PeerInfo.Port);

         if (ServerStunResult.QueryError == STUNQueryError.Success)
         {
            //using (MemoryStream Mem = new MemoryStream())
            //{
            //   Mem.WriteByte((byte)((int)ServerCommand.BeginHolePunching));
            //   await Mem.WriteAsync(MozStatic.SerializePunchInfo(ServerStunResult, 10000));
            //   await Mem.FlushAsync();
            //   await _Context.Response.Body.WriteAsync(Mem.ToArray());
            //}
            //await _Context.Response.Body.FlushAsync();
         }
         else
         {
            return DisconnectionReason.ServerException;
         }
         //udpPuncher Puncher = new udpPuncher();
         //bool PunchResult = false;
         //int[]? PunchedPortsCollection;
         ////PunchResult = await Puncher.PR2PRPunch(udpSrv, ClientEndpoint);
         //if (((int)PeerInfo.NatType) >= 1 && ((int)PeerInfo.NatType) <= 4)
         //{
         //   PunchResult = await Puncher.PR2PRPunch(udpSrv, ClientEndpoint);
         //}
         //else if (PeerInfo.NatType == STUNNATType.Symmetric)
         //{
         //   int[] PunchedPorts = await Puncher.PR2SymmetricPunch(udpSrv, PeerInfo.Port, PeerInfo.Port + PeerInfo.PortsCount, PeerInfo.ipAddress.ToString());
         //   //int[] PunchedPorts = await Puncher.PRNatPunchToSymmetricAsync(UdpSrv, ClientPeerInfo.Port, ClientPeerInfo.Port + ClientPeerInfo.PortsCount, ClientPeerInfo.ipAddress.ToString());
         //   if (PunchedPorts.Length > 0)
         //   {
         //      PunchResult = true;
         //      PunchedPortsCollection = PunchedPorts;
         //   }
         //   else
         //   {
         //      PunchResult = false;
         //      PunchedPortsCollection = null;
         //   }
         //}
         //else if (PeerInfo.NatType == STUNNATType.Unspecified)
         //{
         //   return DisconnectionReason.ServerException;
         //}
         //if (!PunchResult)
         //{
         //   using (MemoryStream Mem = new MemoryStream())
         //   {
         //      Mem.WriteByte((byte)((int)ServerCommand.PunchResult));
         //      Mem.WriteByte(0xaa);//Failed
         //      await Mem.FlushAsync();
         //      await _Context.Response.Body.WriteAsync(Mem.ToArray());
         //   }
         //   await _Context.Response.Body.FlushAsync();
         //   //await BodyWriter.WriteString(Context.Response.Body, "PUNCH FAILED");
         //   return DisconnectionReason.ServerException;
         //}
         if (false)
         {

         }
         else
         {
            //using (MemoryStream Mem = new MemoryStream())
            //{
            //   Mem.WriteByte((byte)((int)ServerCommand.PunchResult));
            //   Mem.WriteByte(0xbb);//Success
            //   await Mem.FlushAsync();
            //   await _Context.Response.Body.WriteAsync(Mem.ToArray());
            //}
            //await _Context.Response.Body.FlushAsync();

            using (MemoryStream Mem = new MemoryStream())
            {
               Mem.WriteByte((byte)((int)ServerCommands.BeginUdpClient));
               byte[] ConInfo = MozStatic.SerializeUdpConnectionInfo(TransportMode.LiteNet, ServerStunResult.PublicEndPoint.Address,
               ServerStunResult.PublicEndPoint.Port);
               Mem.Write(ConInfo);
               await Mem.FlushAsync();
               await _Context.Response.Body.WriteAsync(Mem.ToArray());
            }
            await _Context.Response.Body.FlushAsync();
            if (TimeLen <= 30000)
               CTS.CancelAfter(TimeLen);
            else
               CTS.CancelAfter(30000);

            CTS.Token.Register(() =>
            {
               this.Dispose();
            });

            IPEndPoint? LocalEP = udpSrv.Client.LocalEndPoint as IPEndPoint;
            _server = new NetManager(this)
            {
               ChannelsCount = ChannelCount,
               AutoRecycle = true,
               UpdateTime = 1,
               SimulatePacketLoss = false,
               SimulateLatency = false,
               //EnableStatistics = true,
               UnsyncedEvents = true,
               DisconnectTimeout = 10000,
               PingInterval = 2000,
            };
            udpSrv.Dispose();
            _server.Start(LocalEP.Port);
            byte[] UnconMes = new byte[2] { 255, 255 };
            for (int i = 0; i < 5; i++)
            {
               _server.SendUnconnectedMessage(UnconMes, ClientEndpoint);
            }
         }


         while (_server.IsRunning)
         {
            await Task.Delay(1000);
         }
         Logger.Log("SpeedbenchDone.");
         return DisconnectionReason.ServerRequested;
      }
      NetPeer? _peer = null;
      public void Dispose()
      {
         CTS.Dispose();
         _server.Stop();
      }

      public void OnConnectionRequest(ConnectionRequest request)
      {
         Logger.Log("Connection req received.");
         request.Accept();
      }

      public void OnNetworkError(IPEndPoint endPoint, SocketError socketError)
      {
      }

      public void OnNetworkLatencyUpdate(NetPeer peer, int latency)
      {
      }

      public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod)
      {
      }

      public void OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType)
      {
      }

      public void OnPeerConnected(NetPeer peer)
      {
         _ = Task.Run(async () =>
           {
              byte[] Data = new byte[peer.Mtu * 4];
              Random RND = new Random();
              while (!CTS.IsCancellationRequested)
              {
                 for (int k = 0; k < 256; k++)
                 {
                    for (int i = 0; i < ChanCount; i++)
                    {
                       if (peer.GetPacketsCountInReliableQueue(((byte)i), true) < 200)
                       {
                          RND.NextBytes(Data);
                          peer.Send(Data, (byte)i, DeliveryMethod.ReliableOrdered);
                       }
                    }
                 }
                 await Task.Delay(10);
              }
           });
      }

      public void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
      {
         this.Dispose();
      }
   }
}
