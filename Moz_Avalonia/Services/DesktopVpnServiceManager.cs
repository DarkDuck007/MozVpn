namespace Moz_Avalonia.Services;

public class DesktopVpnServiceManager : IVpnServiceManager
{
    public bool IsVpnRunning { get; private set; }
    public string? ActiveProfileName { get; private set; }

    public void StartVpn(string profileName, int socksPort)
    {
        IsVpnRunning = true;
        ActiveProfileName = profileName;
    }

    public void StopVpn()
    {
        IsVpnRunning = false;
        ActiveProfileName = null;
    }
}
