using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using MozUtil;
using MozUtil.NatUtils;
using MozUtil.Types;

namespace DirtySocksASP
{
    public sealed class MZRoamServerService : IDisposable
    {
        private readonly string _seed;
        private readonly int _portCount;
        private readonly int _intervalSeconds;
        private readonly int _windowSize;
        private readonly int _minPort;
        private readonly int _maxPort;
        private readonly byte _typeMask;
        private readonly uint _sessionMask;
        private readonly byte[] _hmacKey;

        private readonly object _windowLock = new();
        private readonly Dictionary<long, int[]> _activeBuckets = new();
        private readonly ConcurrentDictionary<int, WindowPortListener> _listenersByPort = new();
        private readonly ConcurrentDictionary<uint, MZRoamSession> _sessionsById = new();
        private readonly ConcurrentDictionary<ulong, MZRoamSession> _sessionsByNonce = new();
        private readonly CancellationTokenSource _cts = new();
        private Task? _maintenanceTask;
        private int _disposed;

        public MZRoamServerService(
            string seed,
            int portCount = MZRoamProtocol.DefaultPortCount,
            int intervalSeconds = MZRoamProtocol.DefaultIntervalSeconds,
            int windowSize = MZRoamProtocol.DefaultWindowSize,
            int minPort = MZRoamProtocol.DefaultMinPort,
            int maxPort = MZRoamProtocol.DefaultMaxPort)
        {
            if (string.IsNullOrWhiteSpace(seed))
                throw new ArgumentException("MZRoam seed cannot be empty.", nameof(seed));

            _seed = seed;
            _portCount = Math.Clamp(portCount, 1, 256);
            _intervalSeconds = Math.Max(1, intervalSeconds);
            _windowSize = Math.Clamp(windowSize, 1, 21);
            _minPort = minPort;
            _maxPort = maxPort;

            MZRoamProtocol.DeriveSeedMasks(_seed, out _typeMask, out _sessionMask, out _hmacKey);
        }

        public int ActiveListenerCount => _listenersByPort.Count;
        public int ActiveSessionCount => _sessionsById.Count;

        public static MZRoamServerService? TryStartFromEnvironment()
        {
            string? enabledRaw = Environment.GetEnvironmentVariable("MOZ_ROAM_ENABLED");
            if (!bool.TryParse(enabledRaw, out bool enabled) || !enabled)
            {
                return null;
            }

            string? seed = Environment.GetEnvironmentVariable("MOZ_ROAM_SEED");
            if (string.IsNullOrWhiteSpace(seed))
            {
                Logger.Log("[MZRoam] MOZ_ROAM_ENABLED is true, but MOZ_ROAM_SEED is not set. MZRoam service will not start.");
                return null;
            }

            int portCount = ParseEnvInt("MOZ_ROAM_PORT_COUNT", MZRoamProtocol.DefaultPortCount);
            int intervalSeconds = ParseEnvInt("MOZ_ROAM_INTERVAL_SECONDS", MZRoamProtocol.DefaultIntervalSeconds);
            int windowSize = ParseEnvInt("MOZ_ROAM_WINDOW_SIZE", MZRoamProtocol.DefaultWindowSize);
            int minPort = ParseEnvInt("MOZ_ROAM_MIN_PORT", MZRoamProtocol.DefaultMinPort);
            int maxPort = ParseEnvInt("MOZ_ROAM_MAX_PORT", MZRoamProtocol.DefaultMaxPort);

            var service = new MZRoamServerService(seed.Trim(), portCount, intervalSeconds, windowSize, minPort, maxPort);
            service.Start();
            return service;
        }

        private static int ParseEnvInt(string name, int defaultValue)
        {
            string? raw = Environment.GetEnvironmentVariable(name);
            return int.TryParse(raw, out int parsed) && parsed > 0 ? parsed : defaultValue;
        }

        public void Start()
        {
            StaticServers.StartSocksServer();
            ReconcileWindow();
            _maintenanceTask = Task.Run(() => RunMaintenanceLoopAsync(_cts.Token));
            Logger.Log($"[MZRoam] Server started (ports/bucket={_portCount}, interval={_intervalSeconds}s, windowSize={_windowSize}, activePorts={_listenersByPort.Count}).");
        }

        private async Task RunMaintenanceLoopAsync(CancellationToken ct)
        {
            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
                while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                {
                    try
                    {
                        ReconcileWindow();
                        CleanupStaleSessions();
                    }
                    catch (Exception ex)
                    {
                        Logger.LogException(ex);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
        }

        private void ReconcileWindow()
        {
            long currentBucket = MZRoamProtocol.GetBucketIndex(DateTime.UtcNow.Ticks, _intervalSeconds);
            int halfBefore = (_windowSize - 1) / 2;
            int halfAfter = _windowSize - 1 - halfBefore;

            var desiredBuckets = new HashSet<long>();
            for (long offset = -halfBefore; offset <= halfAfter; offset++)
            {
                desiredBuckets.Add(currentBucket + offset);
            }

            lock (_windowLock)
            {
                // 1. Add new buckets first so shared ports increment their ref count before old buckets are removed
                foreach (long bucket in desiredBuckets)
                {
                    if (_activeBuckets.ContainsKey(bucket))
                        continue;

                    int[] ports = MZRoamProtocol.GetPortsForBucket(_seed, bucket, _portCount, _minPort, _maxPort);
                    _activeBuckets[bucket] = ports;

                    foreach (int port in ports)
                    {
                        if (_listenersByPort.TryGetValue(port, out var existing))
                        {
                            existing.IncrementRef();
                        }
                        else
                        {
                            var listener = TryCreateListener(port);
                            if (listener != null)
                            {
                                _listenersByPort[port] = listener;
                                listener.Start();
                            }
                        }
                    }
                }

                // 2. Remove expired buckets
                long[] expired = _activeBuckets.Keys.Where(b => !desiredBuckets.Contains(b)).ToArray();
                foreach (long bucket in expired)
                {
                    if (_activeBuckets.Remove(bucket, out int[]? ports) && ports != null)
                    {
                        foreach (int port in ports)
                        {
                            if (_listenersByPort.TryGetValue(port, out var listener))
                            {
                                if (listener.DecrementRef() <= 0)
                                {
                                    if (_listenersByPort.TryRemove(port, out var removed))
                                    {
                                        removed.Dispose();
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        private WindowPortListener? TryCreateListener(int port)
        {
            try
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                socket.Bind(new IPEndPoint(IPAddress.Any, port));
                return new WindowPortListener(port, socket, HandleIncomingWindowPacketAsync);
            }
            catch (SocketException ex)
            {
                Logger.Log($"[MZRoam] Could not bind window UDP port {port} ({ex.SocketErrorCode}); skipping port.");
                return null;
            }
        }

        private void CleanupStaleSessions()
        {
            long nowTicks = DateTime.UtcNow.Ticks;
            long maxIdleTicks = TimeSpan.FromSeconds(60).Ticks;

            foreach (var kvp in _sessionsById)
            {
                var session = kvp.Value;
                if (session.LNClient.IsDisposed || (nowTicks - Interlocked.Read(ref session.LastActivityTicks)) > maxIdleTicks)
                {
                    RemoveSession(session);
                }
            }
        }

        private void RemoveSession(MZRoamSession session)
        {
            _sessionsById.TryRemove(session.SessionId, out _);
            _sessionsByNonce.TryRemove(session.ClientNonce, out _);
            session.Dispose();
        }

        private async Task HandleIncomingWindowPacketAsync(WindowPortListener listener, ReadOnlyMemory<byte> packet, EndPoint remoteEndPoint)
        {
            if (MZRoamProtocol.TryParseDataPacket(
                packet.Span,
                _typeMask,
                _sessionMask,
                out uint sessionId,
                out byte[] rawLiteNet))
            {
                if (_sessionsById.TryGetValue(sessionId, out var session) && !session.LNClient.IsDisposed)
                {
                    session.UpdateClientRoute(remoteEndPoint, listener);
                    await session.ForwardToLNClientAsync(rawLiteNet).ConfigureAwait(false);
                }
                return;
            }

            long currentBucket = MZRoamProtocol.GetBucketIndex(DateTime.UtcNow.Ticks, _intervalSeconds);
            int maxSkew = Math.Max(2, (_windowSize + 1) / 2);

            if (MZRoamProtocol.TryParseAndVerifyHello(
                packet.Span,
                _typeMask,
                _hmacKey,
                currentBucket,
                maxSkew,
                out ulong clientNonce,
                out ushort protoVer,
                out ushort minVer,
                out MozProtocolCapabilities caps))
            {
                if (!MozProtocol.IsCompatible(protoVer, minVer))
                {
                    Logger.Log($"[MZRoam] Rejected incompatible client protocol range v{minVer}-v{protoVer}.");
                    return;
                }

                MZRoamSession? session = GetOrCreateSession(clientNonce, protoVer, minVer, caps, remoteEndPoint, listener);
                if (session == null)
                    return;

                session.UpdateClientRoute(remoteEndPoint, listener);

                byte[] ackPacket = MZRoamProtocol.BuildHelloAckPacket(
                    _typeMask,
                    _sessionMask,
                    _hmacKey,
                    clientNonce,
                    session.SessionId,
                    session.ConnectionKeyBytes);

                try
                {
                    await listener.Socket.SendToAsync(ackPacket, SocketFlags.None, remoteEndPoint).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
                {
                }
            }
        }

        private MZRoamSession? GetOrCreateSession(
            ulong clientNonce,
            ushort protoVer,
            ushort minVer,
            MozProtocolCapabilities caps,
            EndPoint clientEndPoint,
            WindowPortListener listener)
        {
            if (_sessionsByNonce.TryGetValue(clientNonce, out var existing) && !existing.LNClient.IsDisposed)
            {
                return existing;
            }

            lock (_windowLock)
            {
                if (_sessionsByNonce.TryGetValue(clientNonce, out existing) && !existing.LNClient.IsDisposed)
                {
                    return existing;
                }

                uint sessionId = AllocateSessionId();
                StaticServers.StartSocksServer();

                var lnClient = new LNClient(protoVer, minVer, caps);
                var (lnPort, connKeyBytes) = lnClient.StartRoamSession();

                var session = new MZRoamSession(
                    sessionId,
                    clientNonce,
                    lnClient,
                    new IPEndPoint(IPAddress.Loopback, lnPort),
                    connKeyBytes,
                    clientEndPoint,
                    listener,
                    _typeMask,
                    _sessionMask,
                    GetFallbackListener);

                lnClient.Disposed += (_, _) => RemoveSession(session);

                _sessionsById[sessionId] = session;
                _sessionsByNonce[clientNonce] = session;
                session.Start();

                Logger.Log($"[MZRoam] Created session 0x{sessionId:X8} for {clientEndPoint} -> LNClient 127.0.0.1:{lnPort}.");
                return session;
            }
        }

        private WindowPortListener? GetFallbackListener()
        {
            foreach (var kvp in _listenersByPort)
            {
                if (!kvp.Value.IsDisposed)
                    return kvp.Value;
            }
            return null;
        }

        private uint AllocateSessionId()
        {
            Span<byte> buf = stackalloc byte[4];
            while (true)
            {
                RandomNumberGenerator.Fill(buf);
                uint id = BitConverter.ToUInt32(buf);
                if (id != 0 && !_sessionsById.ContainsKey(id))
                    return id;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            try { _cts.Cancel(); } catch { }
            foreach (var kvp in _listenersByPort)
            {
                kvp.Value.Dispose();
            }
            _listenersByPort.Clear();

            foreach (var kvp in _sessionsById)
            {
                kvp.Value.Dispose();
            }
            _sessionsById.Clear();
            _sessionsByNonce.Clear();
            _cts.Dispose();
        }

        internal sealed class WindowPortListener : IDisposable
        {
            private readonly Func<WindowPortListener, ReadOnlyMemory<byte>, EndPoint, Task> _onPacket;
            private readonly CancellationTokenSource _cts = new();
            private int _refCount = 1;
            private int _disposed;

            public WindowPortListener(
                int port,
                Socket socket,
                Func<WindowPortListener, ReadOnlyMemory<byte>, EndPoint, Task> onPacket)
            {
                Port = port;
                Socket = socket;
                _onPacket = onPacket;
            }

            public int Port { get; }
            public Socket Socket { get; }
            public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

            public int IncrementRef() => Interlocked.Increment(ref _refCount);
            public int DecrementRef() => Interlocked.Decrement(ref _refCount);

            public void Start()
            {
                _ = Task.Run(() => ReceiveLoopAsync(_cts.Token));
            }

            private async Task ReceiveLoopAsync(CancellationToken ct)
            {
                byte[] buffer = ArrayPool<byte>.Shared.Rent(65536);
                EndPoint anyRemote = new IPEndPoint(IPAddress.Any, 0);
                try
                {
                    while (!ct.IsCancellationRequested && !IsDisposed)
                    {
                        SocketReceiveFromResult res;
                        try
                        {
                            res = await Socket.ReceiveFromAsync(buffer, SocketFlags.None, anyRemote, ct).ConfigureAwait(false);
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
                            if (IsDisposed) break;
                            continue;
                        }

                        if (res.ReceivedBytes <= 0)
                            continue;

                        await _onPacket(this, buffer.AsMemory(0, res.ReceivedBytes), res.RemoteEndPoint).ConfigureAwait(false);
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
                try { Socket.Dispose(); } catch { }
                _cts.Dispose();
            }
        }

        private sealed class MZRoamSession : IDisposable
        {
            private readonly Socket _loopbackSocket;
            private readonly IPEndPoint _lnClientLoopbackEp;
            private readonly byte _typeMask;
            private readonly uint _sessionMask;
            private readonly Func<WindowPortListener?> _fallbackListener;
            private readonly CancellationTokenSource _cts = new();

            private volatile EndPoint _lastClientEndPoint;
            private volatile WindowPortListener _lastWindowListener;
            public long LastActivityTicks;
            private int _disposed;

            public MZRoamSession(
                uint sessionId,
                ulong clientNonce,
                LNClient lnClient,
                IPEndPoint lnClientLoopbackEp,
                byte[] connectionKeyBytes,
                EndPoint initialClientEndPoint,
                WindowPortListener initialListener,
                byte typeMask,
                uint sessionMask,
                Func<WindowPortListener?> fallbackListener)
            {
                SessionId = sessionId;
                ClientNonce = clientNonce;
                LNClient = lnClient;
                _lnClientLoopbackEp = lnClientLoopbackEp;
                ConnectionKeyBytes = connectionKeyBytes;
                _lastClientEndPoint = initialClientEndPoint;
                _lastWindowListener = initialListener;
                _typeMask = typeMask;
                _sessionMask = sessionMask;
                _fallbackListener = fallbackListener;
                LastActivityTicks = DateTime.UtcNow.Ticks;

                _loopbackSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                _loopbackSocket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            }

            public uint SessionId { get; }
            public ulong ClientNonce { get; }
            public LNClient LNClient { get; }
            public byte[] ConnectionKeyBytes { get; }

            public void Start()
            {
                _ = Task.Run(() => LoopbackToClientLoopAsync(_cts.Token));
            }

            public void UpdateClientRoute(EndPoint clientEndPoint, WindowPortListener listener)
            {
                _lastClientEndPoint = clientEndPoint;
                _lastWindowListener = listener;
                Interlocked.Exchange(ref LastActivityTicks, DateTime.UtcNow.Ticks);
            }

            public async Task ForwardToLNClientAsync(byte[] rawLiteNetPacket)
            {
                if (Volatile.Read(ref _disposed) != 0)
                    return;

                try
                {
                    await _loopbackSocket.SendToAsync(rawLiteNetPacket, SocketFlags.None, _lnClientLoopbackEp, _cts.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is SocketException or ObjectDisposedException or OperationCanceledException)
                {
                }
            }

            private async Task LoopbackToClientLoopAsync(CancellationToken ct)
            {
                byte[] buffer = ArrayPool<byte>.Shared.Rent(65536);
                EndPoint anyLoopback = new IPEndPoint(IPAddress.Any, 0);
                try
                {
                    while (!ct.IsCancellationRequested && Volatile.Read(ref _disposed) == 0)
                    {
                        SocketReceiveFromResult res;
                        try
                        {
                            res = await _loopbackSocket.ReceiveFromAsync(buffer, SocketFlags.None, anyLoopback, ct).ConfigureAwait(false);
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
                            if (Volatile.Read(ref _disposed) != 0) break;
                            continue;
                        }

                        if (res.ReceivedBytes <= 0)
                            continue;

                        Interlocked.Exchange(ref LastActivityTicks, DateTime.UtcNow.Ticks);

                        byte[] framed = MZRoamProtocol.BuildDataPacket(
                            _typeMask,
                            _sessionMask,
                            SessionId,
                            buffer.AsSpan(0, res.ReceivedBytes));

                        WindowPortListener? listener = _lastWindowListener;
                        if (listener == null || listener.IsDisposed)
                        {
                            listener = _fallbackListener();
                        }

                        EndPoint clientEp = _lastClientEndPoint;
                        if (listener != null && !listener.IsDisposed && clientEp != null)
                        {
                            try
                            {
                                await listener.Socket.SendToAsync(framed, SocketFlags.None, clientEp, ct).ConfigureAwait(false);
                            }
                            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or OperationCanceledException)
                            {
                            }
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
                try { _loopbackSocket.Dispose(); } catch { }
                try { LNClient.Dispose(); } catch { }
                _cts.Dispose();
            }
        }
    }
}
