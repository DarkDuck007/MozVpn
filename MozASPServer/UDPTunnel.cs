using System.Collections.Concurrent;
using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Win32.SafeHandles;
using MozUtil;

namespace DirtySocksASP
{
   public class UDPTunnel : IDisposable
   {
      public const ushort KeepAliveValPacket = 0xFFFF; //65535
      UdpClient _udpClient;
      IPEndPoint _EP;
      public UDPTunnel(UdpClient UDPClient, IPEndPoint EP)
      {
         _udpClient = UDPClient;
         _EP = EP;
      }
      /// <summary>
      /// Starts a new UDP Tunnel.
      /// </summary>
      /// <param name="KeepAliveInterval">KeepAlive packet interval.</param>
      /// <param name="LocalPort">Local Port to connect the tunnel to. 0 to connect to default local Socks5 Server.</param>
      /// <returns></returns>
      public async Task<DisconnectionReason> StartTunnelAsync(CancellationToken CT, ProtocolType PType, IPEndPoint SourceEP, string DestAddr = "127.0.0.1", int DestPort = 0, int KeepAliveInterval = -1)
      {
         int KeepAliveTimer = 0;
         bool isCancelled = false;
         CT.Register(() => { isCancelled = true; });
         DisconnectionReason DisconReason = DisconnectionReason.Unknown;
         if (PType == ProtocolType.Udp)
         {
            //One connection only, with the desired IP:Port.
            IPAddress DestinationAddress = Dns.GetHostEntry(DestAddr).AddressList.First(x => x.AddressFamily == SourceEP.AddressFamily);
            using (UDPConnection Tun = new UDPConnection(_udpClient, SourceEP, new IPEndPoint(DestinationAddress, DestPort), KeepAliveInterval))
            {
               await Tun.TunnelAsync();
            }
         }
         else if (PType == ProtocolType.Tcp)
         {
            ConcurrentDictionary<ushort, TCPConnection> TCPConnections = new ConcurrentDictionary<ushort, TCPConnection>();
            //Task KeepAliveTask = Task.Run(async () =>
            // {
            //    while (!isCancelled)
            //    {
            //       KeepAliveTimer += 1;
            //       if (KeepAliveTimer >= KeepAliveInterval)
            //       {
            //          //Send Keepalive packet
            //       }
            //       await Task.Delay(1000);
            //    }
            // }, CT);
            if (DestPort == 0)
               DestPort = StaticServers.Socks5ServerPort;

            Task ReaderTask = Task.Run(async () =>
            {
               UdpReceiveResult res;
               while (!isCancelled)
               {
                  res = await _udpClient.ReceiveAsync();
                  byte[] ConnectionIDBytes = new byte[2];
                  if (res.RemoteEndPoint.ToString() != _EP.ToString())
                  {
                     byte[] Panic = Encoding.UTF8.GetBytes("SERVER PANIC ENDPOINT MISMATCH");
                     Logger.Log($"Endpoint mismatch panic expected {_EP.ToString()} got {res.RemoteEndPoint.ToString()}");
                     await _udpClient.SendAsync(Panic, Panic.Length, res.RemoteEndPoint);
                     isCancelled = true;
                     DisconReason = DisconnectionReason.ServerException;
                     break;
                  }
                  else
                  {
                     try
                     {
                        Array.Copy(res.Buffer, ConnectionIDBytes, ConnectionIDBytes.Length);
                        ushort ConnectionID = BitConverter.ToUInt16(ConnectionIDBytes, 0);
                        if (ConnectionID == 65535) //This is keepalive packet
                        {
                           await ReplyKeepAliveAsync();
                        }
                        if (TCPConnections.ContainsKey(ConnectionID))
                           await TCPConnections[ConnectionID].TcpClientStream.WriteAsync(res.Buffer, 2, res.Buffer.Length - 2);
                        else
                        {
                           TCPConnection Con = new TCPConnection(ConnectionID, DestAddr, DestPort);
                           try
                           {
                              TCPConnections.TryAdd(ConnectionID, Con);
                              await Con.TcpClientStream.WriteAsync(res.Buffer, 2, res.Buffer.Length - 2);
                              Con.RemoveConnection += (object? sender, ushort e) => { Con.Dispose(); TCPConnections.Remove(e, out _); };
                              _ = Task.Run(async () =>
                              {
                                 byte[] WriteBuffer = new byte[4096];
                                 int i = 0;
                                 while ((i = await Con.TcpClientStream.ReadAsync(WriteBuffer, CT)) != 0)
                                 {
                                    await _udpClient.SendAsync(WriteBuffer, i, _EP);
                                 }
                              }, CT);
                           }
                           catch (Exception ex)
                           {
                              Con.Dispose();
                              TCPConnections.Remove(ConnectionID, out _);
                              Logger.Log(ex.StackTrace);
                           }
                        }
                     }
                     catch (Exception ex)
                     {
                        Console.WriteLine(ex.StackTrace);
                        Logger.Log(ex.StackTrace);
                     }

                  }
               }
            }, CT);
            await ReaderTask;
         }
         else
         {
            DisconReason = DisconnectionReason.ServerException;
            throw new NotSupportedException("Protocol not supported");
         }
         //Reader

         return DisconReason;
      }

      private async Task ReplyKeepAliveAsync()
      {
         //bool KeepAliveSuccess = false;
         //for (int i = 0; i < 5; i++)
         //{

         //}
         await _udpClient.SendAsync(new byte[] { 0xFF, 0xFF }, 2, _EP);
      }

      public void Dispose()
      {
         try
         {
            _udpClient.Dispose();
         }
         catch (Exception ex)
         {
            Logger.Log(ex);
         }
      }
   }
}
