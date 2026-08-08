using System;
using System.Diagnostics;
using System.Security;
using System.Text;

namespace Moz_Avalonia.Services;

public sealed class DesktopNotificationService
{
    public void Show(string title, string message)
    {
        try
        {
            if (OperatingSystem.IsLinux()) ShowLinux(title, message);
            else if (OperatingSystem.IsWindows()) ShowWindows(title, message);
        }
        catch
        {
            // Notifications are best-effort and must never affect an active tunnel.
        }
    }

    private static void ShowLinux(string title, string message)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "notify-send",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("--app-name=Moz VPN");
        startInfo.ArgumentList.Add("--icon=network-vpn");
        startInfo.ArgumentList.Add(title);
        startInfo.ArgumentList.Add(message);
        Process.Start(startInfo)?.Dispose();
    }

    private static void ShowWindows(string title, string message)
    {
        var safeTitle = SecurityElement.Escape(title) ?? string.Empty;
        var safeMessage = SecurityElement.Escape(message) ?? string.Empty;
        var script = "$ErrorActionPreference='SilentlyContinue';" +
                     "[Windows.UI.Notifications.ToastNotificationManager,Windows.UI.Notifications,ContentType=WindowsRuntime]>$null;" +
                     "[Windows.Data.Xml.Dom.XmlDocument,Windows.Data.Xml.Dom.XmlDocument,ContentType=WindowsRuntime]>$null;" +
                     "$x=New-Object Windows.Data.Xml.Dom.XmlDocument;" +
                     $"$x.LoadXml('<toast><visual><binding template=\"ToastGeneric\"><text>{safeTitle}</text><text>{safeMessage}</text></binding></visual></toast>');" +
                     "$t=[Windows.UI.Notifications.ToastNotification]::new($x);" +
                     "[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('Moz VPN').Show($t);";
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-WindowStyle");
        startInfo.ArgumentList.Add("Hidden");
        startInfo.ArgumentList.Add("-EncodedCommand");
        startInfo.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        Process.Start(startInfo)?.Dispose();
    }
}
