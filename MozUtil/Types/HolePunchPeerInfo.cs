using System.Net;
using System.Net.Sockets;
using STUN;

namespace MozUtil.Types
{
   public class HolePunchPeerInfo
   {
      public HolePunchPeerInfo()
      {
         ipAddress = new IPAddress(0);
      }

      public STUNNATType NatType { get; set; }
      public AddressFamily addressFamily { get; set; }
      public IPAddress ipAddress { get; set; }
      public int HolePunchTimeout { get; set; }
      public int Port { get; set; }
      public int PortsCount { get; set; }
   }
}