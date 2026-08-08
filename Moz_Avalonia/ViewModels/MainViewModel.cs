using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Moz_Avalonia.Models;
using Moz_Avalonia.Services;

namespace Moz_Avalonia.ViewModels;

public partial class MainViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly SettingsStore _settingsStore = new();
    private readonly StunProbeService _stunProbe = new();
    private readonly DesktopIntegrationService _desktop = new();
    private readonly DesktopNotificationService _notifications = new();
    private AppSettings _settings = new();
    private bool _initialized;
    private bool _lastDraftSaveSucceeded;
    private CancellationTokenSource? _stunTestCts;
    private Task? _stunTestTask;

    public Func<string, string, string, Task<bool>> ConfirmAsync { get; set; } =
        (_, _, _) => Task.FromResult(true);
    public Func<IReadOnlyList<StunNatGroup>, Task<StunNatGroup?>> ChooseStunNatGroupAsync { get; set; } =
        groups => Task.FromResult<StunNatGroup?>(groups.OrderByDescending(x => x.Count).FirstOrDefault());

    public MainViewModel()
    {
        foreach (var server in ResourceCatalog.Load("ServerList.txt")) ServerOptions.Add(server);
        foreach (var server in ResourceCatalog.Load("StunList.txt")) StunOptions.Add(server);
        foreach (var channel in Enumerable.Range(1, 64)) ChannelOptions.Add(channel);
        foreach (var browser in _desktop.FindBrowsers()) Browsers.Add(browser);
    }

    public ObservableCollection<string> ServerOptions { get; } = [];
    public ObservableCollection<string> StunOptions { get; } = [];
    public ObservableCollection<string> HttpProxyOptions { get; } = [];
    public ObservableCollection<string> TransportOptions { get; } = ["Reliable", "Unreliable", "TCP", "BALETUN"];
    public ObservableCollection<int> ChannelOptions { get; } = [];
    public ObservableCollection<ConnectionViewModel> Connections { get; } = [];
    public ObservableCollection<StunProbeResult> StunResults { get; } = [];
    public ObservableCollection<BrowserInfo> Browsers { get; } = [];

    public string SettingsPath => _settingsStore.Path;
    public bool IsWindows => OperatingSystem.IsWindows();
    public bool IsUiVisible { get; private set; } = true;
    public bool HasConnections => Connections.Count > 0;
    public bool IsEditing => EditingConnection is not null;
    public string EditorHeader => IsEditing ? $"Edit {EditingConnection!.Name}" : "New connection";
    public string SaveButtonText => IsEditing ? "Save" : "Add";
    public string SaveAndConnectButtonText => IsEditing ? "Save & connect" : "Add & connect";
    public double ConnectionRailWidth => IsEditorOpen ? 190 : 350;
    public string EditorToggleText => IsEditorOpen ? "‹ Close editor" : "＋ New connection";

    [ObservableProperty] private ConnectionViewModel? _selectedConnection;
    [ObservableProperty] private ConnectionViewModel? _editingConnection;
    [ObservableProperty] private string _profileName = "Connection";
    [ObservableProperty] private string _serverAddress = "https://noisy-tree-58ff.topolly84.workers.dev/";
    [ObservableProperty] private string _stunServer = "Auto";
    [ObservableProperty] private string _httpProxy = string.Empty;
    [ObservableProperty] private string _selectedTransport = "Reliable";
    [ObservableProperty] private int _maxChannels = 32;
    [ObservableProperty] private bool _useHttpProxy;
    [ObservableProperty] private bool _forceSymmetric;
    [ObservableProperty] private bool _aggressivePortScan;
    [ObservableProperty] private bool _skipStun;
    [ObservableProperty] private bool _autoConnectProfile;
    [ObservableProperty] private string? _selectedServerOption;
    [ObservableProperty] private string? _selectedStunOption;
    [ObservableProperty] private string? _selectedHttpProxyOption;
    [ObservableProperty] private BrowserInfo? _selectedBrowser;
    [ObservableProperty] private string _browserUrl = "https://browserleaks.com/ip";
    [ObservableProperty] private string _statusMessage = "Ready";
    [ObservableProperty] private bool _isTestingStun;
    [ObservableProperty] private bool _isEditorOpen = true;
    [ObservableProperty] private int _stunTestBatchSize = 12;
    [ObservableProperty] private int _stunTestTimeoutMs = 1800;
    [ObservableProperty] private string _stunTestSummary = "Ready to test all configured STUN servers.";

    partial void OnSelectedServerOptionChanged(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        ServerAddress = value;
        if (_initialized && EditingConnection is null)
            _settings.ServerAddress = NormalizeServer(value);
    }

    partial void OnSelectedStunOptionChanged(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        StunServer = value;
        if (_initialized && EditingConnection is null)
            _settings.StunServer = value.Trim();
    }
    partial void OnSelectedHttpProxyOptionChanged(string? value) { if (!string.IsNullOrWhiteSpace(value)) HttpProxy = value; }
    partial void OnEditingConnectionChanged(ConnectionViewModel? value)
    {
        OnPropertyChanged(nameof(IsEditing));
        OnPropertyChanged(nameof(EditorHeader));
        OnPropertyChanged(nameof(SaveButtonText));
        OnPropertyChanged(nameof(SaveAndConnectButtonText));
    }

    partial void OnIsEditorOpenChanged(bool value)
    {
        OnPropertyChanged(nameof(ConnectionRailWidth));
        OnPropertyChanged(nameof(EditorToggleText));
    }

    public async Task InitializeAsync()
    {
        _settings = await _settingsStore.LoadAsync();
        ServerAddress = _settings.ServerAddress;
        StunServer = _settings.StunServer;
        HttpProxy = _settings.HttpProxy;
        SelectedTransport = TransportOptions.Contains(_settings.Transport) ? _settings.Transport : "Reliable";
        MaxChannels = Math.Clamp(_settings.MaxChannels, 1, 64);
        UseHttpProxy = _settings.UseHttpProxy;
        ForceSymmetric = _settings.ForceSymmetric;
        AggressivePortScan = _settings.AggressivePortScan;
        SkipStun = _settings.SkipStun;
        IsEditorOpen = _settings.IsEditorOpen;
        StunTestBatchSize = Math.Clamp(_settings.StunTestBatchSize, 1, 64);
        StunTestTimeoutMs = Math.Clamp(_settings.StunTestTimeoutMs, 250, 30000);
        MergeOptions(ServerOptions, _settings.CustomServers);
        MergeOptions(StunOptions, _settings.CustomStunServers);
        MergeOptions(HttpProxyOptions, _settings.CustomHttpProxies);
        SynchronizeServerSelections();
        SelectedBrowser = Browsers.FirstOrDefault(x => x.Executable == _settings.PreferredBrowser) ?? Browsers.FirstOrDefault();

        var migratedGlobalStartup = _settings.AutoConnectSavedProfiles;
        if (migratedGlobalStartup)
        {
            foreach (var profile in _settings.SavedProfiles) profile.AutoConnectAtLaunch = true;
            _settings.AutoConnectSavedProfiles = false;
        }

        foreach (var profile in _settings.SavedProfiles)
            AddProfile(profile);
        _initialized = true;
        if (migratedGlobalStartup) await SaveAsync();
        var startupProfiles = Connections.Where(connection => connection.Profile.AutoConnectAtLaunch).ToArray();
        if (startupProfiles.Length > 0)
            await Task.WhenAll(startupProfiles.Select(connection => connection.ConnectCommand.ExecuteAsync(null)));
        StatusMessage = Connections.Count == 0 ? "Create a connection profile to begin." : $"Restored {Connections.Count} connection profile(s).";
    }

    [RelayCommand]
    private async Task AddConnectionAsync()
    {
        _lastDraftSaveSucceeded = false;
        if (!ValidateDraft(out var error))
        {
            StatusMessage = error;
            return;
        }
        var profile = CreateDraftProfile();
        if (EditingConnection is null)
        {
            SelectedConnection = AddProfile(profile);
            StatusMessage = $"Added {profile.Name}. It can remain active while you use another connection.";
        }
        else
        {
            if (HasSameConnectionSettings(EditingConnection.Profile, profile))
            {
                EditingConnection.UpdateProfileMetadata(profile.Name, profile.AutoConnectAtLaunch);
                SelectedConnection = EditingConnection;
                StatusMessage = $"Saved profile options for {profile.Name} without interrupting its connection.";
            }
            else
            {
                SelectedConnection = await ReplaceProfileAsync(EditingConnection, profile);
                StatusMessage = $"Saved connection changes to {profile.Name}.";
            }
            EditingConnection = null;
        }
        IsEditorOpen = false;
        await SaveAsync();
        _lastDraftSaveSucceeded = true;
    }

    [RelayCommand]
    private async Task AddAndConnectAsync()
    {
        await AddConnectionAsync();
        if (_lastDraftSaveSucceeded && SelectedConnection is not null && !SelectedConnection.IsConnected)
            await SelectedConnection.ConnectCommand.ExecuteAsync(null);
    }

    [RelayCommand]
    private async Task RemoveConnectionAsync(ConnectionViewModel? connection)
    {
        if (connection is null) return;
        var name = connection.Name;
        if (!await ConfirmAsync("Delete connection?",
                $"Delete the saved profile “{name}”? This cannot be undone.", "Delete"))
            return;
        if (EditingConnection == connection) EditingConnection = null;
        await connection.DisposeAsync();
        Connections.Remove(connection);
        if (SelectedConnection == connection) SelectedConnection = Connections.FirstOrDefault();
        OnPropertyChanged(nameof(HasConnections));
        await SaveAsync();
        StatusMessage = $"Removed {name}.";
    }

    [RelayCommand]
    private async Task EditConnectionAsync(ConnectionViewModel? connection)
    {
        if (connection is null) return;
        if (EditingConnection is not null && EditingConnection != connection &&
            !await ConfirmAsync("Discard changes?",
                $"Discard the unsaved changes to “{EditingConnection.Name}”?", "Discard"))
            return;
        EditingConnection = connection;
        IsEditorOpen = true;
        SelectedConnection = connection;
        var profile = connection.Profile;
        ProfileName = profile.Name;
        ServerAddress = profile.ServerAddress;
        StunServer = profile.StunServer;
        SynchronizeServerSelections();
        HttpProxy = profile.HttpProxy;
        SelectedTransport = profile.Transport;
        MaxChannels = profile.MaxChannels;
        UseHttpProxy = profile.UseHttpProxy;
        ForceSymmetric = profile.ForceSymmetric;
        AggressivePortScan = profile.AggressivePortScan;
        SkipStun = profile.SkipStun;
        AutoConnectProfile = profile.AutoConnectAtLaunch;
        StatusMessage = connection.IsConnected
            ? "Name and startup changes are applied without interruption. Connection-setting changes restart the profile; use Save & connect to reconnect it."
            : $"Editing {connection.Name}.";
        await PersistEditorStateAsync();
    }

    [RelayCommand]
    private async Task CancelEditAsync()
    {
        EditingConnection = null;
        RestoreNewConnectionDraft();
        IsEditorOpen = false;
        await PersistEditorStateAsync();
        StatusMessage = "Edit cancelled.";
    }

    [RelayCommand]
    private async Task ToggleEditorAsync()
    {
        if (IsEditorOpen)
        {
            if (EditingConnection is not null &&
                !await ConfirmAsync("Discard changes?",
                    $"Discard the unsaved changes to “{EditingConnection.Name}”?", "Discard"))
                return;

            EditingConnection = null;
            RestoreNewConnectionDraft();
            IsEditorOpen = false;
            await PersistEditorStateAsync();
            return;
        }

        EditingConnection = null;
        RestoreNewConnectionDraft();
        IsEditorOpen = true;
        await PersistEditorStateAsync();
    }

    [RelayCommand]
    private async Task UseAsSystemProxyAsync(ConnectionViewModel? connection)
    {
        if (!IsWindows)
        {
            StatusMessage = "Automatic system proxy configuration is available only on Windows.";
            return;
        }
        if (connection is null || !connection.IsConnected)
        {
            StatusMessage = "Connect that profile before using its system proxy.";
            return;
        }
        try
        {
            if (connection.IsSystemProxy)
            {
                StatusMessage = await _desktop.ClearSystemProxyAsync();
                connection.IsSystemProxy = false;
                return;
            }
            StatusMessage = await _desktop.SetSystemProxyAsync(connection.HttpPort, connection.SocksPort);
            foreach (var item in Connections) item.IsSystemProxy = item == connection;
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    [RelayCommand]
    private void LaunchBrowser(ConnectionViewModel? connection)
    {
        if (connection is null || !connection.IsConnected)
        {
            StatusMessage = "Connect that profile before launching a proxied browser.";
            return;
        }
        if (SelectedBrowser is null)
        {
            StatusMessage = "No Chromium-family browser was detected. Install Chromium, Chrome, Brave, Edge, or Vivaldi.";
            return;
        }
        try
        {
            StatusMessage = _desktop.LaunchBrowser(SelectedBrowser, connection.HttpPort,
                connection.Profile.BrowserProfileId, BrowserUrl);
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    [RelayCommand]
    private async Task CopyProxyAsync(ConnectionViewModel? connection)
    {
        if (connection is null || !connection.IsConnected) return;
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow.Clipboard: not null } desktop)
        {
            await desktop.MainWindow.Clipboard.SetTextAsync($"{connection.HttpAddress}{Environment.NewLine}{connection.SocksAddress}");
            StatusMessage = "Proxy addresses copied to the clipboard.";
        }
    }

    [RelayCommand(CanExecute = nameof(CanStartStunTests))]
    private async Task StartStunTestsAsync()
    {
        if (IsTestingStun) return;
        _stunTestCts?.Dispose();
        _stunTestCts = new CancellationTokenSource();
        var token = _stunTestCts.Token;
        var candidates = StunOptions.Where(StunProbeService.IsCandidate)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var timeout = Math.Clamp(StunTestTimeoutMs, 250, 30000);
        var batchSize = Math.Clamp(StunTestBatchSize, 1, 64);

        IsTestingStun = true;
        NotifyStunCommandStates();
        StunResults.Clear();
        foreach (var server in candidates) StunResults.Add(new StunProbeResult(server, timeout));
        StunTestSummary = $"0 / {candidates.Length} completed · batch size {batchSize}";
        StatusMessage = $"Testing all {candidates.Length} configured STUN servers…";
        try
        {
            using var gate = new SemaphoreSlim(batchSize);
            var completed = 0;
            var tasks = StunResults.Select(async item =>
            {
                await gate.WaitAsync(token);
                try
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        item.Status = "Sending / waiting";
                        item.Detail = $"Waiting up to {timeout} ms for a binding response";
                    });
                    var outcome = await _stunProbe.ProbeAsync(item.Server, timeout, token);
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        item.Success = outcome.Success;
                        item.LatencyMs = outcome.LatencyMs;
                        item.NatType = outcome.NatType;
                        item.Status = outcome.Success ? "Succeeded" :
                            outcome.LatencyMs >= timeout ? "Timed out" : "Failed";
                        item.Detail = outcome.Detail;
                        completed++;
                        StunTestSummary = $"{completed} / {candidates.Length} completed · " +
                                          $"{StunResults.Count(x => x.Success == true)} responding";
                    });
                }
                finally { gate.Release(); }
            }).ToArray();
            _stunTestTask = Task.WhenAll(tasks);
            await _stunTestTask;

            await SelectFastestStunResultAsync(false);
        }
        catch (OperationCanceledException)
        {
            foreach (var result in StunResults.Where(x => x.Success is null))
            {
                result.Status = "Cancelled";
                result.Detail = "Test stopped by user";
            }
            await SelectFastestStunResultAsync(true);
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
        finally
        {
            IsTestingStun = false;
            _stunTestTask = null;
            NotifyStunCommandStates();
        }
    }

    private bool CanStartStunTests() => !IsTestingStun;

    [RelayCommand]
    private async Task RestartStunTestsAsync()
    {
        await StopStunTestsAsync();
        await StartStunTestsAsync();
    }

    private bool CanStopStunTests() => IsTestingStun;

    [RelayCommand(CanExecute = nameof(CanStopStunTests))]
    private async Task StopStunTestsAsync()
    {
        if (!IsTestingStun || _stunTestCts is null) return;
        _stunTestCts.Cancel();
        if (_stunTestTask is not null)
            try { await _stunTestTask; } catch (OperationCanceledException) { }
    }

    private void NotifyStunCommandStates()
    {
        StartStunTestsCommand.NotifyCanExecuteChanged();
        StopStunTestsCommand.NotifyCanExecuteChanged();
        RestartStunTestsCommand.NotifyCanExecuteChanged();
    }

    private async Task SelectFastestStunResultAsync(bool partialRun)
    {
        var successful = StunResults.Where(x => x.Success == true && x.LatencyMs.HasValue).ToArray();
        if (successful.Length == 0)
        {
            StunTestSummary = partialRun ? "Testing stopped; no completed probe succeeded." : "No server responded.";
            StatusMessage = partialRun ? "STUN testing stopped before a server responded." : "No tested STUN server responded.";
            return;
        }

        var groups = successful.GroupBy(x => string.IsNullOrWhiteSpace(x.NatType) ? "Unknown" : x.NatType)
            .Select(x => new StunNatGroup(x.Key, x.OrderBy(result => result.LatencyMs).ToArray()))
            .OrderByDescending(x => x.Count).ThenBy(x => x.FastestLatencyMs).ToArray();
        var trustedGroup = groups.Length == 1 ? groups[0] : await ChooseStunNatGroupAsync(groups);
        if (trustedGroup is null)
        {
            StunTestSummary = "NAT results disagree; no group was selected.";
            StatusMessage = "STUN results were not applied because no NAT type was selected.";
            return;
        }

        var winner = trustedGroup.Results.OrderBy(x => x.LatencyMs).First();

        StunServer = winner.Server;
        SelectedStunOption = winner.Server;
        _settings.StunServer = winner.Server;
        if (_initialized) await _settingsStore.SaveAsync(_settings);
        var prefix = partialRun ? "Testing stopped · fastest completed result" : "Fastest result";
        StunTestSummary = $"{prefix}: {winner.Server} ({winner.LatencyMs} ms) · " +
                          $"trusted NAT: {trustedGroup.NatType} ({trustedGroup.Count} server(s))";
        StatusMessage = $"Selected {winner.Server} ({winner.LatencyMs} ms)" +
                        (partialRun ? " from the completed STUN tests." : ".");
    }

    [RelayCommand]
    private async Task AddCustomServerAsync()
    {
        if (!Uri.TryCreate(NormalizeServer(ServerAddress), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            StatusMessage = "Enter a valid HTTP or HTTPS Moz server URL.";
            return;
        }
        ServerAddress = uri.AbsoluteUri;
        AddUnique(ServerOptions, ServerAddress);
        await SaveAsync();
    }

    [RelayCommand]
    private async Task AddCustomStunAsync()
    {
        if (!LooksLikeEndpoint(StunServer)) { StatusMessage = "Enter a STUN server as host:port."; return; }
        AddUnique(StunOptions, StunServer.Trim());
        await SaveAsync();
    }

    [RelayCommand]
    private async Task AddCustomHttpProxyAsync()
    {
        if (string.IsNullOrWhiteSpace(HttpProxy)) { StatusMessage = "Enter an HTTP proxy address."; return; }
        AddUnique(HttpProxyOptions, HttpProxy.Trim());
        await SaveAsync();
    }

    public async Task<string> ResolveAutoStunAsync()
    {
        StatusMessage = "Auto mode is testing STUN servers…";
        var candidates = StunOptions.Where(StunProbeService.IsCandidate).ToArray();
        var results = await _stunProbe.ProbeAsync(candidates, Math.Clamp(StunTestTimeoutMs, 250, 30000),
            Math.Clamp(StunTestBatchSize, 1, 64));
        var winner = candidates.Zip(results, (server, outcome) => new { Server = server, Outcome = outcome })
            .Where(x => x.Outcome.Success).OrderBy(x => x.Outcome.LatencyMs).FirstOrDefault()
            ?? throw new InvalidOperationException("Auto STUN could not find a responding server. Add or select a STUN server manually.");
        StatusMessage = $"Auto STUN chose {winner.Server} at {winner.Outcome.LatencyMs} ms.";
        return winner.Server;
    }

    public async Task SaveAsync()
    {
        if (!_initialized) return;
        _settings.ServerAddress = ServerAddress;
        _settings.StunServer = StunServer;
        _settings.HttpProxy = HttpProxy;
        _settings.Transport = SelectedTransport;
        _settings.MaxChannels = MaxChannels;
        _settings.UseHttpProxy = UseHttpProxy;
        _settings.ForceSymmetric = ForceSymmetric;
        _settings.AggressivePortScan = AggressivePortScan;
        _settings.SkipStun = SkipStun;
        _settings.AutoConnectSavedProfiles = false;
        _settings.IsEditorOpen = IsEditorOpen;
        _settings.StunTestBatchSize = Math.Clamp(StunTestBatchSize, 1, 64);
        _settings.StunTestTimeoutMs = Math.Clamp(StunTestTimeoutMs, 250, 30000);
        _settings.PreferredBrowser = SelectedBrowser?.Executable ?? string.Empty;
        _settings.CustomServers = ServerOptions.Except(ResourceCatalog.Load("ServerList.txt")).ToList();
        _settings.CustomStunServers = StunOptions.Except(ResourceCatalog.Load("StunList.txt")).ToList();
        _settings.CustomHttpProxies = HttpProxyOptions.ToList();
        _settings.SavedProfiles = Connections.Select(x => x.Profile).ToList();
        await _settingsStore.SaveAsync(_settings);
    }

    private async Task PersistEditorStateAsync()
    {
        if (!_initialized) return;
        _settings.IsEditorOpen = IsEditorOpen;
        await _settingsStore.SaveAsync(_settings);
    }

    private ConnectionViewModel AddProfile(ConnectionProfile profile)
    {
        var connection = new ConnectionViewModel(profile, Connections.Count, ResolveAutoStunAsync,
            ClearSystemProxyForConnectionAsync, () => IsUiVisible, NotifyConnectionStatus);
        Connections.Add(connection);
        SelectedConnection ??= connection;
        OnPropertyChanged(nameof(HasConnections));
        return connection;
    }

    private async Task<ConnectionViewModel> ReplaceProfileAsync(ConnectionViewModel oldConnection, ConnectionProfile profile)
    {
        var index = Connections.IndexOf(oldConnection);
        if (index < 0) return AddProfile(profile);
        await oldConnection.DisposeAsync();
        var replacement = new ConnectionViewModel(profile, index, ResolveAutoStunAsync,
            ClearSystemProxyForConnectionAsync, () => IsUiVisible, NotifyConnectionStatus);
        Connections[index] = replacement;
        return replacement;
    }

    private async Task ClearSystemProxyForConnectionAsync(ConnectionViewModel connection)
    {
        if (!connection.IsSystemProxy) return;
        StatusMessage = await _desktop.ClearSystemProxyAsync();
        foreach (var item in Connections) item.IsSystemProxy = false;
    }

    public void SetUiVisible(bool visible)
    {
        if (IsUiVisible == visible) return;
        IsUiVisible = visible;
        if (visible)
            foreach (var connection in Connections) connection.ResumeUiUpdates();
    }

    public void NotifyBackgroundMode() =>
        _notifications.Show("Moz VPN is still running", "Active connections will continue in the background.");

    private void NotifyConnectionStatus(string title, string message)
    {
        if (!IsUiVisible) _notifications.Show(title, message);
    }

    private ConnectionProfile CreateDraftProfile() => new()
    {
        BrowserProfileId = EditingConnection?.Profile.BrowserProfileId ?? Guid.NewGuid().ToString("N"),
        Name = string.IsNullOrWhiteSpace(ProfileName) ? $"Connection {Connections.Count + 1}" : ProfileName.Trim(),
        ServerAddress = NormalizeServer(ServerAddress), StunServer = StunServer.Trim(), HttpProxy = HttpProxy.Trim(),
        Transport = SelectedTransport, MaxChannels = Math.Clamp(MaxChannels, 1, 64), UseHttpProxy = UseHttpProxy,
        ForceSymmetric = ForceSymmetric, AggressivePortScan = AggressivePortScan, SkipStun = SkipStun,
        AutoConnectAtLaunch = AutoConnectProfile
    };

    private static bool HasSameConnectionSettings(ConnectionProfile current, ConnectionProfile draft) =>
        current.ServerAddress.Equals(draft.ServerAddress, StringComparison.OrdinalIgnoreCase) &&
        current.StunServer.Equals(draft.StunServer, StringComparison.OrdinalIgnoreCase) &&
        current.HttpProxy.Equals(draft.HttpProxy, StringComparison.Ordinal) &&
        current.Transport.Equals(draft.Transport, StringComparison.Ordinal) &&
        current.MaxChannels == draft.MaxChannels &&
        current.UseHttpProxy == draft.UseHttpProxy &&
        current.ForceSymmetric == draft.ForceSymmetric &&
        current.AggressivePortScan == draft.AggressivePortScan &&
        current.SkipStun == draft.SkipStun;

    private void RestoreNewConnectionDraft()
    {
        ProfileName = "Connection";
        ServerAddress = _settings.ServerAddress;
        StunServer = _settings.StunServer;
        SynchronizeServerSelections();
        HttpProxy = _settings.HttpProxy;
        SelectedTransport = TransportOptions.Contains(_settings.Transport) ? _settings.Transport : "Reliable";
        MaxChannels = Math.Clamp(_settings.MaxChannels, 1, 64);
        UseHttpProxy = _settings.UseHttpProxy;
        ForceSymmetric = _settings.ForceSymmetric;
        AggressivePortScan = _settings.AggressivePortScan;
        SkipStun = _settings.SkipStun;
        AutoConnectProfile = false;
    }

    private bool ValidateDraft(out string error)
    {
        if (!Uri.TryCreate(NormalizeServer(ServerAddress), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            error = "Enter a valid Moz server URL.";
        else if (!StunServer.Equals("Auto", StringComparison.OrdinalIgnoreCase) && !LooksLikeEndpoint(StunServer))
            error = "Select Auto or enter a STUN server as host:port.";
        else if (UseHttpProxy && string.IsNullOrWhiteSpace(HttpProxy))
            error = "Enter the HTTP proxy used to initiate the connection.";
        else { error = string.Empty; return true; }
        return false;
    }

    private static string NormalizeServer(string value) => value.Trim().TrimEnd('/') + "/";
    private void SynchronizeServerSelections()
    {
        SelectedServerOption = ServerOptions.FirstOrDefault(value =>
            NormalizeServer(value).Equals(NormalizeServer(ServerAddress), StringComparison.OrdinalIgnoreCase));
        SelectedStunOption = StunOptions.FirstOrDefault(value =>
            value.Equals(StunServer, StringComparison.OrdinalIgnoreCase));
    }

    private static bool LooksLikeEndpoint(string value) => value.Trim().LastIndexOf(':') > 0 && ushort.TryParse(value[(value.LastIndexOf(':') + 1)..], out var port) && port > 0;
    private static void AddUnique(ObservableCollection<string> target, string value) { if (!target.Contains(value, StringComparer.OrdinalIgnoreCase)) target.Add(value); }
    private static void MergeOptions(ObservableCollection<string> target, IEnumerable<string> values) { foreach (var value in values) AddUnique(target, value); }

    public async ValueTask DisposeAsync()
    {
        _stunTestCts?.Cancel();
        if (_stunTestTask is not null)
            try { await _stunTestTask; } catch (OperationCanceledException) { }
        _stunTestCts?.Dispose();
        await SaveAsync();
        foreach (var connection in Connections.ToArray()) await connection.DisposeAsync();
    }
}
