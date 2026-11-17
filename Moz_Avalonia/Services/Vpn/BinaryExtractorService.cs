using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Moz_Avalonia.Services.Vpn;

public static class BinaryExtractorService
{
    private static readonly object _syncLock = new();

    public static string GetBinDirectory()
    {
        string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string binDir = Path.Combine(baseDir, "MozVpn", "bin");
        if (!Directory.Exists(binDir))
        {
            Directory.CreateDirectory(binDir);
        }
        return binDir;
    }

    public static string EnsureLinuxHelperExtracted()
    {
        string binDir = GetBinDirectory();
        string targetPath = Path.Combine(binDir, "moz-tun-helper");

        lock (_syncLock)
        {
            ExtractResourceIfNewer("moz-tun-helper", targetPath);
            try
            {
                if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                {
                    File.SetUnixFileMode(targetPath,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                        UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                        UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                }
            }
            catch (Exception ex)
            {
                MozUtil.Logger.Log($"[BinaryExtractor] Failed setting Unix permissions on helper: {ex.Message}");
            }
        }

        return targetPath;
    }

    public static string EnsureLinuxSupervisorScript()
    {
        string binDir = GetBinDirectory();
        string scriptPath = Path.Combine(binDir, "moz-tun-run.sh");

        const string scriptContent = """
        #!/bin/sh
        # moz-tun-run.sh: Root supervisor for moz-tun-helper with DNS & routing lifecycle management
        DOH_PORT="${DOH_PORT:-53053}"

        cleanup() {
            # Clean up DNS redirect rules
            iptables -t nat -D OUTPUT -p udp --dport 53 -j REDIRECT --to-ports "$DOH_PORT" 2>/dev/null
            iptables -t nat -D OUTPUT -p tcp --dport 53 -j REDIRECT --to-ports "$DOH_PORT" 2>/dev/null
            iptables -t nat -D PREROUTING -p udp --dport 53 -j REDIRECT --to-ports "$DOH_PORT" 2>/dev/null
            iptables -t nat -D PREROUTING -p tcp --dport 53 -j REDIRECT --to-ports "$DOH_PORT" 2>/dev/null

            # Clean up policy routing rules and custom table
            ip rule del pref 32760 2>/dev/null
            ip rule del pref 32761 2>/dev/null
            ip rule del pref 32762 2>/dev/null
            ip route flush table 51421 2>/dev/null
            ip route del 0.0.0.0/1 dev moz-tun 2>/dev/null
            ip route del 128.0.0.0/1 dev moz-tun 2>/dev/null
            ip link del moz-tun 2>/dev/null
            rm -f /dev/shm/moz-tun*.ready 2>/dev/null

            if [ -n "$PID" ]; then
                kill -TERM "$PID" 2>/dev/null
                wait "$PID" 2>/dev/null
            fi
        }
        trap cleanup EXIT INT TERM

        "$@" &
        PID=$!

        # Wait for EOF on stdin from parent app. When parent closes stdin or exits, unblocks and cleans up.
        cat >/dev/null

        cleanup
        """;

        lock (_syncLock)
        {
            File.WriteAllText(scriptPath, scriptContent.Replace("\r\n", "\n"));
            try
            {
                if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                {
                    File.SetUnixFileMode(scriptPath,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                        UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                        UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                }
            }
            catch (Exception ex)
            {
                MozUtil.Logger.Log($"[BinaryExtractor] Failed setting Unix permissions on supervisor: {ex.Message}");
            }
        }

        return scriptPath;
    }

    public static (string HelperPath, string WintunPath) EnsureWindowsHelperExtracted()
    {
        string binDir = GetBinDirectory();
        string helperPath = Path.Combine(binDir, "moz-tun-helper.exe");
        string wintunPath = Path.Combine(binDir, "wintun.dll");

        lock (_syncLock)
        {
            ExtractResourceIfNewer("moz-tun-helper.exe", helperPath);
            ExtractResourceIfNewer("wintun.dll", wintunPath);
        }

        return (helperPath, wintunPath);
    }

    private static void ExtractResourceIfNewer(string resourceSuffix, string destinationPath)
    {
        var asm = Assembly.GetExecutingAssembly();
        var resourceName = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(resourceSuffix, StringComparison.OrdinalIgnoreCase));

        if (resourceName == null)
        {
            if (File.Exists(destinationPath)) return;
            throw new FileNotFoundException($"Embedded resource ending with '{resourceSuffix}' was not found in assembly.");
        }

        using var stream = asm.GetManifestResourceStream(resourceName);
        if (stream == null) return;

        if (File.Exists(destinationPath))
        {
            var info = new FileInfo(destinationPath);
            if (info.Length == stream.Length)
            {
                return;
            }
        }

        string tempPath = destinationPath + ".tmp";
        using (var destStream = File.Create(tempPath))
        {
            stream.CopyTo(destStream);
        }

        File.Move(tempPath, destinationPath, overwrite: true);
        MozUtil.Logger.Log($"[BinaryExtractor] Extracted {resourceSuffix} to {destinationPath}");
    }
}
