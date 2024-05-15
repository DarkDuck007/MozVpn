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
   }
}