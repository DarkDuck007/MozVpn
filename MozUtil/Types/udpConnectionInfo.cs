using System.IO;
using System;
using System.Net;
using System.Net.Sockets;
using MozUtil.NatUtils;

namespace MozUtil.Types
{
   public class udpConnectionInfo
   {
      public TransportMode udpMode { get; set; }
      public AddressFamily addressFamily { get; set; }
      public IPAddress ipAddress { get; set; }
      public int Port { get; set; }
      public string? ConnectionKey { get; set; }

      public static byte[] SerializeUdpConnectionInfo(TransportMode udpMode, IPAddress ip, int port,
       byte[] ConnectionKey = null)
      {
         return MozStatic.SerializeUdpConnectionInfo(udpMode, ip, port, ConnectionKey);
      }

      public static udpConnectionInfo DeserializeUdpConnectionInfo(byte[] Data, int Offset)
      {
         return DeserializeUdpConnectionInfo(Data, Offset);
      }
   }
}