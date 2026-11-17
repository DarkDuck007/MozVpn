using System;
using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using MozUtil.NatUtils;
using MozUtil.Types;

namespace MozUtil.Clients
{
    public sealed class MZRoamClientRelay : IDisposable
    {
        private readonly string _serverHost;
        private readonly string _seed;
        private readonly int _portCount;
        private readonly int _intervalSeconds;
        private readonly int _minPort;
        private readonly int _maxPort;
        private readonly byte _typeMask;
        private readonly uint _sessionMask;
        private readonly CancellationTokenSource _cts = new();

        private IPAddress? _serverIp;
        private Socket? _localSocket;
        private Socket? _remoteSocket;
        private EndPoint? _liteNetClientLoopbackEp;

        private long _cachedBucketIndex = long.MinValue;
        private int[] _cachedBucketPorts = Array.Empty<int>();
        private int _activePortIndex;
        private long _lastInboundTicks;
        private uint _packetCounter;

        private int _disposed;

        public MZRoamClientRelay(
            string serverHost,
            string seed,
            int portCount = MZRoamProtocol.DefaultPortCount,
            int intervalSeconds = MZRoamProtocol.DefaultIntervalSeconds,
            int minPort = MZRoamProtocol.DefaultMinPort,
            int maxPort = MZRoamProtocol.DefaultMaxPort)
        {
            if (string.IsNullOrWhiteSpace(serverHost))
                throw new ArgumentException("Server host is required for MZRoam.", nameof(serverHost));
            if (string.IsNullOrWhiteSpace(seed))
                throw new ArgumentException("Seed is required for MZRoam.", nameof(seed));

            _serverHost = serverHost;
            _seed = seed;
            _portCount = Math.Clamp(portCount, 1, 256);
            _intervalSeconds = Math.Max(1, intervalSeconds);
            _minPort = minPort;
            _maxPort = maxPort;

            MZRoamProtocol.DeriveSeedMasks(_seed, out _typeMask, out _sessionMask, out _);
        }

        public IPEndPoint? LocalRelayEndPoint { get; private set; }
        public uint SessionId { get; private set; }
        public string? ConnectionKey { get; private set; }

        public async Task<bool> InitializeAndHandshakeAsync(int timeoutMs = 10000, CancellationToken cancellationToken = default)
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken);
            CancellationToken ct = linkedCts.Token;

            _serverIp = await MZRoamProtocol.ResolveServerAddressAsync(_serverHost).ConfigureAwait(false);
            AddressFamily remoteFamily = _serverIp.AddressFamily;

            _localSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _localSocket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            LocalRelayEndPoint = (IPEndPoint)_localSocket.LocalEndPoint!;

            _remoteSocket = new Socket(remoteFamily, SocketType.Dgram, ProtocolType.Udp);
            _remoteSocket.Bind(new IPEndPoint(remoteFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0));

            byte[] nonceBytes = new byte[8];
            RandomNumberGenerator.Fill(nonceBytes);
            ulong clientNonce = BitConverter.ToUInt64(nonceBytes, 0);

            Logger.Log($"[MZRoam] Resolved '{_serverHost}' -> {_serverIp}. Starting UDP handshake (ports={_portCount}, interval={_intervalSeconds}s)...");

            var sw = Stopwatch.StartNew();
            byte[] receiveBuffer = new byte[2048];
            EndPoint anyRemote = new IPEndPoint(remoteFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
            int probeCursor = 0;

            while (sw.ElapsedMilliseconds < timeoutMs && !ct.IsCancellationRequested)
            {
                long nowTicks = DateTime.UtcNow.Ticks;
                long bucketIndex = MZRoamProtocol.GetBucketIndex(nowTicks, _intervalSeconds);
                int[] ports = GetOrUpdateBucketPorts(bucketIndex);

                byte[] helloPacket = MZRoamProtocol.BuildHelloPacket(_seed, clientNonce, bucketIndex);

                // Probe up to 4 ports per tick (or all if fewer) so we quickly cover the bucket without flooding
                int batch = Math.Min(ports.Length, 4);
                for (int i = 0; i < batch; i++)
                {
                    int port = ports[(probeCursor + i) % ports.Length];
                    var destEp = new IPEndPoint(_serverIp, port);
                    try
                    {
                        await _remoteSocket.SendToAsync(helloPacket, SocketFlags.None, destEp, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Logger.Log($"[MZRoam] Handshake send error on port {port}: {ex.Message}");
                    }
                }
                probeCursor = (probeCursor + batch) % Math.Max(1, ports.Length);

                using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                waitCts.CancelAfter(220);

                try
                {
                    while (!waitCts.IsCancellationRequested)
                    {
                        var res = await _remoteSocket.ReceiveFromAsync(receiveBuffer, SocketFlags.None, anyRemote, waitCts.Token).ConfigureAwait(false);
                        if (MZRoamProtocol.TryParseAndVerifyHelloAck(
                            receiveBuffer.AsSpan(0, res.ReceivedBytes),
                            _seed,
                            clientNonce,
                            out uint sessionId,
                            out string connKey))
                        {
                            SessionId = sessionId;
                            ConnectionKey = connKey;
                            _lastInboundTicks = DateTime.UtcNow.Ticks;

                            if (res.RemoteEndPoint is IPEndPoint ackEp)
                            {
                                int idx = Array.IndexOf(ports, ackEp.Port);
                                if (idx >= 0) _activePortIndex = idx;
                                Logger.Log($"[MZRoam] Handshake succeeded via port {ackEp.Port} (SessionId=0x{sessionId:X8}).");
                            }

                            _ = Task.Run(() => LoopLocalToRemoteAsync(_cts.Token));
                            _ = Task.Run(() => LoopRemoteToLocalAsync(_cts.Token));
                            return true;
                        }
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // Retransmit on next tick
                }
                catch (SocketException)
                {
                    await Task.Delay(100, ct).ConfigureAwait(false);
                }
            }

            Logger.Log("[MZRoam] Handshake timed out.");
            return false;
        }

        private int[] GetOrUpdateBucketPorts(long bucketIndex)
        {
            if (bucketIndex != Interlocked.Read(ref _cachedBucketIndex) || _cachedBucketPorts.Length == 0)
            {
                int[] nextPorts = MZRoamProtocol.GetPortsForBucket(_seed, bucketIndex, _portCount, _minPort, _maxPort);
                _cachedBucketPorts = nextPorts;
                Interlocked.Exchange(ref _cachedBucketIndex, bucketIndex);
            }
            return _cachedBucketPorts;
        }

        private int SelectTargetPort(long nowTicks)
        {
            long bucketIndex = MZRoamProtocol.GetBucketIndex(nowTicks, _intervalSeconds);
            int[] ports = GetOrUpdateBucketPorts(bucketIndex);

            // Rotate across the current bucket's ports if no inbound traffic has arrived recently,
            // or periodically hop across the bucket ports to distribute traffic.
            long sinceInboundMs = (nowTicks - Interlocked.Read(ref _lastInboundTicks)) / TimeSpan.TicksPerMillisecond;
            uint count = Interlocked.Increment(ref _packetCounter);
            if (sinceInboundMs > 1200 || (count % 32 == 0))
            {
                _activePortIndex = (int)((uint)Interlocked.Increment(ref _activePortIndex) % (uint)ports.Length);
            }

            int idx = Math.Abs(_activePortIndex) % ports.Length;
            return ports[idx];
        }

        private async Task LoopLocalToRemoteAsync(CancellationToken ct)
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(65536);
            EndPoint anyLoopback = new IPEndPoint(IPAddress.Any, 0);
            try
            {
                while (!ct.IsCancellationRequested && _localSocket != null && _remoteSocket != null && _serverIp != null)
                {
                    SocketReceiveFromResult result;
                    try
                    {
                        result = await _localSocket.ReceiveFromAsync(buffer, SocketFlags.None, anyLoopback, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }
                    catch (SocketException)
                    {
                        continue;
                    }

                    if (result.ReceivedBytes <= 0)
                        continue;

                    _liteNetClientLoopbackEp = result.RemoteEndPoint;

                    int targetPort = SelectTargetPort(DateTime.UtcNow.Ticks);
                    byte[] framed = MZRoamProtocol.BuildDataPacket(
                        _typeMask,
                        _sessionMask,
                        SessionId,
                        buffer.AsSpan(0, result.ReceivedBytes));

                    try
                    {
                        await _remoteSocket.SendToAsync(framed, SocketFlags.None, new IPEndPoint(_serverIp, targetPort), ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
                    {
                        if (_disposed != 0) break;
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private async Task LoopRemoteToLocalAsync(CancellationToken ct)
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(65536);
            EndPoint anyRemote = new IPEndPoint(
                _serverIp?.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any,
                0);

            try
            {
                while (!ct.IsCancellationRequested && _localSocket != null && _remoteSocket != null)
                {
                    SocketReceiveFromResult result;
                    try
                    {
                        result = await _remoteSocket.ReceiveFromAsync(buffer, SocketFlags.None, anyRemote, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }
                    catch (SocketException)
                    {
                        continue;
                    }

                    if (result.ReceivedBytes <= MZRoamProtocol.DataHeaderLength)
                        continue;

                    if (!MZRoamProtocol.TryParseDataPacket(
                        buffer.AsSpan(0, result.ReceivedBytes),
                        _typeMask,
                        _sessionMask,
                        out uint sessionId,
                        out byte[] rawPayload))
                    {
                        continue;
                    }

                    if (sessionId != SessionId)
                        continue;

                    Interlocked.Exchange(ref _lastInboundTicks, DateTime.UtcNow.Ticks);

                    EndPoint? clientEp = _liteNetClientLoopbackEp;
                    if (clientEp == null)
                        continue;

                    try
                    {
                        await _localSocket.SendToAsync(rawPayload, SocketFlags.None, clientEp, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
                    {
                        if (_disposed != 0) break;
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            try { _cts.Cancel(); } catch { }
            try { _localSocket?.Dispose(); } catch { }
            try { _remoteSocket?.Dispose(); } catch { }
            _cts.Dispose();
        }
    }
}
