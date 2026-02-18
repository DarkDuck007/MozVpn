using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace MozUtil
{
   public static class MozWin32
   {
      public const int INTERNET_OPTION_SETTINGS_CHANGED = 39;
      public const int INTERNET_OPTION_REFRESH = 37;

      [DllImport("wininet.dll")]
      public static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

      public static void unsetProxy()
      {
         setProxy("", false);
      }

      public static void setProxy(string proxyhost, bool proxyEnabled, bool BypassLocal = true)
      {
         const string userRoot = "HKEY_CURRENT_USER";
         const string subkey = "Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings";
         const string keyName = userRoot + "\\" + subkey;
         if (proxyhost.Length != 0)
            Registry.SetValue(keyName, "ProxyServer", proxyhost);
         Registry.SetValue(keyName, "ProxyEnable", proxyEnabled ? "1" : "0", RegistryValueKind.DWord);
         if (BypassLocal)
            Registry.SetValue(keyName, "ProxyOverride", "<local>");

         // These lines implement the Interface in the beginning of program 
         // They cause the OS to refresh the settings, causing IP to realy update
         InternetSetOption(IntPtr.Zero, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
         InternetSetOption(IntPtr.Zero, INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);
      }
   }
}