using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LiteNetLib;
using MozUtil.Types;

namespace MozUtil.Clients
{
    public class RelayManager : IDisposable
    {
        private readonly LiteNetMozClient _client;
        private readonly SubTunInfo _subTunInfo;
        private TcpListener? _tcpListener;
        private UdpClient? _udpListener;
        private CancellationTokenSource? _cts;
        private readonly Dictionary<IPEndPoint, UdpRelaySession> _udpSessions = new();
        private readonly Dictionary<ushort, UdpRelaySession> _udpSessionsByConId = new();
        private readonly object _lock = new object();

        public RelayManager(LiteNetMozClient client, SubTunInfo subTunInfo)
        {
            _client = client;
            _subTunInfo = subTunInfo;
        }

        public void Start()
        {
            _cts = new CancellationTokenSource();
            if (_subTunInfo.Type == TunType.CustomTcpTun)
            {
                StartTcpRelay();
            }
            else if (_subTunInfo.Type == TunType.CustomUdpTun)
            {
                StartUdpRelay();
            }
        }

        private void StartTcpRelay()
        {
            try
            {
                _tcpListener = new TcpListener(IPAddress.Loopback, _subTunInfo.LocalPort);
                _tcpListener.Start();
                _subTunInfo.Status = TunStatus.Connected;
                
                var token = _cts.Token;
                Task.Run(async () =>
                {
                    while (!token.IsCancellationRequested)
                    {
                        try
                        {
                            var tcpClient = await _tcpListener.AcceptTcpClientAsync(token);
                            _ = Task.Run(async () => await HandleTcpClientAsync(tcpClient, token));
                        }
                        catch
                        {
                            break;
                        }
                    }
                    _subTunInfo.Status = TunStatus.Disconnected;
                });
            }
            catch (Exception ex)
            {
                Logger.Log($"TCP Relay Listener failed to start on {_subTunInfo.LocalPort}: {ex.Message}");
                _subTunInfo.Status = TunStatus.Failed;
            }
        }

        private async Task HandleTcpClientAsync(TcpClient tcpClient, CancellationToken token)
        {
            ushort conId = _client.AllocateConnectionId();
            byte selectedChannel = 0;
            
            lock (_client.ChannelConnectionCount)
            {
                short lowest = _client.ChannelConnectionCount.Values.Min();
                selectedChannel = _client.ChannelConnectionCount.FirstOrDefault(x => x.Value == lowest).Key;
                _client.ChannelConnectionCount[selectedChannel]++;
            }

            try
            {
                // Resolve target hostname/IP
                IPAddress? targetIP = null;
                if (IPAddress.TryParse(_subTunInfo.DestinationHostName, out var ip))
                {
                    targetIP = ip;
                }
                else
                {
                    var addresses = await Dns.GetHostAddressesAsync(_subTunInfo.DestinationHostName, token);
                    foreach (var addr in addresses)
                    {
                        if (addr.AddressFamily == AddressFamily.InterNetwork || addr.AddressFamily == AddressFamily.InterNetworkV6)
                        {
                            targetIP = addr;
                            break;
                        }
                    }
                }

                if (targetIP == null)
                {
                    throw new Exception($"Could not resolve destination hostname {_subTunInfo.DestinationHostName}");
                }

                // Send OpenEndToEndCustomPipe command to the server
                byte[] cmd = ClientCommandUtils.BuildCustomPipeCommand(conId, targetIP, _subTunInfo.DestinationPort, selectedChannel, ProtocolType.Tcp, DeliveryMethod.ReliableOrdered);
                
                // Get active peer
                List<NetPeer> connectedPeers = new List<NetPeer>();
                _client.LiteNetManager.GetPeersNonAlloc(connectedPeers, ConnectionState.Connected);
                if (connectedPeers.Count == 0)
                {
                    throw new Exception("No active LiteNet connection to signaling server.");
                }
                
                connectedPeers[0].Send(cmd, DeliveryMethod.ReliableUnordered);

                // Create the reliable connection mapping
                MozLiteNetReliableConnection con = new MozLiteNetReliableConnection(conId, selectedChannel, tcpClient, 0, ref _client.LiteNetManager, _client.MaxOutboundPackets);
                con.DataAvailable += _client.Con_DataAvailable;
                con.ConnectionClosed += _client.Con_ConnectionClosed;
                
                lock (_client.Connections)
                {
                    _client.Connections.Add(conId, con);
                }

                await con.StartConnectionAsync();
            }
            catch (Exception ex)
            {
                Logger.Log($"Error setting up TCP connection relay to {_subTunInfo.DestinationHostName}:{_subTunInfo.DestinationPort}: {ex.Message}");
                tcpClient.Dispose();
                lock (_client.ChannelConnectionCount)
                {
                    _client.ChannelConnectionCount[selectedChannel]--;
                }
            }
        }

        private void StartUdpRelay()
        {
            try
            {
                _udpListener = new UdpClient(new IPEndPoint(IPAddress.Loopback, _subTunInfo.LocalPort));
                _subTunInfo.Status = TunStatus.Connected;

                var token = _cts.Token;
                // Start UDP packet receiver loop
                Task.Run(async () =>
                {
                    while (!token.IsCancellationRequested)
                    {
                        try
                        {
                            var result = await _udpListener.ReceiveAsync(token);
                            await HandleIncomingLocalUdpPacketAsync(result.Buffer, result.RemoteEndPoint);
                        }
                        catch
                        {
                            break;
                        }
                    }
                    _subTunInfo.Status = TunStatus.Disconnected;
                });

                // Start cleanup loop for idle UDP sessions
                Task.Run(async () =>
                {
                    while (!token.IsCancellationRequested)
                    {
                        await Task.Delay(10000, token);
                        CleanupIdleUdpSessions();
                    }
                });
            }
            catch (Exception ex)
            {
                Logger.Log($"UDP Relay Listener failed to start on {_subTunInfo.LocalPort}: {ex.Message}");
                _subTunInfo.Status = TunStatus.Failed;
            }
        }

        private async Task HandleIncomingLocalUdpPacketAsync(byte[] data, IPEndPoint remoteEP)
        {
            UdpRelaySession? session;
            lock (_lock)
            {
                _udpSessions.TryGetValue(remoteEP, out session);
            }

            if (session == null)
            {
                // Create a new session/connection for this client source endpoint
                ushort conId = _client.AllocateConnectionId();
                byte selectedChannel = 0;

                lock (_client.ChannelConnectionCount)
                {
                    short lowest = _client.ChannelConnectionCount.Values.Min();
                    selectedChannel = _client.ChannelConnectionCount.FirstOrDefault(x => x.Value == lowest).Key;
                    _client.ChannelConnectionCount[selectedChannel]++;
                }

                try
                {
                    IPAddress? targetIP = null;
                    if (IPAddress.TryParse(_subTunInfo.DestinationHostName, out var ip))
                    {
                        targetIP = ip;
                    }
                    else
                    {
                        var addresses = await Dns.GetHostAddressesAsync(_subTunInfo.DestinationHostName);
                        foreach (var addr in addresses)
                        {
                            if (addr.AddressFamily == AddressFamily.InterNetwork || addr.AddressFamily == AddressFamily.InterNetworkV6)
                            {
                                targetIP = addr;
                                break;
                            }
                        }
                    }

                    if (targetIP == null)
                    {
                        throw new Exception($"Could not resolve destination hostname {_subTunInfo.DestinationHostName}");
                    }

                    // Register custom pipe on server
                    byte[] cmd = ClientCommandUtils.BuildCustomPipeCommand(conId, targetIP, _subTunInfo.DestinationPort, selectedChannel, ProtocolType.Udp, DeliveryMethod.Unreliable);
                    
                    List<NetPeer> connectedPeers = new List<NetPeer>();
                    _client.LiteNetManager.GetPeersNonAlloc(connectedPeers, ConnectionState.Connected);
                    if (connectedPeers.Count == 0)
                    {
                        throw new Exception("No active LiteNet connection.");
                    }
                    
                    connectedPeers[0].Send(cmd, DeliveryMethod.ReliableUnordered);

                    session = new UdpRelaySession(conId, selectedChannel, remoteEP);
                    lock (_lock)
                    {
                        _udpSessions[remoteEP] = session;
                        _udpSessionsByConId[conId] = session;
                    }
                    
                    lock (_client.UdpRelaySessionsByConID)
                    {
                        _client.UdpRelaySessionsByConID[conId] = this;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"Error setting up UDP session to {_subTunInfo.DestinationHostName}:{_subTunInfo.DestinationPort}: {ex.Message}");
                    lock (_client.ChannelConnectionCount)
                    {
                        _client.ChannelConnectionCount[selectedChannel]--;
                    }
                    return;
                }
            }

            session.LastActive = DateTime.UtcNow;

            // Send unreliable data to server
            // Format: [ConnectionID (2 bytes)] [Payload]
            byte[] sendBuf = new byte[2 + data.Length];
            BitConverter.GetBytes(session.ConnectionID).CopyTo(sendBuf, 0);
            Array.Copy(data, 0, sendBuf, 2, data.Length);

            List<NetPeer> peers = new List<NetPeer>();
            _client.LiteNetManager.GetPeersNonAlloc(peers, ConnectionState.Connected);
            if (peers.Count > 0)
            {
                peers[0].Send(sendBuf, session.ChannelID, DeliveryMethod.Unreliable);
            }
        }

        public void HandleIncomingUnreliablePacket(ushort connectionId, byte[] payload)
        {
            UdpRelaySession? session;
            lock (_lock)
            {
                _udpSessionsByConId.TryGetValue(connectionId, out session);
            }

            if (session != null)
            {
                session.LastActive = DateTime.UtcNow;
                try
                {
                    _udpListener?.Send(payload, payload.Length, session.SourceEndpoint);
                }
                catch (Exception ex)
                {
                    Logger.Log($"Error sending UDP reply to local client {session.SourceEndpoint}: {ex.Message}");
                }
            }
        }

        private void CleanupIdleUdpSessions()
        {
            var now = DateTime.UtcNow;
            var expired = new List<UdpRelaySession>();
            
            lock (_lock)
            {
                foreach (var session in _udpSessions.Values)
                {
                    if (now - session.LastActive > TimeSpan.FromSeconds(60))
                    {
                        expired.Add(session);
                    }
                }

                foreach (var session in expired)
                {
                    _udpSessions.Remove(session.SourceEndpoint);
                    _udpSessionsByConId.Remove(session.ConnectionID);
                    
                    lock (_client.UdpRelaySessionsByConID)
                    {
                        _client.UdpRelaySessionsByConID.Remove(session.ConnectionID);
                    }

                    lock (_client.ChannelConnectionCount)
                    {
                        _client.ChannelConnectionCount[session.ChannelID]--;
                    }

                    // Send close connection packet to server
                    byte[] CloseConReqBuffer = new byte[4];
                    BitConverter.GetBytes(session.ConnectionID).CopyTo(CloseConReqBuffer, 2);
                    List<NetPeer> connectedPeers = new List<NetPeer>();
                    _client.LiteNetManager.GetPeersNonAlloc(connectedPeers, ConnectionState.Connected);
                    if (connectedPeers.Count > 0)
                    {
                        connectedPeers[0].Send(CloseConReqBuffer, DeliveryMethod.ReliableUnordered);
                    }
                }
            }
        }

        public void Dispose()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;

            _tcpListener?.Stop();
            _udpListener?.Dispose();

            lock (_lock)
            {
                foreach (var session in _udpSessions.Values)
                {
                    lock (_client.ChannelConnectionCount)
                    {
                        _client.ChannelConnectionCount[session.ChannelID]--;
                    }
                    lock (_client.UdpRelaySessionsByConID)
                    {
                        _client.UdpRelaySessionsByConID.Remove(session.ConnectionID);
                    }
                }
                _udpSessions.Clear();
                _udpSessionsByConId.Clear();
            }
        }
    }

    public class UdpRelaySession
    {
        public ushort ConnectionID { get; }
        public byte ChannelID { get; }
        public IPEndPoint SourceEndpoint { get; }
        public DateTime LastActive { get; set; }

        public UdpRelaySession(ushort connectionId, byte channelId, IPEndPoint sourceEndpoint)
        {
            ConnectionID = connectionId;
            ChannelID = channelId;
            SourceEndpoint = sourceEndpoint;
            LastActive = DateTime.UtcNow;
        }
    }
}