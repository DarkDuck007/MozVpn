namespace Moz_Avalonia.Services;

public class DesktopVpnServiceManager : IVpnServiceManager
{
    public bool IsVpnRunning { get; private set; }
    public string? ActiveProfileName { get; private set; }
    public int ActiveSocksPort { get; private set; }

    public void StartVpn(string profileName, int socksPort)
    {
        IsVpnRunning = true;
        ActiveProfileName = profileName;
        ActiveSocksPort = socksPort;
    }

    public void StopVpn()
    {
        IsVpnRunning = false;
        ActiveProfileName = null;
        ActiveSocksPort = 0;
    }

    public void UpdateNotificationStatus(string status)
    {
    }
}
