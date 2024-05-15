using System;
using System.Buffers;
using System.Net.Sockets;
using System.Threading.Tasks;
using LiteNetLib;

namespace MozUtil
{
   internal class MozLiteNetReliableConnection : IMozConnection, IDisposable
   {
      private readonly int CopyBufferLength = 4094;
      private readonly NetworkStream tcpClientToClientStream;

      public MozLiteNetReliableConnection(ushort _ConnectionID, byte _ChannelID, TcpClient _Client, int _PeerID,
         ref NetManager LNManager, int MaxOutPackets)
      {
         BoundChannelID = _ChannelID;
         ConnectionID = _ConnectionID;
         TcpClientToClient = _Client;
         PeerID = _PeerID;
         tcpClientToClientStream = TcpClientToClient.GetStream();
         LiteNetManager = LNManager;
         MaxOutboundPackets = MaxOutPackets;
      }

      private int MaxOutboundPackets { get; set; }
      private NetManager LiteNetManager { get; }
      public ushort ConnectionID { get; set; }
      public TcpClient TcpClientToClient { get; set; }
      public byte BoundChannelID { get; set; }
      public int PeerID { get; set; }

      public void Dispose()
      {
         Close();
      }

      public event EventHandler<ushort>? ConnectionClosed;
      public event EventHandler<MozPacket>? DataAvailable;

      public async Task StartConnectionAsync()
      {
         TcpClientToClient.NoDelay = true;
         var RentedArray = MozStatic.BufferByteArrayPool.Rent(CopyBufferLength + 2);
         MozPacket MPacket = new MozPacket { RawData = RentedArray };
         MPacket.ChannelID = BoundChannelID;
         MPacket.PeerID = PeerID;
         BitConverter.GetBytes(ConnectionID).CopyTo(MPacket.RawData, 0);
         try
         {
            int i = 0;
            while ((i = await tcpClientToClientStream.ReadAsync(MPacket.RawData, 2, CopyBufferLength)) > 0)
               try
               {
                  MPacket.StartIndex = 0;
                  MPacket.Length = i + 2;
                  //await UdpSrv.SendAsync(ReadBuffer, i + 2, ClientRemoteEndpoint);
                  //DataAvailable?.Invoke(this, MPacket);
                  if (LiteNetManager.IsRunning)
                     try
                     {
                        //while (LiteNetManager.GetPeerById(MPacket.PeerID).GetPacketsCountInReliableQueue(MPacket.ChannelID, true) > MaxOutboundPackets)
                        //{
                        //   System.Threading.Thread.Sleep(1);
                        //}
                        //Logger.Log($"Client is sending ({e.Length - 2}) {e.Length} bytes to server Con ID {((MozLiteNetReliableConnection)sender).ConnectionID} channel {e.ChannelID}");
                        //LiteNetManager.GetPeerById(MPacket.PeerID).Send(MPacket.RawData, MPacket.StartIndex, MPacket.Length, MPacket.ChannelID, DeliveryMethod.ReliableOrdered);
                        DataAvailable?.Invoke(this, MPacket);
                     }
                     catch (Exception ex)
                     {
                        Logger.Log(ex.Message + Environment.NewLine + ex.StackTrace);
                     }
               }
               catch (Exception ex)
               {
                  Logger.Log(ex.StackTrace);
                  break;
               }
            //Console.WriteLine("Server is sending {0} bytes to a client Con ID {1}", i + 2, ConnectionID);
            //Logger.Log($"Server is sending {i + 2} bytes to a client Con ID {ConnectionID}");
         }
         catch (Exception ex)
         {
            Console.WriteLine(ex.Message);
            //Logger.Log(ex.Message + Environment.NewLine + ex.StackTrace);
         }

         byte[] CloseConReqBuffer = new byte[4];
         BitConverter.GetBytes(ConnectionID).CopyTo(CloseConReqBuffer, 2);
         MPacket.Length = CloseConReqBuffer.Length;
         MPacket.RawData = CloseConReqBuffer;
         DataAvailable?.Invoke(this, MPacket);
         Close();
         try
         {
            MozStatic.BufferByteArrayPool.Return(RentedArray);
         }
         catch (Exception ex)
         {
            Logger.Log(ex.Message + Environment.NewLine + ex.StackTrace);
         }
      }

      public async Task SendDataToClientAsync(ArraySegment<byte> Data) //Send data from client to server
      {
         try
         {
            await tcpClientToClientStream.WriteAsync(Data);
         }
         catch (Exception ex)
         {
            Logger.Log(ex.StackTrace);
            Close();
         }
      }

      public void Close()
      {
         ConnectionClosed?.Invoke(this, ConnectionID);
         TcpClientToClient.Close();
         tcpClientToClientStream.Close();
      }
   }
}