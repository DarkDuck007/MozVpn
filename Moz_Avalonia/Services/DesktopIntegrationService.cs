using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using MozUtil;

namespace Moz_Avalonia.Services;

public sealed record BrowserInfo(string Name, string Executable)
{
    public override string ToString() => Name;
}

public sealed class DesktopIntegrationService
{
    public static void ClearWindowsSystemProxyOnExit()
    {
        if (!OperatingSystem.IsWindows()) return;
        MozWin32.unsetProxy();
    }

    public IReadOnlyList<BrowserInfo> FindBrowsers()
    {
        var candidates = OperatingSystem.IsWindows()
            ? WindowsCandidates()
            : new[]
            {
                new BrowserInfo("Chromium", "chromium"), new BrowserInfo("Chromium", "chromium-browser"),
                new BrowserInfo("Google Chrome", "google-chrome"), new BrowserInfo("Google Chrome", "google-chrome-stable"),
                new BrowserInfo("Brave", "brave-browser"), new BrowserInfo("Microsoft Edge", "microsoft-edge"),
                new BrowserInfo("Vivaldi", "vivaldi")
            };

        return candidates.Where(x => IsExecutableAvailable(x.Executable)).DistinctBy(x => x.Executable).ToArray();
    }

    public async Task<string> SetSystemProxyAsync(int httpPort, int socksPort)
    {
        if (OperatingSystem.IsWindows())
        {
            MozWin32.setProxy($"http=127.0.0.1:{httpPort};https=127.0.0.1:{httpPort};socks=127.0.0.1:{socksPort}", true);
            return "Windows system proxy enabled.";
        }

        if (OperatingSystem.IsLinux() && IsExecutableAvailable("gsettings"))
        {
            await RunAsync("gsettings", "set", "org.gnome.system.proxy", "mode", "manual");
            await RunAsync("gsettings", "set", "org.gnome.system.proxy.http", "host", "127.0.0.1");
            await RunAsync("gsettings", "set", "org.gnome.system.proxy.http", "port", httpPort.ToString());
            await RunAsync("gsettings", "set", "org.gnome.system.proxy.https", "host", "127.0.0.1");
            await RunAsync("gsettings", "set", "org.gnome.system.proxy.https", "port", httpPort.ToString());
            await RunAsync("gsettings", "set", "org.gnome.system.proxy.socks", "host", "127.0.0.1");
            await RunAsync("gsettings", "set", "org.gnome.system.proxy.socks", "port", socksPort.ToString());
            return "GNOME system proxy enabled.";
        }

        throw new PlatformNotSupportedException("Automatic system proxy configuration currently supports Windows and GNOME (gsettings). Use the displayed proxy addresses for this desktop.");
    }

    public async Task<string> ClearSystemProxyAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            MozWin32.unsetProxy();
            return "Windows system proxy disabled.";
        }

        if (OperatingSystem.IsLinux() && IsExecutableAvailable("gsettings"))
        {
            await RunAsync("gsettings", "set", "org.gnome.system.proxy", "mode", "none");
            return "GNOME system proxy disabled.";
        }

        return "No supported desktop-wide proxy backend was detected.";
    }

    public string LaunchBrowser(BrowserInfo browser, int httpPort, string browserProfileId, string? url = null)
    {
        if (!Guid.TryParseExact(browserProfileId, "N", out _))
            throw new ArgumentException("The browser profile identifier is invalid.", nameof(browserProfileId));

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var profileRoot = Path.Combine(appData, "MozVpn", "browser-profiles");
        var profilePath = Path.Combine(profileRoot, browserProfileId);
        Directory.CreateDirectory(profilePath);
        var startInfo = new ProcessStartInfo(browser.Executable) { UseShellExecute = false };
        startInfo.ArgumentList.Add($"--proxy-server=http://127.0.0.1:{httpPort}");
        startInfo.ArgumentList.Add($"--user-data-dir={profilePath}");
        startInfo.ArgumentList.Add("--no-first-run");
        startInfo.ArgumentList.Add("--no-default-browser-check");
        startInfo.ArgumentList.Add("--disable-background-networking");
        startInfo.ArgumentList.Add("--disable-sync");
        startInfo.ArgumentList.Add("--force-webrtc-ip-handling-policy=disable_non_proxied_udp");
        if (!string.IsNullOrWhiteSpace(url))
            startInfo.ArgumentList.Add(url);
        Process.Start(startInfo);
        return $"Launched {browser.Name} in this connection's isolated profile through port {httpPort}.";
    }

    private static bool IsExecutableAvailable(string executable)
    {
        if (Path.IsPathRooted(executable))
            return File.Exists(executable);
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        return path.Split(Path.PathSeparator).Any(directory => File.Exists(Path.Combine(directory, executable)));
    }

    private static BrowserInfo[] WindowsCandidates()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return
        [
            new("Google Chrome", Path.Combine(programFiles, "Google", "Chrome", "Application", "chrome.exe")),
            new("Google Chrome", Path.Combine(programFilesX86, "Google", "Chrome", "Application", "chrome.exe")),
            new("Microsoft Edge", Path.Combine(programFilesX86, "Microsoft", "Edge", "Application", "msedge.exe")),
            new("Brave", Path.Combine(programFiles, "BraveSoftware", "Brave-Browser", "Application", "brave.exe")),
            new("Chromium", Path.Combine(local, "Chromium", "Application", "chrome.exe"))
        ];
    }

    private static async Task RunAsync(string fileName, params string[] arguments)
    {
        var info = new ProcessStartInfo(fileName) { RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"Could not start {fileName}.");
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? $"{fileName} exited with code {process.ExitCode}." : error.Trim());
    }
}
