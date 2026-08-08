using System.Net;
using System.Net.Sockets;
using System.Text;
using MozUtil;
namespace DirtySocksASP
{
   public class UDPConnection : IDisposable
   {
      UdpClient _Client;
      IPEndPoint _Source;
      IPEndPoint _Destination;
      int KeepAliveCounter = 0;
      int KeepAliveIntervalSeconds = 5;
      int KillTunnelTimeout = 900;
      int KillTunnelCounter = 0;
      Timer KLTimer;
      CancellationTokenSource CancelTSource;
      public UDPConnection(UdpClient ClientToTun, IPEndPoint Source, IPEndPoint Destination, int KeepAliveIntervalSec = -1, int TunTimeout = 900, CancellationTokenSource? CTS = null)
      {
         _Client = ClientToTun;
         _Source = Source;
         _Destination = Destination;
         KeepAliveIntervalSeconds = KeepAliveIntervalSec;
         KLTimer = new Timer(TimerCallback, null, 1000, 1000);
         KillTunnelTimeout = TunTimeout;
         CancelTSource = (CTS == null) ? new CancellationTokenSource() : CTS;
      }

      public void Dispose()
      {
         try
         {
            _Client.Dispose();
         }
         catch (Exception ex)
         {
            Logger.Log(ex);
         }
         try
         {
            KLTimer.Dispose();
         }
         catch (Exception ex)
         {
            Logger.Log(ex);
         }
         try
         {
            CancelTSource.Cancel();
            CancelTSource.Dispose();
         }
         catch (Exception ex)
         {
            Logger.Log(ex);
         }
      }

      public void TimerCallback(object? state)
      {
         if (KillTunnelCounter > KillTunnelTimeout)
         {
            this.Dispose();
         }
         KillTunnelCounter++;
         if (KeepAliveIntervalSeconds > 0)
         {
            if (KeepAliveIntervalSeconds - KeepAliveCounter <= 0)
            {
               Interlocked.Exchange(ref KeepAliveCounter, 0);
               KeepAliveCounter = 0;
               try
               {
                  _Client.Send(new byte[0], _Source);
               }
               catch (ObjectDisposedException)
               {
                  KLTimer.Dispose();
               }
               catch (Exception ex)
               {
                  Logger.Log(ex);
               }
            }
            Interlocked.Increment(ref KeepAliveCounter);
         }

      }
      public async Task TunnelAsync()
      {
         UdpReceiveResult RecRes;
         //while (!CancelTSource.IsCancellationRequested)
         while (true)
         {
            try
            {
               RecRes = await _Client.ReceiveAsync(CancelTSource.Token);
               Logger.Log($"Received {RecRes.Buffer.Length}");
               if (RecRes.RemoteEndPoint.Equals(_Source))
               {
                  //Interlocked.Exchange(ref KillTunnelTimeout, 0);
                  Interlocked.Exchange(ref KeepAliveCounter, 0);
                  if (RecRes.Buffer.Length == 0)
                  {
                     Logger.Log("Empty Buffer, Keepalive.");
                     await _Client.SendAsync(new byte[0], _Source);
                  }
                  else
                  {
                     await _Client.SendAsync(RecRes.Buffer, RecRes.Buffer.Length, _Destination);
                  }
               }
               else if (RecRes.RemoteEndPoint.Equals(_Destination))
               {
                  //if (RecRes.Buffer.Length == 0)
                  //{
                  //   await _Client.SendAsync(new byte[0], 0, _Destination);
                  //}
                  await _Client.SendAsync(RecRes.Buffer, RecRes.Buffer.Length, _Source);
               }
               else
               {
                  Logger.Log("Endpoint mismatch. Attempting update.");
                  try
                  {
                     await _Client.SendAsync(new byte[0], RecRes.RemoteEndPoint);
                     string ClientIP = Encoding.ASCII.GetString(RecRes.Buffer);
                     if (ClientIP.Equals(_Source.Address.ToString()))
                     {
                        if (RecRes.RemoteEndPoint.Address.Equals(_Source.Address))
                        {
                           _Source = RecRes.RemoteEndPoint;
                           Logger.Log("Endpoint updated.");
                           await _Client.SendAsync(new byte[0], _Source);
                           continue;
                        }
                        else
                        {
                           Logger.Log("Source IP Mismatch. endpoint not updated.");
                           continue;
                        }
                     }
                     else
                     {
                        Logger.Log("No data provided.");
                     }
                  }
                  catch (Exception ex)
                  {
                     Logger.Log(ex);
                  }
               }
            }
            catch (Exception ex)
            {
               Logger.Log(ex);
               break;
            }
         }
      }
   }
}
