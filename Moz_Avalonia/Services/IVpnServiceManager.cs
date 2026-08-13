namespace Moz_Avalonia.Services;

public interface IVpnServiceManager
{
    bool IsVpnRunning { get; }
    string? ActiveProfileName { get; }
    int ActiveSocksPort { get; }
    void StartVpn(string profileName, int socksPort);
    void StopVpn();
    void UpdateNotificationStatus(string status);
}
