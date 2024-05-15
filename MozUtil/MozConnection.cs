using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace MozUtil
{
   internal class MozConnection : IMozConnection
   {
      private readonly int CopyBufferLength = 1300;
      private readonly NetworkStream tcpClientStream;
      private readonly UdpClient UdpCli;

      public MozConnection(TcpClient Client, ushort ID, IPEndPoint RemoteEP, UdpClient _UdpCli)
      {
         ConnectionID = ID;
         TcpClient = Client;
         ServerRemoteEndpoint = RemoteEP;
         UdpCli = _UdpCli;
         tcpClientStream = TcpClient.GetStream();
      }

      public ushort ConnectionID { get; set; }

      public IPEndPoint ServerRemoteEndpoint { get; set; }

      public TcpClient TcpClient { get; set; }

      public event EventHandler<ushort> ConnectionClosed;

      public async Task HandleReadConnectionAsync()
      {
         //EncryptionProvider EncProv = new EncryptionProvider();
         TcpClient.NoDelay = true;
         byte[] ReadBuffer = new byte[CopyBufferLength + 2];
         BitConverter.GetBytes(ConnectionID).CopyTo(ReadBuffer, 0);
         await Task.Run(async () =>
         {
            int i = 0;
            try
            {
               while ((i = await tcpClientStream.ReadAsync(ReadBuffer, 2, CopyBufferLength)) > 0)
                  try
                  {
                     //Interlocked.Increment(ref Program.OutPkRate);
                     //Console.WriteLine("Client is sending {0} bytes to the server", i + 2);
                     //byte[] EncSendData = await EncProv.EncryptAsync(ReadBuffer, 0, i + 2);
                     //await UdpCli.SendAsync(EncSendData, EncSendData.Length, ServerRemoteEndpoint);
                     await UdpCli.SendAsync(ReadBuffer, i + 2, ServerRemoteEndpoint);
                  }
                  catch (Exception ex)
                  {
                     Console.WriteLine(ex.Message);
                  }
            }
            catch (Exception ex)
            {
               //Console.WriteLine($"MozConnection.cs in TcpReader loop: {ex.Message}");
            }

            byte[] CloseConReqBuffer = new byte[4];
            BitConverter.GetBytes(ConnectionID).CopyTo(CloseConReqBuffer, 2);
            await UdpCli.SendAsync(CloseConReqBuffer, CloseConReqBuffer.Length, ServerRemoteEndpoint);
            Close();
            //TcpClient.Dispose();
            //tcpClientStream.Dispose();
            //UdpCli.Dispose();
            ConnectionClosed?.Invoke(this, ConnectionID);
         });
      }

      public void Close()
      {
         tcpClientStream.Close();
         //tcpClientStream.Dispose();
         TcpClient.Close();
         //_TcpClient.Dispose();
         ConnectionClosed?.Invoke(this, ConnectionID);
      }

      public async Task SendDataWriteAsync(ArraySegment<byte> Data)
      {
         try
         {
            await tcpClientStream.WriteAsync(Data.ToArray(), 0, Data.Count);
            //Interlocked.Increment(ref Program.InPkRate);

            //Console.WriteLine($"{Data.Count} received from the server");
         }
         catch
         {
            Close();
         }
      }
   }
}