MozVpn notes (Android MAUI / Xray)

- App id on device: Moz.Duck.VPN (use adb run-as Moz.Duck.VPN ...)
- Xray binaries are packaged as native libs:
  - Platforms/Android/lib/arm64-v8a/libxray.so
  - Platforms/Android/lib/x86_64/xray (may need renaming to libxray.so if not packaged)
- Xray is launched from ApplicationInfo.NativeLibraryDir; raw binary in app data can be noexec.
- Config flow:
  - Resources/Raw/Config.json is copied to /data/user/0/Moz.Duck.VPN/files/xray_tun.json.
  - Tun inbound is injected at runtime with the current fd.
  - Xray logs: xray_access.log, xray_error.log (same directory).
- Geo assets:
  - Resources/Raw/geoip.dat and Resources/Raw/geosite.dat are bundled as MauiAsset.
  - Copied to app data; XRAY_LOCATION_ASSET is set to app data when launching.
- VPN/TUN:
  - VpnService.Builder sets IPv4 and IPv6 routes; MTU set.
  - Tun fd handling tries DetachFd + AdoptFd and clears CLOEXEC via fcntl.
  - Still seeing no packets captured; xray_access.log stays empty.
- Config note:
  - Current Config.json uses VLESS to 127.0.0.1:6075; if no local VLESS server is running, traffic will time out.
  - v2rayNG uses tun2socks; for full UDP the Xray tun inbound should be used.

MozGo (Go reimplementation, UDP NAT traversal)
- Added MozGo module with minimal UDP tunnel and /GetEP HTTP endpoint.
- Folder structure:
  - MozGo/go.mod
  - MozGo/README.md (protocol + usage)
  - MozGo/internal/proto/proto.go (packet framing)
  - MozGo/cmd/mozgo-server/main.go (UDP relay + /GetEP)
  - MozGo/cmd/mozgo-client/main.go (client relay + keepalive)
- Protocol:
  - HELLO (session + target), HELLO_ACK, DATA, KEEPALIVE, KEEPALIVE_ACK
- Bidirectional UDP relay via server-side per-session target UDP connection.
- /GetEP mirrors DirtySocksASP /GetEP for non‑STUN NAT discovery.
