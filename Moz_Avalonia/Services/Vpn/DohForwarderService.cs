using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Moz_Avalonia.Services.Vpn;

/// <summary>
/// A lightweight local DNS server that tunnels all standard UDP and TCP DNS queries
/// through the active MozVpn SOCKS5 proxy using DNS-over-HTTPS (RFC 8484).
/// </summary>
public sealed class DohForwarderService : IAsyncDisposable
{
    private static readonly string[] DohUpstreamUrls =
    [
        "https://1.1.1.1/dns-query",     // Cloudflare Primary
        "https://8.8.8.8/dns-query",     // Google Primary
        "https://1.0.0.1/dns-query",     // Cloudflare Secondary
        "https://8.8.4.4/dns-query"      // Google Secondary
    ];

    private static readonly MediaTypeHeaderValue DnsMediaType =
        new("application/dns-message");

    private readonly ConcurrentDictionary<string, (byte[] Response, DateTime ExpiresAt)> _cache = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    private UdpClient? _udpListener;
    private TcpListener? _tcpListener;
    private HttpClient? _httpClient;
    private CancellationTokenSource? _cts;

    public int ListenPort { get; private set; } = 53053;
    public bool IsRunning => _cts != null && !_cts.IsCancellationRequested;

    public async Task StartAsync(int socksPort, int preferredPort = 53053, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (IsRunning) await StopInternalAsync();

            _cts = new CancellationTokenSource();
            ListenPort = preferredPort;

            // Configure HttpClient with SocketsHttpHandler pointing to local MozVpn SOCKS5 proxy
            var handler = new SocketsHttpHandler
            {
                Proxy = new WebProxy($"socks5://127.0.0.1:{socksPort}"),
                UseProxy = true,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
                ConnectTimeout = TimeSpan.FromSeconds(5)
            };

            _httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(6)
            };

            // Bind UDP listener
            _udpListener = BindUdpListener(ref preferredPort);
            ListenPort = preferredPort;

            // Bind TCP listener on same port
            try
            {
                _tcpListener = new TcpListener(IPAddress.Any, ListenPort);
                _tcpListener.Server.NoDelay = true;
                _tcpListener.Start();
            }
            catch (Exception ex)
            {
                MozUtil.Logger.Log($"[DoH] Could not bind TCP DNS listener on {ListenPort}: {ex.Message}");
            }

            MozUtil.Logger.Log($"[DoH] DNS-over-HTTPS forwarder active on port {ListenPort} (via SOCKS5 127.0.0.1:{socksPort}).");

            // Start background receiver loops
            _ = Task.Run(() => ListenUdpAsync(_udpListener, _httpClient, _cts.Token));
            if (_tcpListener != null)
            {
                _ = Task.Run(() => ListenTcpAsync(_tcpListener, _httpClient, _cts.Token));
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await StopInternalAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StopInternalAsync()
    {
        if (_cts != null)
        {
            try
            {
                _cts.Cancel();
            }
            catch {}
            _cts.Dispose();
            _cts = null;
        }

        try
        {
            _udpListener?.Close();
            _udpListener?.Dispose();
        }
        catch {}
        _udpListener = null;

        try
        {
            _tcpListener?.Stop();
        }
        catch {}
        _tcpListener = null;

        try
        {
            _httpClient?.Dispose();
        }
        catch {}
        _httpClient = null;

        _cache.Clear();
        MozUtil.Logger.Log("[DoH] DNS-over-HTTPS forwarder stopped.");
        await Task.CompletedTask;
    }

    private static UdpClient BindUdpListener(ref int port)
    {
        for (int i = 0; i < 10; i++)
        {
            try
            {
                var client = new UdpClient(new IPEndPoint(IPAddress.Any, port));
                return client;
            }
            catch (SocketException)
            {
                port++;
            }
        }

        // Fallback to OS assigned ephemeral port if specified range busy
        var ephemeralClient = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        port = ((IPEndPoint)ephemeralClient.Client.LocalEndPoint!).Port;
        return ephemeralClient;
    }

    private async Task ListenUdpAsync(UdpClient listener, HttpClient client, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                UdpReceiveResult result = await listener.ReceiveAsync(ct);
                if (result.Buffer.Length < 12) continue; // Minimum valid DNS header size is 12 bytes

                _ = Task.Run(async () =>
                {
                    try
                    {
                        byte[]? response = await ResolveDohAsync(result.Buffer, client, ct);
                        if (response != null && response.Length > 0)
                        {
                            await listener.SendAsync(response, response.Length, result.RemoteEndPoint);
                        }
                    }
                    catch (Exception ex)
                    {
                        MozUtil.Logger.Log($"[DoH] Failed answering UDP DNS query: {ex.Message}");
                    }
                }, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested) break;
                MozUtil.Logger.Log($"[DoH] UDP receive error: {ex.Message}");
                await Task.Delay(50, ct);
            }
        }
    }

    private async Task ListenTcpAsync(TcpListener listener, HttpClient client, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                TcpClient tcpClient = await listener.AcceptTcpClientAsync(ct);
                _ = Task.Run(async () =>
                {
                    using (tcpClient)
                    using (var stream = tcpClient.GetStream())
                    {
                        try
                        {
                            byte[] lenBuffer = new byte[2];
                            int read = await stream.ReadAsync(lenBuffer.AsMemory(0, 2), ct);
                            if (read < 2) return;

                            int msgLen = (lenBuffer[0] << 8) | lenBuffer[1];
                            if (msgLen < 12 || msgLen > 4096) return;

                            byte[] queryBuffer = new byte[msgLen];
                            int totalRead = 0;
                            while (totalRead < msgLen)
                            {
                                int r = await stream.ReadAsync(queryBuffer.AsMemory(totalRead, msgLen - totalRead), ct);
                                if (r <= 0) break;
                                totalRead += r;
                            }
                            if (totalRead < msgLen) return;

                            byte[]? response = await ResolveDohAsync(queryBuffer, client, ct);
                            if (response != null && response.Length > 0)
                            {
                                byte[] respLenBuffer = [(byte)(response.Length >> 8), (byte)(response.Length & 0xFF)];
                                await stream.WriteAsync(respLenBuffer.AsMemory(0, 2), ct);
                                await stream.WriteAsync(response.AsMemory(0, response.Length), ct);
                            }
                        }
                        catch (Exception ex)
                        {
                            MozUtil.Logger.Log($"[DoH] Failed answering TCP DNS query: {ex.Message}");
                        }
                    }
                }, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested) break;
                MozUtil.Logger.Log($"[DoH] TCP accept error: {ex.Message}");
                await Task.Delay(50, ct);
            }
        }
    }

    private async Task<byte[]?> ResolveDohAsync(byte[] queryBuffer, HttpClient client, CancellationToken ct)
    {
        if (queryBuffer.Length < 12) return null;

        // Cache key: question bytes (ignoring first 2 bytes which is random Transaction ID)
        string cacheKey = Convert.ToBase64String(queryBuffer, 2, queryBuffer.Length - 2);
        if (_cache.TryGetValue(cacheKey, out var cached) && cached.ExpiresAt > DateTime.UtcNow)
        {
            // Clone cached response and overwrite transaction ID to match incoming query
            byte[] cachedResponse = (byte[])cached.Response.Clone();
            cachedResponse[0] = queryBuffer[0];
            cachedResponse[1] = queryBuffer[1];
            return cachedResponse;
        }

        // Query DoH upstreams with automatic fallback
        foreach (string dohUrl in DohUpstreamUrls)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, dohUrl);
                using var content = new ByteArrayContent(queryBuffer);
                content.Headers.ContentType = DnsMediaType;
                request.Content = content;
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/dns-message"));

                using var response = await client.SendAsync(request, ct);
                if (response.IsSuccessStatusCode)
                {
                    byte[] responseBytes = await response.Content.ReadAsByteArrayAsync(ct);
                    if (responseBytes.Length >= 12)
                    {
                        // Cache response for 60 seconds
                        _cache[cacheKey] = (responseBytes, DateTime.UtcNow.AddSeconds(60));

                        // Ensure transaction ID matches query
                        responseBytes[0] = queryBuffer[0];
                        responseBytes[1] = queryBuffer[1];
                        return responseBytes;
                    }
                }
            }
            catch
            {
                // Try next upstream
            }
        }

        // Return Server Failure (SERVFAIL) response on lookup failure
        return BuildServFailResponse(queryBuffer);
    }

    private static byte[] BuildServFailResponse(byte[] queryBuffer)
    {
        byte[] resp = (byte[])queryBuffer.Clone();
        if (resp.Length >= 4)
        {
            resp[2] = 0x81; // Response + Recursion Desired
            resp[3] = 0x82; // Server Failure (RCODE=2), Recursion Available
        }
        return resp;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _gate.Dispose();
    }
}

