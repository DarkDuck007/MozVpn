using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Moz_Avalonia.Services.Vpn;

public class LinuxVpnBackend : IVpnBackend
{
    private readonly DohForwarderService _dohService = new();
    private Process? _helperProcess;
    private string? _addedServerRouteIp;
    private string? _physicalGateway;
    private string? _physicalInterface;
    private string? _currentReadyFile;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public bool IsRunning => _helperProcess != null && !_helperProcess.HasExited;

    public async Task StartAsync(string serverAddress, int socksPort, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (IsRunning) await StopInternalAsync();

            // 1. Start the DNS-over-HTTPS (DoH) forwarder tunneling through the local SOCKS5 proxy
            await _dohService.StartAsync(socksPort, preferredPort: 53053, cancellationToken);

            string helperPath = BinaryExtractorService.EnsureLinuxHelperExtracted();
            string supervisorPath = BinaryExtractorService.EnsureLinuxSupervisorScript();

            string? serverIp = await ResolveServerIpAsync(serverAddress);
            if (!string.IsNullOrEmpty(serverIp) && !serverIp.StartsWith("127."))
            {
                _addedServerRouteIp = serverIp;
            }

            var (gateway, iface) = await GetOutboundRouteAsync(_addedServerRouteIp);
            _physicalGateway = gateway;
            _physicalInterface = iface;

            string readyFile = $"/dev/shm/moz-tun-{Guid.NewGuid():N}.ready";
            _currentReadyFile = readyFile;

            // Post-up configuration script executed as root by tun2socks
            string postUpScript = BuildPostUpScript(gateway, iface, _addedServerRouteIp, _dohService.ListenPort, readyFile);

            var psi = new ProcessStartInfo
            {
                FileName = IsRunningAsRoot() ? supervisorPath : "pkexec",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            psi.Environment["DOH_PORT"] = _dohService.ListenPort.ToString();

            if (!IsRunningAsRoot())
            {
                psi.ArgumentList.Add(supervisorPath);
            }

            psi.ArgumentList.Add(helperPath);
            psi.ArgumentList.Add("--device");
            psi.ArgumentList.Add("tun://moz-tun");
            psi.ArgumentList.Add("--proxy");
            psi.ArgumentList.Add($"socks5://127.0.0.1:{socksPort}");
            psi.ArgumentList.Add("--tun-post-up");
            psi.ArgumentList.Add($"sh -c \"{postUpScript}\"");
            psi.ArgumentList.Add("--loglevel");
            psi.ArgumentList.Add("info");

            var outputBuilder = new System.Text.StringBuilder();
            var errorBuilder = new System.Text.StringBuilder();

            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    MozUtil.Logger.Log($"[LinuxTun] {e.Data}");
                    outputBuilder.AppendLine(e.Data);
                }
            };
            proc.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    MozUtil.Logger.Log($"[LinuxTun Error] {e.Data}");
                    errorBuilder.AppendLine(e.Data);
                }
            };

            if (!proc.Start())
            {
                throw new InvalidOperationException("Failed to start moz-tun-helper process.");
            }

            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            _helperProcess = proc;

            // Wait for helper to be ready (up to 45 seconds for user to authenticate in GUI)
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
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

                if (File.Exists(readyFile))
                {
                    MozUtil.Logger.Log("[LinuxTun] TUN interface and routes detected ready.");
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

            if (timeoutCts.IsCancellationRequested && !File.Exists(readyFile))
            {
                await StopInternalAsync();
                throw new TimeoutException("Timed out waiting for TUN helper authorization and setup.");
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
                    // Closing StandardInput triggers supervisor script's root cleanup trap and kills tun2socks cleanly
                    try
                    {
                        _helperProcess.StandardInput.Close();
                    }
                    catch {}

                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    try
                    {
                        await _helperProcess.WaitForExitAsync(cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        // Fallback: kill the pkexec process directly without traversing tree
                        _helperProcess.Kill(entireProcessTree: false);
                    }
                }
            }
            catch (Exception ex)
            {
                MozUtil.Logger.Log($"[LinuxTun] Error terminating helper: {ex.Message}");
            }
            finally
            {
                _helperProcess.Dispose();
                _helperProcess = null;
            }
        }

        // Stop the local DoH forwarder
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

        // Cleanup host bypass route if added to main table
        if (!string.IsNullOrEmpty(_addedServerRouteIp))
        {
            try
            {
                if (!string.IsNullOrEmpty(_physicalGateway) && !string.IsNullOrEmpty(_physicalInterface))
                {
                    await RunCommandAsync("ip", $"route del {_addedServerRouteIp}/32 via {_physicalGateway} dev {_physicalInterface}");
                }
                else if (!string.IsNullOrEmpty(_physicalInterface))
                {
                    await RunCommandAsync("ip", $"route del {_addedServerRouteIp}/32 dev {_physicalInterface}");
                }
                else
                {
                    await RunCommandAsync("ip", $"route del {_addedServerRouteIp}/32");
                }
            }
            catch {}
            _addedServerRouteIp = null;
        }

        // Fallback cleanup of rules and interface in case supervisor didn't run
        try { await RunCommandAsync("iptables", $"-t nat -D OUTPUT -p udp --dport 53 -j REDIRECT --to-ports {_dohService.ListenPort}"); } catch {}
        try { await RunCommandAsync("iptables", $"-t nat -D OUTPUT -p tcp --dport 53 -j REDIRECT --to-ports {_dohService.ListenPort}"); } catch {}
        try { await RunCommandAsync("iptables", $"-t nat -D PREROUTING -p udp --dport 53 -j REDIRECT --to-ports {_dohService.ListenPort}"); } catch {}
        try { await RunCommandAsync("iptables", $"-t nat -D PREROUTING -p tcp --dport 53 -j REDIRECT --to-ports {_dohService.ListenPort}"); } catch {}
        try { await RunCommandAsync("ip", "rule del pref 32760"); } catch {}
        try { await RunCommandAsync("ip", "rule del pref 32761"); } catch {}
        try { await RunCommandAsync("ip", "rule del pref 32762"); } catch {}
        try { await RunCommandAsync("ip", "route flush table 51421"); } catch {}
        try { await RunCommandAsync("ip", "link del moz-tun"); } catch {}
    }

    private static string BuildPostUpScript(string? gateway, string? iface, string? serverIp, int dohPort, string readyFile)
    {
        // 1. Assign static IP to TUN interface and bring it up
        var script = "ip addr replace 10.254.254.1/30 dev moz-tun && ip link set dev moz-tun up";

        // 2. WireGuard (fwmark 0xca6c) and Cloudflare WARP (fwmark 0x100cf) anti-loop rules:
        // Direct their outbound encrypted packets to physical egress instead of trapping them in moz-tun
        script += " && (ip rule add fwmark 0xca6c lookup main pref 32760 2>/dev/null || true)";
        script += " && (ip rule add fwmark 0x100cf lookup 65743 pref 32761 2>/dev/null || true)";

        // 3. Populate dedicated MozVpn routing table 51421:
        // Host route for remote Moz Server via physical / current egress interface
        if (!string.IsNullOrEmpty(serverIp) && !serverIp.StartsWith("127."))
        {
            if (!string.IsNullOrEmpty(gateway) && !string.IsNullOrEmpty(iface))
            {
                script += $" && ip route replace {serverIp}/32 via {gateway} dev {iface} table 51421 metric 1";
            }
            else if (!string.IsNullOrEmpty(iface))
            {
                script += $" && ip route replace {serverIp}/32 dev {iface} table 51421 metric 1";
            }
        }

        // Default route in table 51421 routes all system traffic through moz-tun
        script += " && ip route replace default dev moz-tun table 51421";

        // 4. Activate table 51421 for system traffic
        script += " && (ip rule add table 51421 pref 32762 2>/dev/null || true)";

        // 5. Intercept all outbound DNS queries (UDP and TCP port 53) and redirect to local DoH forwarder
        script += $" && (iptables -t nat -A OUTPUT -p udp --dport 53 -j REDIRECT --to-ports {dohPort} 2>/dev/null || true)";
        script += $" && (iptables -t nat -A OUTPUT -p tcp --dport 53 -j REDIRECT --to-ports {dohPort} 2>/dev/null || true)";
        script += $" && (iptables -t nat -A PREROUTING -p udp --dport 53 -j REDIRECT --to-ports {dohPort} 2>/dev/null || true)";
        script += $" && (iptables -t nat -A PREROUTING -p tcp --dport 53 -j REDIRECT --to-ports {dohPort} 2>/dev/null || true)";

        // 6. Optional systemd-resolved integration if available
        script += " && (command -v resolvectl >/dev/null 2>&1 && resolvectl dns moz-tun 127.0.0.1 && resolvectl default-route moz-tun true || true)";

        // 7. Signal readiness
        script += $" && touch {readyFile} && echo MOZ_TUN_READY";

        return script;
    }

    private static bool IsRunningAsRoot()
    {
        return Environment.UserName == "root" || (Environment.GetEnvironmentVariable("USER") == "root");
    }

    private static async Task<(string? Gateway, string? Interface)> GetOutboundRouteAsync(string? targetIp)
    {
        try
        {
            string probeTarget = (string.IsNullOrEmpty(targetIp) || targetIp.StartsWith("127.")) ? "8.8.8.8" : targetIp;
            string output = await RunCommandAsync("ip", $"route get {probeTarget}");

            string? gateway = null;
            string? iface = null;

            var matchVia = Regex.Match(output, @"via\s+([0-9.]+)");
            if (matchVia.Success)
            {
                gateway = matchVia.Groups[1].Value;
            }

            var matchDev = Regex.Match(output, @"dev\s+([a-zA-Z0-9_.-]+)");
            if (matchDev.Success)
            {
                string dev = matchDev.Groups[1].Value;
                if (dev != "moz-tun" && dev != "lo")
                {
                    iface = dev;
                }
            }

            if (string.IsNullOrEmpty(iface))
            {
                string defaultOutput = await RunCommandAsync("ip", "-4 route show default");
                var matchDef = Regex.Match(defaultOutput, @"default\s+via\s+([0-9.]+)\s+dev\s+([a-zA-Z0-9_.-]+)");
                if (matchDef.Success)
                {
                    gateway = matchDef.Groups[1].Value;
                    iface = matchDef.Groups[2].Value;
                }
            }

            return (gateway, iface);
        }
        catch (Exception ex)
        {
            MozUtil.Logger.Log($"[LinuxTun] Failed to parse outbound route: {ex.Message}");
            return (null, null);
        }
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
            MozUtil.Logger.Log($"[LinuxTun] Failed resolving server IP for {serverAddress}: {ex.Message}");
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
