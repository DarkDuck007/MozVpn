using System;

namespace MozUtil.Types
{
   [Flags]
   public enum MozProtocolCapabilities : uint
   {
      None = 0,
      ReliableUnorderedStreamClose = 1 << 0,
      OrphanFloodProtection = 1 << 1,
      StreamCloseAcknowledgement = 1 << 2,
      PerStreamFlowControl = 1 << 3
   }

   public static class MozProtocol
   {
      public const ushort LegacyVersion = 1;
      public const ushort CurrentVersion = 2;
      public const ushort MinimumVersion = LegacyVersion;
      public const string VersionHeader = "X-Moz-Protocol-Version";
      public const string MinimumVersionHeader = "X-Moz-Protocol-Min-Version";
      public const string CapabilitiesHeader = "X-Moz-Protocol-Capabilities";
      public const int HelloPayloadLength = 8;

      public const MozProtocolCapabilities ClientCapabilities =
         MozProtocolCapabilities.ReliableUnorderedStreamClose |
         MozProtocolCapabilities.OrphanFloodProtection;

      public const MozProtocolCapabilities ServerCapabilities =
         MozProtocolCapabilities.ReliableUnorderedStreamClose |
         MozProtocolCapabilities.StreamCloseAcknowledgement;

      public static byte[] BuildHello(ushort command, MozProtocolCapabilities capabilities)
      {
         byte[] packet = new byte[6 + HelloPayloadLength];
         BitConverter.GetBytes(command).CopyTo(packet, 4);
         BitConverter.GetBytes(CurrentVersion).CopyTo(packet, 6);
         BitConverter.GetBytes(MinimumVersion).CopyTo(packet, 8);
         BitConverter.GetBytes((uint)capabilities).CopyTo(packet, 10);
         return packet;
      }

      public static bool TryReadHello(byte[] packet, int offset, out ushort version, out ushort minimumVersion,
         out MozProtocolCapabilities capabilities)
      {
         version = LegacyVersion;
         minimumVersion = LegacyVersion;
         capabilities = MozProtocolCapabilities.None;
         if (packet == null || offset < 0 || packet.Length - offset < HelloPayloadLength)
            return false;

         version = BitConverter.ToUInt16(packet, offset);
         minimumVersion = BitConverter.ToUInt16(packet, offset + 2);
         capabilities = (MozProtocolCapabilities)BitConverter.ToUInt32(packet, offset + 4);
         return version >= minimumVersion && minimumVersion > 0;
      }

      public static bool IsCompatible(ushort remoteVersion, ushort remoteMinimumVersion)
      {
         return remoteMinimumVersion <= CurrentVersion && remoteVersion >= MinimumVersion;
      }
   }
}
