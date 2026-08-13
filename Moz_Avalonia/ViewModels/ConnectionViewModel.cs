using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Moz_Avalonia.Models;
using Moz_Avalonia.Services;
using MozUtil;
using MozUtil.NatUtils;
using MozUtil.Types;

namespace Moz_Avalonia.ViewModels;

public partial class ConnectionViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly Func<Task<string>> _resolveStun;
    private readonly Func<ConnectionViewModel, Task> _clearSystemProxy;
    private readonly Func<bool> _isUiVisible;
    private readonly Action<string, string> _notify;
    private readonly ConcurrentQueue<string> _deferredLogs = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _telemetryTask;
    private MozManager? _manager;
    private ulong _lastBytesIn;
    private ulong _lastBytesOut;
    private DateTimeOffset? _connectedAt;
    private bool _serverStatsSubscribed;
    private bool _disconnecting;

    public ConnectionViewModel(ConnectionProfile profile, int ordinal, Func<Task<string>> resolveStun,
        Func<ConnectionViewModel, Task> clearSystemProxy, Func<bool> isUiVisible,
        Action<string, string> notify)
    {
        Profile = profile;
        if (!Guid.TryParseExact(Profile.BrowserProfileId, "N", out _))
            Profile.BrowserProfileId = Guid.NewGuid().ToString("N");
        _resolveStun = resolveStun;
        _clearSystemProxy = clearSystemProxy;
        _isUiVisible = isUiVisible;
        _notify = notify;
        Name = string.IsNullOrWhiteSpace(profile.Name) ? $"Connection {ordinal + 1}" : profile.Name;
        SocksPort = PortAllocator.FindAvailable(6375 + ordinal * 20);
        HttpPort = PortAllocator.FindAvailable(6385 + ordinal * 20);
        _telemetryTask = RunTelemetryLoopAsync(_lifetime.Token);
        RefreshVpnButtonText();
    }

    public ConnectionProfile Profile { get; }
    public string ServerAddress => Profile.ServerAddress;
    public string Transport => Profile.Transport;
    public int SocksPort { get; }
    public int HttpPort { get; }
    public string SocksAddress => IsConnected ? $"socks5://127.0.0.1:{SocksPort}" : "—";
    public string HttpAddress => IsConnected ? $"http://127.0.0.1:{HttpPort}" : "—";
    public string VpnButtonText => (App.VpnManager?.IsVpnRunning == true && App.VpnManager.ActiveProfileName == Name) ? "Stop VPN" : "VPN Mode";

    [ObservableProperty] private IBrush _vpnButtonBrush = Brush.Parse("#2962FF");

    public void RefreshVpnButtonText()
    {
        OnPropertyChanged(nameof(VpnButtonText));
        VpnButtonBrush = (App.VpnManager?.IsVpnRunning == true && App.VpnManager.ActiveProfileName == Name)
            ? Brushes.LimeGreen
            : Brush.Parse("#2962FF");
    }
    public IReadOnlyList<double> InboundHistory { get; } = new List<double>();
    public IReadOnlyList<double> OutboundHistory { get; } = new List<double>();
    public ObservableCollection<SubTunInfo> Relays { get; } = [];
    public ObservableCollection<StatItem> ServerStats { get; } = [];

    [ObservableProperty] private string _name;
    [ObservableProperty] private string _udpStatus = "Disconnected";
    [ObservableProperty] private string _httpStatus = "Disconnected";
    [ObservableProperty] private IBrush _statusBrush = Brushes.IndianRed;
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isSystemProxy;
    [ObservableProperty] private string _log = "MozVPN log";
    [ObservableProperty] private string _latency = "0 ms";
    [ObservableProperty] private IBrush _latencyBrush = Brushes.Gray;
    [ObservableProperty] private string _packetLoss = "0%";
    [ObservableProperty] private string _inboundRate = "0 B/s";
    [ObservableProperty] private string _outboundRate = "0 B/s";
    [ObservableProperty] private string _totalIn = "0 B";
    [ObservableProperty] private string _totalOut = "0 B";
    [ObservableProperty] private string _uptime = "00:00:00";
    [ObservableProperty] private int _activeConnections;
    [ObservableProperty] private int _channels;
    [ObservableProperty] private string _serverStatsStatus = "Not receiving";
    [ObservableProperty] private string _relayEndpoint = "engage.cloudflareclient.com:2408";
    [ObservableProperty] private string _relayListenPort = "6445";
    [ObservableProperty] private string _errorMessage = string.Empty;
    [ObservableProperty] private int _graphVersion;

    partial void OnIsConnectedChanged(bool value)
    {
        OnPropertyChanged(nameof(SocksAddress));
        OnPropertyChanged(nameof(HttpAddress));
        ConnectCommand.NotifyCanExecuteChanged();
        DisconnectCommand.NotifyCanExecuteChanged();
        CreateRelayCommand.NotifyCanExecuteChanged();
        ToggleServerStatsCommand.NotifyCanExecuteChanged();
    }

    private bool CanConnect() => !IsConnected && !IsBusy;
    private bool CanDisconnect() => IsConnected || IsBusy;

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        IsBusy = true;
        ErrorMessage = string.Empty;
        ConnectCommand.NotifyCanExecuteChanged();
        DisconnectCommand.NotifyCanExecuteChanged();
        try
        {
            var server = NormalizeServer(Profile.ServerAddress);
            var stun = Profile.StunServer.Equals("Auto", StringComparison.OrdinalIgnoreCase)
                ? await _resolveStun()
                : Profile.StunServer;
            var mode = Profile.Transport switch
            {
                "Unreliable" => TransportMode.Normal,
                "TCP" => TransportMode.TCP,
                "BALETUN" => TransportMode.BaleTun,
                _ => TransportMode.LiteNet
            };
            _manager = new MozManager(server, checked((byte)Profile.MaxChannels), stun, SocksPort, HttpPort, 10000,
                server.Equals("http://127.0.0.1:5209/", StringComparison.OrdinalIgnoreCase), mode,
                Profile.UseHttpProxy, NullIfEmpty(Profile.HttpProxy), Profile.ForceSymmetric, Profile.SkipStun);
            _manager.symmetricConnectionClientCount = Profile.AggressivePortScan ? 3000 : 100;
            _manager.NewLogArrived += OnLog;
            _manager.LatencyUpdated += OnLatency;
            _manager.StatusUpdated += OnStatus;
            _manager.SubTunCreated += OnSubTunCreated;
            var initiated = await Task.Run(_manager.InitiateConnection);
            if (!initiated)
            {
                ErrorMessage = "Connection initiation failed. See the log for details.";
                await DisconnectCoreAsync();
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            AppendLog(ex.ToString());
            await DisconnectCoreAsync();
        }
        finally
        {
            IsBusy = false;
            ConnectCommand.NotifyCanExecuteChanged();
            DisconnectCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private async Task DisconnectAsync() => await DisconnectCoreAsync();

    private async Task DisconnectCoreAsync()
    {
        if (_disconnecting) return;
        _disconnecting = true;
        try
        {
            if (IsSystemProxy)
            {
                try { await _clearSystemProxy(this); }
                catch (Exception ex) { AppendLog($"Could not clear system proxy: {ex.Message}"); }
            }
            if (_manager is not null)
            {
                if (_serverStatsSubscribed && _manager.MClient is not null)
                {
                    try { _manager.MClient.EnableServerStatusInformationStreamingForPeer(-1, -1); } catch { }
                    _manager.MClient.ServerStatusInformationUpdated -= OnServerStats;
                }
                _manager.Dispose();
                _manager = null;
            }
            _serverStatsSubscribed = false;
            IsConnected = false;
            IsSystemProxy = false;
            IsBusy = false;
            UdpStatus = "Disconnected";
            HttpStatus = "Disconnected";
            StatusBrush = Brushes.IndianRed;
            _connectedAt = null;
            _lastBytesIn = _lastBytesOut = 0;
            Latency = "0 ms";
            LatencyBrush = Brushes.Gray;
            InboundRate = "0 B/s";
            OutboundRate = "0 B/s";
            ActiveConnections = 0;
            Channels = 0;
            ServerStatsStatus = "Not receiving";
        }
        finally { _disconnecting = false; }
    }

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private void CreateRelay()
    {
        ErrorMessage = string.Empty;
        try
        {
            if (_manager?.MClient is null || !_manager.MClient.isRunning)
                throw new InvalidOperationException("An active reliable connection is required for UDP relays.");
            if (!TrySplitEndpoint(RelayEndpoint, out var host, out var destinationPort))
                throw new FormatException("Relay endpoint must be in host:port form (IPv6 literals may use [address]:port). ");
            if (!int.TryParse(RelayListenPort, out var localPort) || localPort is < 1 or > 65535)
                throw new FormatException("The local relay port must be between 1 and 65535.");
            if (Relays.Any(x => x.LocalPort == localPort))
                throw new InvalidOperationException("A relay already uses this local port.");
            Relays.Add(_manager.MClient.CreateNewUdpRelay(host, destinationPort, localPort));
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    [RelayCommand]
    private void ReconnectRelay(SubTunInfo? relay)
    {
        if (relay is null || relay.Status != TunStatus.Disconnected || _manager?.MClient is null)
            return;
        Relays.Remove(relay);
        Relays.Add(_manager.MClient.CreateNewUdpRelay(relay.DestinationHostName, relay.DestinationPort, relay.LocalPort));
    }

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private void ToggleServerStats()
    {
        try
        {
            if (_manager?.MClient is null)
                throw new InvalidOperationException("Server statistics require an active reliable connection.");
            if (_serverStatsSubscribed)
            {
                _manager.MClient.EnableServerStatusInformationStreamingForPeer(-1, -1);
                _manager.MClient.ServerStatusInformationUpdated -= OnServerStats;
                _serverStatsSubscribed = false;
                ServerStatsStatus = "Not receiving";
            }
            else
            {
                _manager.MClient.ServerStatusInformationUpdated += OnServerStats;
                if (!_manager.MClient.EnableServerStatusInformationStreamingForPeer())
                    throw new InvalidOperationException("The server statistics request could not be sent.");
                _serverStatsSubscribed = true;
                ServerStatsStatus = "Request sent…";
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    private void OnStatus(object? sender, StatusResult status) => Dispatcher.UIThread.Post(() =>
    {
        AppendLog(status.ToString());
        var wasConnected = IsConnected;
        switch (status)
        {
            case StatusResult.UDPConnected:
                IsConnected = true;
                UdpStatus = "Connected";
                StatusBrush = Brushes.LimeGreen;
                _connectedAt ??= DateTimeOffset.UtcNow;
                if (!wasConnected) _notify($"{Name} connected", "The UDP tunnel is ready.");
                App.VpnManager?.UpdateNotificationStatus("Connected");
                break;
            case StatusResult.UDPConnecting:
            case StatusResult.UDPReconnecting:
                UdpStatus = status == StatusResult.UDPReconnecting ? "Reconnecting…" : "Connecting…";
                StatusBrush = Brushes.Orange;
                if (status == StatusResult.UDPReconnecting)
                    _notify($"{Name} interrupted", "The tunnel is trying to reconnect.");
                App.VpnManager?.UpdateNotificationStatus(UdpStatus);
                break;
            case StatusResult.SendingStun: UdpStatus = "Testing STUN…"; StatusBrush = Brushes.Orange; break;
            case StatusResult.StunSuccess: UdpStatus = "STUN succeeded"; break;
            case StatusResult.StunFailed:
                UdpStatus = "STUN failed"; StatusBrush = Brushes.IndianRed;
                _notify($"{Name}: STUN failed", "The public endpoint could not be discovered.");
                App.VpnManager?.UpdateNotificationStatus("STUN Failed");
                break;
            case StatusResult.UDPError:
                UdpStatus = "Connection failed"; StatusBrush = Brushes.IndianRed;
                _notify($"{Name} failed", "The UDP tunnel encountered an error.");
                App.VpnManager?.UpdateNotificationStatus("Connection Error");
                break;
            case StatusResult.UDPDisconnected:
                UdpStatus = "Disconnected"; IsConnected = false; StatusBrush = Brushes.IndianRed;
                if (wasConnected) _notify($"{Name} disconnected", "The UDP tunnel is no longer connected.");
                App.VpnManager?.UpdateNotificationStatus("Disconnected");
                _ = DisconnectCoreAsync(); break;
            case StatusResult.HTTPConnected: HttpStatus = "Connected"; break;
            case StatusResult.HTTPConnecting: HttpStatus = "Connecting…"; break;
            case StatusResult.HTTPError: HttpStatus = "Failed"; break;
            case StatusResult.HTTPDisconnected: HttpStatus = "Disconnected"; break;
            case StatusResult.InternalServerStarted: HttpStatus = "Proxy ready"; break;
            case StatusResult.InternalServerStopped: HttpStatus = "Proxy stopped"; break;
        }
    });

    private void OnLatency(object? sender, int latency)
    {
        if (!_isUiVisible()) return;
        Dispatcher.UIThread.Post(() =>
        {
        var roundTrip = latency * 2;
        Latency = $"{roundTrip} ms";
        LatencyBrush = roundTrip > 250 ? Brushes.Red
            : roundTrip > 175 ? Brushes.Orange
            : roundTrip > 100 ? Brushes.Yellow
            : Brushes.Lime;
        if (_manager?.LiteNetStats is not null)
            PacketLoss = $"{_manager.LiteNetStats.PacketLossPercent:0.##}%";
        });
    }

    private void OnLog(object? sender, string message) => Dispatcher.UIThread.Post(() => AppendLog(message));
    private void OnSubTunCreated(object? sender, SubTunInfo relay) => Dispatcher.UIThread.Post(() =>
    {
        if (!Relays.Contains(relay)) Relays.Add(relay);
    });

    private void OnServerStats(object? sender, ServerStatusInformation data)
    {
        if (!_isUiVisible()) return;
        Dispatcher.UIThread.Post(() =>
        {
        ServerStatsStatus = data.Uptime == -1 ? "Rejected" : "Enabled";
        foreach (var property in typeof(ServerStatusInformation).GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            var raw = property.GetValue(data);
            var value = property.Name == nameof(ServerStatusInformation.Uptime) && raw is long ticks && ticks >= 0
                ? TimeSpan.FromTicks(ticks).ToString()
                : raw?.ToString() ?? "null";
            var item = ServerStats.FirstOrDefault(x => x.Name == property.Name);
            if (item is null) ServerStats.Add(new StatItem(property.Name, value, raw));
            else item.Update(value, raw);
        }
        });
    }

    private async Task RunTelemetryLoopAsync(CancellationToken token)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                if (!IsConnected || _manager?.LiteNetStats is null) continue;
                var bytesIn = (ulong)_manager.LiteNetStats.BytesReceived;
                var bytesOut = (ulong)_manager.LiteNetStats.BytesSent;
                var deltaIn = bytesIn >= _lastBytesIn ? bytesIn - _lastBytesIn : 0;
                var deltaOut = bytesOut >= _lastBytesOut ? bytesOut - _lastBytesOut : 0;
                _lastBytesIn = bytesIn;
                _lastBytesOut = bytesOut;
                if (!_isUiVisible()) continue;
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    InboundRate = MozStatic.HumanReadable(deltaIn) + "/s";
                    OutboundRate = MozStatic.HumanReadable(deltaOut) + "/s";
                    TotalIn = MozStatic.HumanReadable(bytesIn);
                    TotalOut = MozStatic.HumanReadable(bytesOut);
                    Uptime = (_connectedAt is null ? TimeSpan.Zero : DateTimeOffset.UtcNow - _connectedAt.Value).ToString(@"dd\.hh\:mm\:ss");
                    ActiveConnections = Safe(() => _manager.TotalConnections);
                    Channels = Safe(() => _manager.TotalChannels);
                    AddSample((List<double>)InboundHistory, deltaIn);
                    AddSample((List<double>)OutboundHistory, deltaOut);
                    GraphVersion++;
                });
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private void AppendLog(string message)
    {
        if (!_isUiVisible())
        {
            _deferredLogs.Enqueue(message);
            while (_deferredLogs.Count > 4096) _deferredLogs.TryDequeue(out _);
            return;
        }
        Log += Environment.NewLine + message;
        if (Log.Length > 256 * 1024) Log = Log[^192_000..];
    }

    public void UpdateProfileMetadata(string name, bool autoConnectAtLaunch)
    {
        Profile.Name = name;
        Profile.AutoConnectAtLaunch = autoConnectAtLaunch;
        Name = name;
    }

    public void ResumeUiUpdates()
    {
        Dispatcher.UIThread.Post(() =>
        {
            while (_deferredLogs.TryDequeue(out var message)) AppendLog(message);
            GraphVersion++;
        });
    }

    private static void AddSample(List<double> samples, double value)
    {
        samples.Add(value);
        if (samples.Count > 90) samples.RemoveAt(0);
    }

    private static int Safe(Func<int> getValue) { try { return getValue(); } catch { return 0; } }
    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string NormalizeServer(string value) => value.Trim().TrimEnd('/') + "/";

    private static bool TrySplitEndpoint(string endpoint, out string host, out ushort port)
    {
        host = string.Empty;
        port = 0;
        var trimmed = endpoint.Trim();
        var split = trimmed.LastIndexOf(':');
        if (split < 1 || !ushort.TryParse(trimmed[(split + 1)..], out port) || port == 0) return false;
        host = trimmed[..split].Trim('[', ']');
        return host.Length > 0;
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        await DisconnectCoreAsync();
        await _telemetryTask;
        PortAllocator.Release(SocksPort);
        PortAllocator.Release(HttpPort);
        _lifetime.Dispose();
    }
}
