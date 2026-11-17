using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using LiteNetLib;

namespace DirtySocksASP
{
   public class LNUdpConnection : IDisposable
   {
       private int _closed;
       private readonly UdpClient _udpClient;
       private readonly NetPeer _peer;
       private readonly byte _channelId;
       private readonly ushort _connectionId;
       private readonly CancellationTokenSource _cts = new();
       public event EventHandler<ushort>? ConnectionClosed;

       public LNUdpConnection(ushort connectionId, byte channelId, UdpClient udpClient, NetPeer peer)
       {
           _connectionId = connectionId;
           _channelId = channelId;
           _udpClient = udpClient;
           _peer = peer;
       }

       public async Task StartReceiveAsync()
       {
           try
           {
               var token = _cts.Token;
               while (Interlocked.CompareExchange(ref _closed, 0, 0) == 0 && !token.IsCancellationRequested)
               {
                   var result = await _udpClient.ReceiveAsync(token);
                   
                   // Prepare response packet: [ConnectionID (2 bytes)] [Payload]
                   byte[] sendBuf = new byte[2 + result.Buffer.Length];
                   BitConverter.GetBytes(_connectionId).CopyTo(sendBuf, 0);
                   Array.Copy(result.Buffer, 0, sendBuf, 2, result.Buffer.Length);

                   // Send back to client as Unreliable
                   _peer.Send(sendBuf, _channelId, DeliveryMethod.Unreliable);
               }
           }
           catch
           {
               Close();
           }
       }

       public async Task SendDataToTargetAsync(byte[] data)
       {
           try
           {
               await _udpClient.SendAsync(data, data.Length);
           }
           catch
           {
               Close();
           }
       }

       public void Close()
       {
           if (Interlocked.Exchange(ref _closed, 1) != 0)
               return;
               
           _cts.Cancel();
           _cts.Dispose();
           
           try
           {
               ConnectionClosed?.Invoke(this, _connectionId);
           }
           catch { }
           
           _udpClient.Dispose();
       }

       public void Dispose()
       {
           Close();
       }
   }
}
