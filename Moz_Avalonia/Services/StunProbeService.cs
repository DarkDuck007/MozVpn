using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using MozUtil.NatUtils;
using STUN;

namespace Moz_Avalonia.Services;

public partial class StunProbeResult : ObservableObject
{
    public StunProbeResult(string server, int timeoutMs)
    {
        Server = server;
        TimeoutMs = timeoutMs;
    }

    public string Server { get; }
    public int TimeoutMs { get; }

    [ObservableProperty] private string _status = "Scheduled";
    [ObservableProperty] private bool? _success;
    [ObservableProperty] private long? _latencyMs;
    [ObservableProperty] private string _natType = string.Empty;
    [ObservableProperty] private string _detail = "Waiting for an available batch slot";
}

public sealed class StunNatGroup
{
    public StunNatGroup(string natType, IReadOnlyList<StunProbeResult> results)
    {
        NatType = natType;
        Results = results;
    }

    public string NatType { get; }
    public IReadOnlyList<StunProbeResult> Results { get; }
    public int Count => Results.Count;
    public long FastestLatencyMs => Results.Where(x => x.LatencyMs.HasValue).Min(x => x.LatencyMs!.Value);
    public string Servers => string.Join(", ", Results.Select(x => x.Server));
}

public sealed record StunProbeOutcome(bool Success, long LatencyMs, string Detail, string NatType);

public sealed class StunProbeService
{
    public async Task<StunProbeOutcome> ProbeAsync(string server, int timeout, CancellationToken cancellationToken)
    {
        return await Task.Run(() =>
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                using var udp = new UdpClient();
                using var registration = cancellationToken.Register(udp.Dispose);
                cancellationToken.ThrowIfCancellationRequested();
                // PublicIP only discovers the mapped endpoint and deliberately leaves NATType
                // as Unspecified. Use the same ExactNAT query as the connection procedure so
                // consensus groups are based on an actual RFC 3489 NAT classification.
                var result = MozStun.GetStunResult(udp.Client, server, timeout);
                stopwatch.Stop();
                var success = result.QueryError == STUNQueryError.Success;
                var detail = success ? $"{result.PublicEndPoint} · {result.NATType}" : result.QueryError.ToString();
                return new StunProbeOutcome(success, stopwatch.ElapsedMilliseconds, detail,
                    success ? result.NATType.ToString() : string.Empty);
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            catch (SocketException) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                return new StunProbeOutcome(false, stopwatch.ElapsedMilliseconds, ex.Message, string.Empty);
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<StunProbeOutcome>> ProbeAsync(IEnumerable<string> servers, int timeout,
        int batchSize, CancellationToken cancellationToken = default)
    {
        var candidates = servers.Where(IsCandidate).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        using var gate = new SemaphoreSlim(Math.Clamp(batchSize, 1, 64));
        var tasks = candidates.Select(async server =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { return await ProbeAsync(server, timeout, cancellationToken).ConfigureAwait(false); }
            finally { gate.Release(); }
        });
        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    public static bool IsCandidate(string? server) =>
        !string.IsNullOrWhiteSpace(server) && !server.Equals("Auto", StringComparison.OrdinalIgnoreCase);
}
