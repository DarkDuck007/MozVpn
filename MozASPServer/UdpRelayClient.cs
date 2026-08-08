using MozUtil;
using MozUtil.NatUtils;
using MozUtil.Types;
using STUN;
using System.Net;
using System.Net.Sockets;

namespace DirtySocksASP
{
   public class UdpRelayClient
   {
      STUNQueryResult ServerStunResult;
      HttpContext _context;
      UdpClient _UdpClient;
      public UdpRelayClient(HttpContext context)
      {
         _context = context;
      }

      public async Task<DisconnectionReason> StartRelay()
      {
         //Server nat type is defaulted to PortRestricted.
         //ServerNatType = STUNNATType.PortRestricted;
         //ServerStunResult = MozStun.IPDiscoverOnly(StaticData.StunServers)
         //Ignoring stun again. for speed...

         try
         {
            Logger.Log("extracting relay request info...");
            byte[] Buffer = new byte[4096];
            int read = await _context.Request.Body.ReadAsync(Buffer);
            byte[] StrBytes = new byte[read];
            Array.Copy(Buffer, StrBytes, read);
            UdpRelayRequestInfo RelayReqInfo = UdpRelayRequestInfo.fromBytes(StrBytes);
            Logger.Log("Successfully extracted relay request info.");
            //now do nat punching...
            //Sending stun just for IP Discovery.
            _UdpClient = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
            int i = 0;
            ServerStunResult = new STUNQueryResult();
            ServerStunResult.PublicEndPoint = null;
            while (ServerStunResult.PublicEndPoint is null && i <= 10)
            {
               ServerStunResult = MozStun.IPDiscoverOnly(_UdpClient.Client, StaticData.StunServers[i]);
               i++;
            }
            Logger.Log("Public port after IP Discovery only: " + ServerStunResult.PublicEndPoint.Port);
            ServerStunResult.NATType = STUNNATType.PortRestricted;
            using (MemoryStream Mem = new MemoryStream())
            {
               Mem.WriteByte((byte)((int)ServerCommands.BeginHolePunching));
               await Mem.WriteAsync(MozStatic.SerializePunchInfo(ServerStunResult, 10000));
               await Mem.FlushAsync();
               await _context.Response.Body.WriteAsync(Mem.ToArray());
            }
            int[]? PunchedPortsCollection;
            //bool PunchResult = await UDPPunchModule.TryPunch(UdpSrv, ClientEndpoint, 10000);
            udpPuncher Puncher = new udpPuncher();
            bool PunchResult = false;

            IPAddress ClientIPAddress;
            if (RelayReqInfo.SourceAddressFamily == AddressFamily.InterNetwork)
            {
               byte[] Arrbr = new byte[4];
               Array.Copy(RelayReqInfo.SourceipAddress, Arrbr, 4);
               ClientIPAddress = (new IPAddress(Arrbr));
            }
            else
            {
               ClientIPAddress = (new IPAddress(RelayReqInfo.SourceipAddress));
            }
            IPEndPoint ClientEndpoint = new IPEndPoint(ClientIPAddress, RelayReqInfo.SourcePort);
            if (((int)RelayReqInfo.SourceNatType) >= 1 && ((int)RelayReqInfo.SourceNatType) <= 4)
            {
               //PunchResult = await Puncher.PR2PRPunch(_UdpClient, ClientEndpoint);
            }
            else if (RelayReqInfo.SourceNatType == STUNNATType.Symmetric)
            {
               _ = Task.Run(async () =>
                {
                   for (int k = 0; k < 5; k++)
                   {
                      for (int j = 0; j < RelayReqInfo.SourcePortsCount; j++)
                      {
                         await _UdpClient.SendAsync(new byte[0], new IPEndPoint(ClientIPAddress, RelayReqInfo.SourcePort + j));
                      }
                   }

                });
               //int[] PunchedPorts = await Puncher.PR2SymmetricPunch(_UdpClient, RelayReqInfo.SourcePort, RelayReqInfo.SourcePort + RelayReqInfo.SourcePortsCount, ClientIPAddress.ToString());
               //int[] PunchedPorts = await Puncher.PRNatPunchToSymmetricAsync(UdpSrv, ClientPeerInfo.Port, ClientPeerInfo.Port + ClientPeerInfo.PortsCount, ClientPeerInfo.ipAddress.ToString());
               //if (PunchedPorts.Length > 0)
               //{
               //   PunchResult = true;
               //   PunchedPortsCollection = PunchedPorts;
               //}
               //else
               //{
               //   PunchResult = false;
               //   PunchedPortsCollection = null;
               //}
            }
            else if (RelayReqInfo.SourceNatType == STUNNATType.Unspecified)
            {
               return DisconnectionReason.ServerException;
            }
            PunchResult = true;
            if (PunchResult)
            {
               using (MemoryStream Mem = new MemoryStream())
               {
                  Mem.WriteByte((byte)((int)ServerCommands.BeginUdpClient));
                  byte[] ConInfo = MozStatic.SerializeUdpConnectionInfo(TransportMode.UDPRelay, ServerStunResult.PublicEndPoint.Address,
                  ServerStunResult.PublicEndPoint.Port);
                  Mem.Write(ConInfo);
                  await Mem.FlushAsync();
                  await _context.Response.Body.WriteAsync(Mem.ToArray());
               }
               await _context.Response.Body.FlushAsync();
               CancellationTokenSource cts = new CancellationTokenSource();
               Logger.Log($"UDP Connection Request sent to {ClientEndpoint}");
               using (UDPTunnel udpTun = new UDPTunnel(_UdpClient, ClientEndpoint))
               {
                  await udpTun.StartTunnelAsync(cts.Token, ProtocolType.Udp, ClientEndpoint, RelayReqInfo.Desthostname, RelayReqInfo.DestPort);
               }
            }
         }
         catch (Exception ex)
         {
            Logger.Log(ex);
            return DisconnectionReason.ServerException;
         }
         return DisconnectionReason.Unknown;
      }
   }
}
