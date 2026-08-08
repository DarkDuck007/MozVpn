using System.Net.Sockets;

namespace DirtySocksASP
{
   public class TCPConnection : IDisposable
   {
      public event EventHandler<ushort>? RemoveConnection;
      public TcpClient TcpClient;
      public NetworkStream TcpClientStream;
      public int ConnectioID { get; }
      public TCPConnection(int ConID, string Hostname, int Port)
      {
         ConnectioID = ConID;
         TcpClient = new TcpClient(Hostname, Port);
         TcpClientStream = TcpClient.GetStream();
      }
      public async Task WriteAsync(ArraySegment<byte> WriteBytes)
      {
         await TcpClientStream.WriteAsync(WriteBytes);
      }


      public void Dispose()
      {
         TcpClient.Dispose();
         TcpClientStream.Dispose();
      }
   }
}
