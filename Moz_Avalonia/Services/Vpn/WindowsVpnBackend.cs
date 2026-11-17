using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Moz_Avalonia.Services.Vpn;

public class WindowsVpnBackend : IVpnBackend
{
    private readonly DohForwarderService _dohService = new();
    private Process? _helperProcess;
    private string? _addedServerRouteIp;
    private string? _physicalGateway;
    private string? _currentReadyFile;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public bool IsRunning => _helperProcess != null && !_helperProcess.HasExited;

    public async Task StartAsync(string serverAddress, int socksPort, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (IsRunning) await StopInternalAsync();

            // 1. Start local DNS-over-HTTPS forwarder tunneling queries through local SOCKS5 proxy
            await _dohService.StartAsync(socksPort, preferredPort: 53, cancellationToken);

            var (helperPath, wintunPath) = BinaryExtractorService.EnsureWindowsHelperExtracted();
            string? gateway = await GetDefaultGatewayAsync();
            if (string.IsNullOrEmpty(gateway))
            {
                throw new InvalidOperationException("Could not detect the Windows default gateway.");
            }

            _physicalGateway = gateway;

            string? serverIp = await ResolveServerIpAsync(serverAddress);
            if (!string.IsNullOrEmpty(serverIp) && !serverIp.StartsWith("127."))
            {
                _addedServerRouteIp = serverIp;
            }

            string readyFile = Path.Combine(Path.GetTempPath(), $"moz-tun-{Guid.NewGuid():N}.ready");
            _currentReadyFile = readyFile;

            string postUpScript = BuildWindowsPostUpScript(gateway, _addedServerRouteIp, readyFile);

            var psi = new ProcessStartInfo
            {
                FileName = helperPath,
                WorkingDirectory = Path.GetDirectoryName(helperPath),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            psi.ArgumentList.Add("--device");
            psi.ArgumentList.Add("wintun://MozTun");
            psi.ArgumentList.Add("--proxy");
            psi.ArgumentList.Add($"socks5://127.0.0.1:{socksPort}");
            psi.ArgumentList.Add("--tun-post-up");
            psi.ArgumentList.Add(postUpScript);
            psi.ArgumentList.Add("--loglevel");
            psi.ArgumentList.Add("info");

            var readyTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var outputBuilder = new System.Text.StringBuilder();
            var errorBuilder = new System.Text.StringBuilder();

            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    MozUtil.Logger.Log($"[WinTun] {e.Data}");
                    outputBuilder.AppendLine(e.Data);
                    if (e.Data.Contains("MOZ_TUN_READY"))
                    {
                        readyTcs.TrySetResult(true);
                    }
                }
            };
            proc.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    MozUtil.Logger.Log($"[WinTun Error] {e.Data}");
                    errorBuilder.AppendLine(e.Data);
                }
            };

            proc.Exited += (_, _) =>
            {
                string err = errorBuilder.ToString().Trim();
                if (string.IsNullOrEmpty(err)) err = outputBuilder.ToString().Trim();
                string msg = string.IsNullOrEmpty(err)
                    ? $"Process exited with code {proc.ExitCode}"
                    : err;
                readyTcs.TrySetException(new InvalidOperationException(msg));
            };

            if (!proc.Start())
            {
                throw new InvalidOperationException("Failed to start moz-tun-helper.exe process.");
            }

            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            _helperProcess = proc;

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            while (!linkedCts.Token.IsCancellationRequested)
            {
                if (proc.HasExited)
                {
                    string err = errorBuilder.ToString().Trim();
                    if (string.IsNullOrEmpty(err)) err = outputBuilder.ToString().Trim();
                    string msg = string.IsNullOrEmpty(err)
                        ? $"Process exited with code {proc.ExitCode}"
                        : err;
                    throw new InvalidOperationException($"TUN failed to start: {msg}");
                }

                if (File.Exists(readyFile) || readyTcs.Task.IsCompletedSuccessfully)
                {
                    MozUtil.Logger.Log("[WinTun] Wintun interface and routes detected ready.");
                    break;
                }

                try
                {
                    await Task.Delay(100, linkedCts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            if (timeoutCts.IsCancellationRequested && !File.Exists(readyFile) && !readyTcs.Task.IsCompletedSuccessfully)
            {
                await StopInternalAsync();
                throw new TimeoutException("Timed out waiting for Windows TUN helper setup.");
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
        if (_helperProcess != null)
        {
            try
            {
                if (!_helperProcess.HasExited)
                {
                    _helperProcess.Kill(entireProcessTree: true);
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    await _helperProcess.WaitForExitAsync(cts.Token);
                }
            }
            catch (Exception ex)
            {
                MozUtil.Logger.Log($"[WinTun] Error stopping process: {ex.Message}");
            }
            finally
            {
                _helperProcess.Dispose();
                _helperProcess = null;
            }
        }

        // Stop local DoH service
        try
        {
            await _dohService.StopAsync();
        }
        catch {}

        if (!string.IsNullOrEmpty(_currentReadyFile))
        {
            try { File.Delete(_currentReadyFile); } catch {}
            _currentReadyFile = null;
        }

        // Clean up Windows routes
        try
        {
            await RunCommandAsync("route", "delete 0.0.0.0 mask 128.0.0.0");
            await RunCommandAsync("route", "delete 128.0.0.0 mask 128.0.0.0");
            if (!string.IsNullOrEmpty(_addedServerRouteIp))
            {
                await RunCommandAsync("route", $"delete {_addedServerRouteIp} mask 255.255.255.255");
                _addedServerRouteIp = null;
            }
        }
        catch {}
    }

    private static string BuildWindowsPostUpScript(string gateway, string? serverIp, string readyFile)
    {
        // 1. Assign static IP to Wintun adapter
        string script = "netsh interface ip set address name=\"MozTun\" static 10.254.254.1 255.255.255.252 10.254.254.2";

        // 2. Add explicit bypass host route for remote Moz Server via physical gateway
        if (!string.IsNullOrEmpty(serverIp) && !serverIp.StartsWith("127."))
        {
            script += $" && route add {serverIp} mask 255.255.255.255 {gateway} metric 1";
        }

        // 3. Add dual /1 routes to capture all IPv4 traffic
        script += " && route add 0.0.0.0 mask 128.0.0.0 10.254.254.2 metric 1";
        script += " && route add 128.0.0.0 mask 128.0.0.0 10.254.254.2 metric 1";

        // 4. Configure DNS on Wintun adapter pointing to local DoH forwarder
        script += " && netsh interface ip set dns name=\"MozTun\" static 10.254.254.1";

        script += $" && (type nul > \"{readyFile}\") && echo MOZ_TUN_READY";

        return $"cmd.exe /c \"{script}\"";
    }

    private static async Task<string?> GetDefaultGatewayAsync()
    {
        try
        {
            string output = await RunCommandAsync("route", "print 0.0.0.0");
            var match = Regex.Match(output, @"0\.0\.0\.0\s+0\.0\.0\.0\s+([0-9.]+)\s+([0-9.]+)");
            if (match.Success)
            {
                return match.Groups[1].Value;
            }
        }
        catch (Exception ex)
        {
            MozUtil.Logger.Log($"[WinTun] Failed to query default gateway: {ex.Message}");
        }
        return null;
    }

    private static async Task<string?> ResolveServerIpAsync(string serverAddress)
    {
        try
        {
            string host = MozUtil.NatUtils.MZRoamProtocol.ExtractHost(serverAddress);
            if (string.IsNullOrWhiteSpace(host))
                return null;

            if (IPAddress.TryParse(host, out var parsedIp))
            {
                return parsedIp.ToString();
            }

            var addresses = await Dns.GetHostAddressesAsync(host);
            var ipv4 = Array.Find(addresses, a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
            return ipv4?.ToString();
        }
        catch (Exception ex)
        {
            MozUtil.Logger.Log($"[WinTun] Failed resolving server IP for {serverAddress}: {ex.Message}");
            return null;
        }
    }

    private static async Task<string> RunCommandAsync(string fileName, string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var p = Process.Start(psi);
        if (p == null) return string.Empty;

        string output = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        return output.Trim();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        await _dohService.DisposeAsync();
    }
}
