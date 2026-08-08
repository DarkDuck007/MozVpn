# Moz protocol compatibility

Protocol version 1 is the legacy framing. Version 2 adds capability negotiation without changing any existing
command number or payload layout.

Clients advertise these HTTP headers on every initialization, keep-alive, and reconnect request:

- `X-Moz-Protocol-Version`
- `X-Moz-Protocol-Min-Version`
- `X-Moz-Protocol-Capabilities` (unsigned integer bit mask)

Legacy HTTP servers ignore these headers. After LiteNet connects, a version 2 client also sends
`ClientCommands.ProtocolHello` (`4`) as reliable-unordered control traffic. Its payload is little-endian:

| Offset | Size | Value |
| --- | ---: | --- |
| 0 | 4 | Zero control prefix |
| 4 | 2 | Command number |
| 6 | 2 | Current protocol version |
| 8 | 2 | Minimum compatible version |
| 10 | 4 | Capability flags |

A version 2 server may reply with the same layout using `ServerCommands.ProtocolHello` (`7`). Old servers ignore
client command `4`; old clients ignore server command `7`. A new server must treat a missing header/hello as version
1 with no advertised capabilities and retain legacy behavior.

The server integration in `MozASPServer` is intentionally conditional:

1. Read the three HTTP headers when present; otherwise store version `1`, minimum `1`, capabilities `0`.
2. Add `case ClientCommands.ProtocolHello` to the existing UDP command switch. Parse offset `6` with
   `MozProtocol.TryReadHello`, store the values per peer, and reply with
   `ServerCommandUtils.BuildProtocolHelloCommand(serverCapabilities)`.
3. Check `MozProtocol.IsCompatible`; reject only when the local and remote version ranges do not overlap.
4. Gate every future v2-only operation on its advertised capability bit. Do not infer support merely from a version.

The current server advertises `ReliableUnorderedStreamClose` and `StreamCloseAcknowledgement`. It accepts clients
without either header or hello as legacy v1 clients. The HTTP response repeats the server's version range and
capability mask, while the UDP hello is sent only in response to a client hello so an old client never receives a
new command unexpectedly.

Stream-close frames retain their existing four-byte `[0, 0, connection-id]` layout. Version 2 clients send them on
the reliable-unordered control lane so they are not blocked behind reliable-ordered stream data. Servers must accept
the close frame on either reliable lane so version 1 clients remain supported.

For server-to-client close frames, the server must preserve reliable-ordered delivery for a legacy peer. It may use
reliable-unordered delivery only after that peer advertises `ReliableUnorderedStreamClose`. Likewise, it must not echo
a close acknowledgement unless that peer advertises `StreamCloseAcknowledgement`; unsolicited acknowledgements are
not legacy-safe.

Capabilities are opt-in. A server must not require `StreamCloseAcknowledgement` or `PerStreamFlowControl` until the
client advertises the corresponding flag. Unsupported protocol ranges should be rejected during HTTP initialization
with a clear response rather than by changing UDP framing.

## LiteNetLib wire constants

Moz's version 1 deployment uses a LiteNetLib reliable-window size of 2048 and an MTU probe table ending at 9000.
These are wire-protocol constants, not local performance tuning. LiteNetLib's ACK payload contains one bit for every
entry in `DefaultWindowSize`; a peer built with the stock size of 64 therefore rejects ACK packets from a Moz v1 peer
because their packet sizes differ. The unacknowledged reliable packets are then retransmitted forever, producing
bandwidth that increases with every proxied request.

Do not change `NetConstants.DefaultWindowSize` independently on either endpoint. A future window-size change requires
negotiation before LiteNetLib creates its reliable channels, or a separate transport protocol version/port.
