using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using MozUtil.Types;

namespace MozUtil.NatUtils
{
    public sealed class MozServerDescriptor
    {
        public string RawLine { get; init; } = string.Empty;
        public string Address { get; init; } = string.Empty;
        public string? RoamSeed { get; init; }
        public int RoamPortCount { get; init; } = MZRoamProtocol.DefaultPortCount;
        public int RoamIntervalSeconds { get; init; } = MZRoamProtocol.DefaultIntervalSeconds;
        public string? Alias { get; init; }

        public bool HasRoamSeed => !string.IsNullOrWhiteSpace(RoamSeed);
    }

    public static class MZRoamProtocol
    {
        public const int DefaultPortCount = 10;
        public const int DefaultIntervalSeconds = 10;
        public const int DefaultWindowSize = 5;
        public const int DefaultMinPort = 10000;
        public const int DefaultMaxPort = 60000;
        public const int DataHeaderLength = 5;

        private const byte RawTypeHello = 0x51;
        private const byte RawTypeHelloAck = 0x52;
        private const byte RawTypeData = 0x53;
        private const int HelloFixedLength = 33; // 1(type) + 8(nonce) + 8(bucket) + 2(ver) + 2(minVer) + 4(caps) + 8(hmac)
        private const int HelloAckMinLength = 22; // 1(type) + 8(nonce) + 4(sessionId) + 1(keyLen) + key + 8(hmac)

        public static void DeriveSeedMasks(string seed, out byte typeMask, out uint sessionMask, out byte[] hmacKey)
        {
            hmacKey = Encoding.UTF8.GetBytes(seed ?? string.Empty);
            byte[] hash = SHA256.HashData(hmacKey);
            typeMask = hash[0];
            sessionMask = BinaryPrimitives.ReadUInt32LittleEndian(hash.AsSpan(4, 4));
        }

        public static long GetBucketIndex(long utcTicks, int intervalSeconds = DefaultIntervalSeconds)
        {
            long clampedSeconds = Math.Max(1, intervalSeconds);
            long intervalTicks = clampedSeconds * TimeSpan.TicksPerSecond;
            return utcTicks / intervalTicks;
        }

        public static int[] GetPortsForBucket(
            string seed,
            long bucketIndex,
            int portCount = DefaultPortCount,
            int minPort = DefaultMinPort,
            int maxPort = DefaultMaxPort)
        {
            if (string.IsNullOrWhiteSpace(seed))
                throw new ArgumentException("MZRoam seed cannot be empty.", nameof(seed));
            if (minPort < 1024) minPort = 1024;
            if (maxPort > 65535) maxPort = 65535;
            if (maxPort <= minPort) maxPort = Math.Min(65535, minPort + 1000);

            int range = maxPort - minPort + 1;
            int targetCount = Math.Clamp(portCount, 1, Math.Min(256, range));

            byte[] key = Encoding.UTF8.GetBytes(seed);
            var selected = new List<int>(targetCount);
            var seen = new HashSet<int>();

            Span<byte> input = stackalloc byte[12];
            BinaryPrimitives.WriteInt64LittleEndian(input.Slice(0, 8), bucketIndex);
            Span<byte> hash = stackalloc byte[32];

            uint counter = 0;
            while (selected.Count < targetCount)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(input.Slice(8, 4), counter++);
                HMACSHA256.HashData(key, input, hash);

                for (int offset = 0; offset <= 28 && selected.Count < targetCount; offset += 4)
                {
                    uint raw = BinaryPrimitives.ReadUInt32LittleEndian(hash.Slice(offset, 4));
                    int port = minPort + (int)(raw % (uint)range);
                    if (seen.Add(port))
                    {
                        selected.Add(port);
                    }
                }
            }

            return selected.ToArray();
        }

        public static byte[] BuildHelloPacket(
            string seed,
            ulong clientNonce,
            long bucketIndex,
            ushort protocolVersion = MozProtocol.CurrentVersion,
            ushort minimumVersion = MozProtocol.MinimumVersion,
            MozProtocolCapabilities capabilities = MozProtocol.ClientCapabilities)
        {
            DeriveSeedMasks(seed, out byte typeMask, out _, out byte[] hmacKey);
            int padLen = RandomNumberGenerator.GetInt32(16, 65);
            byte[] packet = new byte[HelloFixedLength + padLen];

            packet[0] = (byte)(RawTypeHello ^ typeMask);
            BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(1, 8), clientNonce);
            BinaryPrimitives.WriteInt64LittleEndian(packet.AsSpan(9, 8), bucketIndex);
            BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(17, 2), protocolVersion);
            BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(19, 2), minimumVersion);
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(21, 4), (uint)capabilities);

            Span<byte> mac = stackalloc byte[32];
            HMACSHA256.HashData(hmacKey, packet.AsSpan(0, 25), mac);
            mac.Slice(0, 8).CopyTo(packet.AsSpan(25, 8));

            RandomNumberGenerator.Fill(packet.AsSpan(HelloFixedLength, padLen));
            return packet;
        }

        public static bool TryParseAndVerifyHello(
            ReadOnlySpan<byte> packet,
            byte typeMask,
            byte[] hmacKey,
            long currentBucketIndex,
            int maxBucketSkew,
            out ulong clientNonce,
            out ushort protocolVersion,
            out ushort minimumVersion,
            out MozProtocolCapabilities capabilities)
        {
            clientNonce = 0;
            protocolVersion = MozProtocol.LegacyVersion;
            minimumVersion = MozProtocol.LegacyVersion;
            capabilities = MozProtocolCapabilities.None;

            if (packet.Length < HelloFixedLength)
                return false;

            if ((byte)(packet[0] ^ typeMask) != RawTypeHello)
                return false;

            long bucketIndex = BinaryPrimitives.ReadInt64LittleEndian(packet.Slice(9, 8));
            if (Math.Abs(bucketIndex - currentBucketIndex) > Math.Max(1, maxBucketSkew))
                return false;

            Span<byte> mac = stackalloc byte[32];
            HMACSHA256.HashData(hmacKey, packet.Slice(0, 25), mac);
            if (!CryptographicOperations.FixedTimeEquals(mac.Slice(0, 8), packet.Slice(25, 8)))
                return false;

            clientNonce = BinaryPrimitives.ReadUInt64LittleEndian(packet.Slice(1, 8));
            protocolVersion = BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(17, 2));
            minimumVersion = BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(19, 2));
            capabilities = (MozProtocolCapabilities)BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(21, 4));
            return protocolVersion >= minimumVersion && minimumVersion > 0;
        }

        public static byte[] BuildHelloAckPacket(
            byte typeMask,
            uint sessionMask,
            byte[] hmacKey,
            ulong clientNonce,
            uint sessionId,
            ReadOnlySpan<byte> connectionKeyBytes)
        {
            if (connectionKeyBytes.Length is < 1 or > 128)
                throw new ArgumentOutOfRangeException(nameof(connectionKeyBytes), "Connection key must be 1..128 bytes.");

            int bodyLen = 14 + connectionKeyBytes.Length;
            byte[] packet = new byte[bodyLen + 8];
            packet[0] = (byte)(RawTypeHelloAck ^ typeMask);
            BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(1, 8), clientNonce);
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(9, 4), sessionId ^ sessionMask);
            packet[13] = (byte)connectionKeyBytes.Length;
            connectionKeyBytes.CopyTo(packet.AsSpan(14, connectionKeyBytes.Length));

            Span<byte> mac = stackalloc byte[32];
            HMACSHA256.HashData(hmacKey, packet.AsSpan(0, bodyLen), mac);
            mac.Slice(0, 8).CopyTo(packet.AsSpan(bodyLen, 8));
            return packet;
        }

        public static bool TryParseAndVerifyHelloAck(
            ReadOnlySpan<byte> packet,
            string seed,
            ulong expectedClientNonce,
            out uint sessionId,
            out string connectionKeyBase64)
        {
            sessionId = 0;
            connectionKeyBase64 = string.Empty;

            DeriveSeedMasks(seed, out byte typeMask, out uint sessionMask, out byte[] hmacKey);
            if (packet.Length < HelloAckMinLength)
                return false;

            if ((byte)(packet[0] ^ typeMask) != RawTypeHelloAck)
                return false;

            ulong nonce = BinaryPrimitives.ReadUInt64LittleEndian(packet.Slice(1, 8));
            if (nonce != expectedClientNonce)
                return false;

            int keyLen = packet[13];
            int bodyLen = 14 + keyLen;
            if (keyLen <= 0 || packet.Length < bodyLen + 8)
                return false;

            Span<byte> mac = stackalloc byte[32];
            HMACSHA256.HashData(hmacKey, packet.Slice(0, bodyLen), mac);
            if (!CryptographicOperations.FixedTimeEquals(mac.Slice(0, 8), packet.Slice(bodyLen, 8)))
                return false;

            uint maskedSessionId = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(9, 4));
            sessionId = maskedSessionId ^ sessionMask;
            connectionKeyBase64 = Convert.ToBase64String(packet.Slice(14, keyLen));
            return sessionId != 0 && !string.IsNullOrEmpty(connectionKeyBase64);
        }

        public static byte[] BuildDataPacket(byte typeMask, uint sessionMask, uint sessionId, ReadOnlySpan<byte> payload)
        {
            byte[] packet = new byte[DataHeaderLength + payload.Length];
            packet[0] = (byte)(RawTypeData ^ typeMask);
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(1, 4), sessionId ^ sessionMask);
            payload.CopyTo(packet.AsSpan(DataHeaderLength));
            return packet;
        }

        public static bool TryParseDataPacket(
            ReadOnlySpan<byte> packet,
            byte typeMask,
            uint sessionMask,
            out uint sessionId,
            out byte[] payload)
        {
            sessionId = 0;
            payload = Array.Empty<byte>();

            if (packet.Length <= DataHeaderLength)
                return false;

            if ((byte)(packet[0] ^ typeMask) != RawTypeData)
                return false;

            uint maskedSessionId = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(1, 4));
            sessionId = maskedSessionId ^ sessionMask;
            payload = packet.Slice(DataHeaderLength).ToArray();
            return sessionId != 0;
        }

        /// <summary>
        /// Parses a line from ServerList.txt.
        /// Supported formats:
        /// 1. Legacy URL or host: "https://server.example.com/" or "1.2.3.4"
        /// 2. Pipe-delimited: "address|seed|ports|interval|alias" (trailing fields optional)
        /// 3. URI query/fragment parameters: "https://server.example.com/#seed=myseed&amp;ports=10&amp;interval=10"
        /// </summary>
        public static MozServerDescriptor? ParseServerLine(string? line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return null;

            string trimmed = line.Trim();
            if (trimmed.StartsWith('#') || trimmed.StartsWith("//", StringComparison.Ordinal))
                return null;

            string address = trimmed;
            string? seed = null;
            int portCount = DefaultPortCount;
            int intervalSeconds = DefaultIntervalSeconds;
            string? alias = null;

            if (trimmed.Contains('|'))
            {
                string[] parts = trimmed.Split('|');
                address = parts[0].Trim();
                if (parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1]))
                    seed = parts[1].Trim();
                if (parts.Length > 2 && int.TryParse(parts[2].Trim(), out int parsedPorts) && parsedPorts > 0)
                    portCount = parsedPorts;
                if (parts.Length > 3 && int.TryParse(parts[3].Trim(), out int parsedInterval) && parsedInterval > 0)
                    intervalSeconds = parsedInterval;
                if (parts.Length > 4 && !string.IsNullOrWhiteSpace(parts[4]))
                    alias = parts[4].Trim();
            }
            else if (Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri) &&
                     (!string.IsNullOrEmpty(uri.Fragment) || !string.IsNullOrEmpty(uri.Query)))
            {
                string queryPart = !string.IsNullOrEmpty(uri.Fragment)
                    ? uri.Fragment.TrimStart('#', '?')
                    : uri.Query.TrimStart('?');

                foreach (string kv in queryPart.Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    int eq = kv.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = Uri.UnescapeDataString(kv[..eq].Trim());
                    string val = Uri.UnescapeDataString(kv[(eq + 1)..].Trim());
                    if (key.Equals("seed", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(val))
                        seed = val;
                    else if (key.Equals("ports", StringComparison.OrdinalIgnoreCase) && int.TryParse(val, out int p) && p > 0)
                        portCount = p;
                    else if (key.Equals("interval", StringComparison.OrdinalIgnoreCase) && int.TryParse(val, out int iv) && iv > 0)
                        intervalSeconds = iv;
                    else if (key.Equals("alias", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(val))
                        alias = val;
                }

                if (seed != null)
                {
                    address = uri.GetLeftPart(UriPartial.Path);
                }
            }

            if (string.IsNullOrWhiteSpace(address))
                return null;

            return new MozServerDescriptor
            {
                RawLine = trimmed,
                Address = address,
                RoamSeed = seed,
                RoamPortCount = portCount,
                RoamIntervalSeconds = intervalSeconds,
                Alias = alias
            };
        }

        public static string ExtractHost(string serverAddress)
        {
            if (string.IsNullOrWhiteSpace(serverAddress))
                return string.Empty;

            var descriptor = ParseServerLine(serverAddress);
            string target = (descriptor?.Address ?? serverAddress).Trim().TrimEnd('/');

            if (Uri.TryCreate(target, UriKind.Absolute, out Uri? uri) && !string.IsNullOrWhiteSpace(uri.DnsSafeHost))
            {
                return uri.DnsSafeHost;
            }

            if (Uri.TryCreate("http://" + target, UriKind.Absolute, out Uri? fallbackUri) &&
                !string.IsNullOrWhiteSpace(fallbackUri.DnsSafeHost))
            {
                return fallbackUri.DnsSafeHost;
            }

            int colon = target.LastIndexOf(':');
            if (colon > 0 && target.IndexOf(':') == colon)
            {
                return target[..colon];
            }

            return target.Trim('[', ']');
        }

        public static async System.Threading.Tasks.Task<IPAddress> ResolveServerAddressAsync(string serverAddress)
        {
            string host = ExtractHost(serverAddress);
            if (string.IsNullOrWhiteSpace(host))
                throw new ArgumentException($"Could not extract a valid host from '{serverAddress}'.", nameof(serverAddress));

            if (IPAddress.TryParse(host, out IPAddress? parsedIp))
                return parsedIp;

            IPAddress[] addresses = await Dns.GetHostAddressesAsync(host).ConfigureAwait(false);
            foreach (var addr in addresses)
            {
                if (addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    return addr;
            }
            if (addresses.Length > 0)
                return addresses[0];

            throw new InvalidOperationException($"Could not resolve any IP addresses for host '{host}'.");
        }
    }
}
