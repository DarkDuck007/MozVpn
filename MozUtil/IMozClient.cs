using System.Net;

namespace MozUtil
{
   public interface IMozClient
   {
      //public udpMode UdpMode { get; }
      public IPEndPoint LocalEndPoint { get; }
      public IPEndPoint ServerEndPoint { get; }
      public int TcpListenPort { get; }
      public int HttpListenPort { get; }
      //public int UdpListenPort { get; }
      public int MaxConnectionRetries { get; set; }
   }
}