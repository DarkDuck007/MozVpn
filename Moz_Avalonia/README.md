# Moz VPN for Avalonia

This is the cross-platform desktop frontend for `MozUtil`. It targets Linux first and also supports Windows.

## Architecture

- `MainViewModel` owns persisted profile choices and a collection of live connections.
- Every `ConnectionViewModel` owns a separate `MozManager`, local SOCKS/HTTP port pair, log, UDP relays, server-stat subscription, and bandwidth history. Connections therefore remain active when another profile is selected.
- `StunProbeService` sends real UDP STUN binding queries and chooses the fastest responding server for Auto mode.
- `DesktopIntegrationService` configures the Windows proxy or GNOME's `gsettings` proxy and launches Chromium-family browsers with isolated profiles.
- Settings are stored atomically at the path shown in the application's status bar (normally `~/.local/share/MozVpn/avalonia-settings.json` on Linux).

## Usage

1. Select or enter a Moz server and choose a STUN server. `Auto` tests up to 20 candidates when connecting.
2. Pick transport and channel settings, then use **Add & connect**.
3. Add more profiles to keep several server tunnels active. Each receives unique local proxy ports.
4. Use **System proxy** to switch desktop traffic, or **Launch browser** to start an isolated Chromium/Chrome/Brave/Edge/Vivaldi session through that connection.
5. The detail tabs expose all WPF functionality: client telemetry, server-stat streaming, UDP relay creation/retry, and logs.

Use **Edit** on a connection card or in the detail toolbar to load that profile into the editor. **Save** stores it disconnected; **Save & connect** restarts it with the changed settings. **Delete** removes the profile and safely stops its tunnel first.

On Linux, desktop-wide proxy switching currently uses GNOME `gsettings`. Other desktop environments can still use the displayed SOCKS5/HTTP endpoints or the proxied-browser launcher. BALETUN remains subject to the hard-coded Windows executable in `MozUtil`; the UI exposes it for WPF parity, but it is not portable until that backend is changed.

Build with:

```sh
dotnet build Moz_Avalonia/Moz_Avalonia.csproj -m:1
dotnet run --project Moz_Avalonia/Moz_Avalonia.csproj
```
