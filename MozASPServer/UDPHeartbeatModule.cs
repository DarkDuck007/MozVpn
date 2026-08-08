using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DirtySocksASP
{
   public static class UDPHeartbeatModule
   {
      public static async Task<bool> DoHeartBeatAsync(UdpClient UDPsrv, IPEndPoint EP, CancellationToken CancellationToken)
      {
         bool Result = false;
         bool isCancelled = false;
         CancellationToken.Register(() => { isCancelled = true; });
         await Task.Run(async () =>
           {
              while (!isCancelled)
              {
                 var res = await UDPsrv.ReceiveAsync();
                 if (res.RemoteEndPoint.ToString() != EP.ToString())
                 {
                    byte[] wtfBytes = Encoding.ASCII.GetBytes("WTF");
                    await UDPsrv.SendAsync(wtfBytes, wtfBytes.Length);
                 }
                 else
                 {
                    string ResBuffer = Encoding.ASCII.GetString(res.Buffer);
                    if (ResBuffer.Length < 12)
                    {
                       byte[] wtfBytes = Encoding.ASCII.GetBytes("WTF");
                       await UDPsrv.SendAsync(wtfBytes, wtfBytes.Length, EP);
                       Result = true;
                       break;
                    }
                    else
                    {
                       if (ResBuffer.Substring(0, 12) == "Client Ping:")
                       {
                          byte[] SendBuffer = Encoding.ASCII.GetBytes("Server Reply:" + (ResBuffer.Split(':')[1]));
                          await UDPsrv.SendAsync(SendBuffer, SendBuffer.Length, EP);
                       }
                       else if (ResBuffer == "SHB")
                       {
                          Result = true;
                          break;
                       }
                    }
                 }
              }
           }, CancellationToken);
         return Result;
      }
   }
}
