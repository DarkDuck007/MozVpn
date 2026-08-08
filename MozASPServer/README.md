# Moz ASP.NET server

`MozASPServer` is the integrated ASP.NET Core endpoint for Moz clients. It targets .NET 9 and references the
repository copies of `MozUtil`, `STUN`, and `LiteNetLib`. MTProto support uses the published `MTProtoProxy` package;
no sibling repository checkout is required.

Run it from the repository root:

```sh
dotnet run --project MozASPServer/MozASPServer.csproj
```

The primary client endpoint is `POST /InitLN`. The server also exposes `/GetEP`, `/Status`, `/GetLog`, `/PollLog`,
`/StartSocks`, `/LiteNetSpeedTest`, `/UDPRL`, and the existing diagnostic endpoints.

## Configuration

The following environment variables retain their historical behavior:

- `IsLocal`: use loopback addressing for local testing.
- `DestIP`: destination SOCKS5 address when `UseInternalSocks5Proxy` is false.
- `Socks5Port`: internal SOCKS5 exit port (default `36567`).
- `UseInternalSocks5Proxy`: start the built-in unauthenticated SOCKS5 CONNECT server (default `true`).

The obsolete Ruffles transport and its missing external projects were removed. The active LiteNet transport supports
legacy v1 clients and negotiated v2 clients as documented in [`../PROTOCOL_COMPATIBILITY.md`](../PROTOCOL_COMPATIBILITY.md).

Legacy peers retain the original reliable-ordered stream-close behavior. New close lanes and acknowledgements are
enabled only when the peer advertises the corresponding capability. As a safety limit, a transport that sends more
than 2 MiB in a five-second sample (or 4 MiB across consecutive suspicious samples) while it has zero logical proxy
connections is disconnected and allowed to reconnect, preventing an abandoned reliable queue from consuming
bandwidth indefinitely. Normal low-volume keep-alives do not accumulate toward this limit.

The bundled LiteNetLib intentionally retains Moz's legacy 2048-packet reliable window. This determines the ACK wire
format and must match deployed clients; replacing it with LiteNetLib's stock 64-packet value causes endless reliable
retransmission traffic.
