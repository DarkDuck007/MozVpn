using System;
using System.Threading;
using System.Threading.Tasks;

namespace Moz_Avalonia.Services.Vpn;

public interface IVpnBackend : IAsyncDisposable
{
    bool IsRunning { get; }
    Task StartAsync(string serverAddress, int socksPort, CancellationToken cancellationToken = default);
    Task StopAsync();
}

