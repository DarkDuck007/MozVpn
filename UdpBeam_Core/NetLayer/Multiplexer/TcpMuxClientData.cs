using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace UdpBeam_Core.NetLayer.Multiplexer
{
   public struct TcpMuxClientData
   {
      public int connectionID;
      public int dataLength;
      public byte[] data;
   }
}
