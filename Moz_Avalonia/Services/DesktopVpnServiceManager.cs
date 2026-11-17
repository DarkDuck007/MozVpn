using System;
using System.Threading.Tasks;
using Moz_Avalonia.Services.Vpn;

namespace Moz_Avalonia.Services;

public class DesktopVpnServiceManager : IVpnServiceManager
{
    private readonly IVpnBackend? _backend;

    public bool IsVpnRunning => _backend?.IsRunning ?? false;
    public string? ActiveProfileName { get; private set; }
    public int ActiveSocksPort { get; private set; }

    public DesktopVpnServiceManager()
    {
        if (OperatingSystem.IsLinux())
        {
            _backend = new LinuxVpnBackend();
        }
        else if (OperatingSystem.IsWindows())
        {
            _backend = new WindowsVpnBackend();
        }
    }

    public async Task StartVpnAsync(string profileName, int socksPort, string? serverAddress = null)
    {
        if (_backend == null)
        {
            throw new PlatformNotSupportedException("No supported VPN backend on this OS.");
        }

        ActiveProfileName = profileName;
        ActiveSocksPort = socksPort;

        try
        {
            await _backend.StartAsync(serverAddress ?? string.Empty, socksPort);
        }
        catch (Exception ex)
        {
            MozUtil.Logger.Log($"[DesktopVpn] Failed starting VPN tunnel: {ex.Message}");
            ActiveProfileName = null;
            ActiveSocksPort = 0;
            throw;
        }
    }

    public void StartVpn(string profileName, int socksPort, string? serverAddress = null)
    {
        _ = StartVpnAsync(profileName, socksPort, serverAddress);
    }

    public async Task StopVpnAsync()
    {
        ActiveProfileName = null;
        ActiveSocksPort = 0;

        if (_backend != null)
        {
            try
            {
                await _backend.StopAsync();
            }
            catch (Exception ex)
            {
                MozUtil.Logger.Log($"[DesktopVpn] Error stopping VPN tunnel: {ex.Message}");
            }
        }
    }

    public void StopVpn()
    {
        ActiveProfileName = null;
        ActiveSocksPort = 0;

        if (_backend != null)
        {
            try
            {
                _backend.StopAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                MozUtil.Logger.Log($"[DesktopVpn] Error stopping VPN tunnel: {ex.Message}");
            }
        }
    }

    public void UpdateNotificationStatus(string status)
    {
    }
}
