using System;
using System.Collections.Generic;

namespace Moz_Avalonia.Models;

public sealed class AppSettings
{
    public string ServerAddress { get; set; } = "https://noisy-tree-58ff.topolly84.workers.dev/";
    public string StunServer { get; set; } = "Auto";
    public string HttpProxy { get; set; } = string.Empty;
    public string Transport { get; set; } = "Reliable";
    public int MaxChannels { get; set; } = 32;
    public string RoamSeed { get; set; } = string.Empty;
    public int RoamPortCount { get; set; } = 10;
    public int RoamIntervalSeconds { get; set; } = 10;
    public bool UseHttpProxy { get; set; }
    public bool ForceSymmetric { get; set; }
    public bool AggressivePortScan { get; set; }
    public bool SkipStun { get; set; }
    // Legacy global setting retained only for one-time migration to per-profile startup.
    public bool AutoConnectSavedProfiles { get; set; }
    public bool IsEditorOpen { get; set; } = true;
    public int StunTestBatchSize { get; set; } = 12;
    public int StunTestTimeoutMs { get; set; } = 1800;
    public string PreferredBrowser { get; set; } = string.Empty;
    public List<string> CustomServers { get; set; } = [];
    public List<string> CustomStunServers { get; set; } = [];
    public List<string> CustomHttpProxies { get; set; } = [];
    public List<ConnectionProfile> SavedProfiles { get; set; } = [];
    public List<string> SuccessfulStuns { get; set; } = [];
    public bool? IsSimpleMode { get; set; }
}

public sealed class ConnectionProfile
{
    public string BrowserProfileId { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Connection";
    public string ServerAddress { get; set; } = string.Empty;
    public string StunServer { get; set; } = "Auto";
    public string HttpProxy { get; set; } = string.Empty;
    public string Transport { get; set; } = "Reliable";
    public int MaxChannels { get; set; } = 32;
    public string RoamSeed { get; set; } = string.Empty;
    public int RoamPortCount { get; set; } = 10;
    public int RoamIntervalSeconds { get; set; } = 10;
    public bool UseHttpProxy { get; set; }
    public bool ForceSymmetric { get; set; }
    public bool AggressivePortScan { get; set; }
    public bool SkipStun { get; set; }
    public bool AutoConnectAtLaunch { get; set; }
    public string? ServerAlias { get; set; }
    public string? StunSpoofLocalAddr { get; set; }
    public string? StunSpoofPublicAddr { get; set; }
    public string? StunSpoofNatType { get; set; }
}
