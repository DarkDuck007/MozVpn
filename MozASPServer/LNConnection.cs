using System.Net.Sockets;
using System.Net;
using MozUtil;
using System.Buffers;
using LiteNetLib;

namespace DirtySocksASP
{
   public class LNConnection : IDisposable
   {
      int _closed;
      int MaxOutboundPackets { get; set; }
      NetManager LiteNetManager { get; set; }
      int CopyBufferLength = 4094;
      public ushort ConnectionID { get; set; }
      public int PeerID { get; set; }
      public TcpClient TcpClientToServer { get; set; }
      public byte BoundChannelID { get; set; }
      NetworkStream tcpClientToServerStream;
      public event EventHandler<ushort>? ConnectionClosed;
      public event EventHandler<MozPacket>? DataAvailable;
      public LNConnection(ushort _ConnectionID, byte _ChannelID, TcpClient _Client, int _PeerID, ref NetManager LNManager, int MaxOutPackets)
      {
         BoundChannelID = _ChannelID;
         ConnectionID = _ConnectionID;
         TcpClientToServer = _Client;
         TcpClientToServer.NoDelay = true;
         PeerID = _PeerID;
         tcpClientToServerStream = TcpClientToServer.GetStream();
         LiteNetManager = LNManager;
         MaxOutboundPackets = MaxOutPackets;
      }
      public async Task StartConnectionAsync()
      {

         var RentedArray = MozStatic.BufferByteArrayPool.Rent(CopyBufferLength + 2);
         MozPacket MPacket = new MozPacket() { RawData = RentedArray };
         MPacket.ChannelID = BoundChannelID;
         MPacket.PeerID = PeerID;
         BitConverter.GetBytes(ConnectionID).CopyTo(MPacket.RawData, 0);
         try
         {
            int i = 0;
            while ((i = await tcpClientToServerStream.ReadAsync(MPacket.RawData, 2, CopyBufferLength)) > 0)
            {
               try
               {
                  //Console.WriteLine($"Server is sending {i} ({i+2}) bytes to a client Con ID {ConnectionID} channel {BoundChannelID}");
                  MPacket.StartIndex = 0; MPacket.Length = i + 2;
                  //await UdpSrv.SendAsync(ReadBuffer, i + 2, ClientRemoteEndpoint);
                  //while (LiteNetManager.GetPeerById(MPacket.PeerID).GetPacketsCountInReliableQueue(MPacket.ChannelID, true) > MaxOutboundPackets)
                  //{
                  //   System.Threading.Thread.Sleep(2);
                  //}
                  //LiteNetManager.GetPeerById(MPacket.PeerID).Send(MPacket.RawData, MPacket.StartIndex, MPacket.Length, MPacket.ChannelID, DeliveryMethod.ReliableOrdered);
                  DataAvailable?.Invoke(this, MPacket);
               }
               catch (Exception)
               {
                  //Logger.Log(ex.StackTrace);
                  break;
               }
               //Logger.Log($"Server is sending {i + 2} bytes to a client Con ID {ConnectionID}");
            }
         }
         catch (Exception)
         {
            //Logger.Log(ex.Message + Environment.NewLine + ex.StackTrace);
         }
         byte[] CloseConReqBuffer = new byte[4];
         BitConverter.GetBytes(ConnectionID).CopyTo(CloseConReqBuffer, 2);
         try
         {
            ArrayPool<byte>.Shared.Return(RentedArray);
         }
         catch (Exception ex)
         {
            Logger.Log(ex.Message);
         }
         MPacket.Length = CloseConReqBuffer.Length;
         MPacket.RawData = CloseConReqBuffer;
         DataAvailable?.Invoke(this, MPacket);
         this.Close();
      }
      public async Task SendDataToServerAsync(ArraySegment<byte> Data) //Send data from client to server
      {
         try
         {
            //Console.WriteLine($"Server received {Data.Length} ({Data.Length + 2}) bytes from a client Con ID {ConnectionID} on channel {BoundChannelID}");
            await tcpClientToServerStream.WriteAsync(Data);
         }
         catch (Exception)
         {
            //Logger.Log(ex.Message + ex.StackTrace);
            this.Close();
         }
      }
      public void Close()
      {
         if (Interlocked.Exchange(ref _closed, 1) != 0)
            return;
         ConnectionClosed?.Invoke(this, ConnectionID);
         ConnectionClosed = null;
         DataAvailable = null;
         TcpClientToServer.Close();
         tcpClientToServerStream.Close();
      }
      public void Dispose()
      {
         this.Close();
      }
   }
}
