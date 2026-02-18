using System.Threading.Channels;

namespace UdpBeam_Core
{
   public class UdpBeamPeer
   {
      Channel<byte[]> SendChannel { get; } = Channel.CreateUnbounded<byte[]>();
   }
}
