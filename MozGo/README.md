MozGo (UDP NAT Traversal Tunnel, Go)

Overview
- Minimal UDP tunnel with NAT traversal concepts similar to MozVPN.
- Server exposes HTTP /GetEP to report client public endpoint (no STUN case).
- UDP tunnel supports bidirectional data between client and a target endpoint.

Protocol (UDP)
- 0x01 HELLO: [1][session u32][targetLen u16][target bytes]
- 0x11 HELLO_ACK: [1][session u32]
- 0x02 DATA: [1][session u32][payload...]
- 0x03 KEEPALIVE: [1][session u32]
- 0x13 KEEPALIVE_ACK: [1][session u32]

Server
```
go run ./cmd/mozgo-server -http :8080 -udp :40000
```

Client
```
go run ./cmd/mozgo-client -server-http http://server:8080 -server-udp server:40000 \
  -local 127.0.0.1:1081 -target 1.2.3.4:9000
```

Notes
- For NATs where STUN is unavailable, calling /GetEP can help logging/debug of
  the observed public endpoint (restricted cone cases).
- This is a minimal proof-of-concept; symmetric NATs are not fully handled.

