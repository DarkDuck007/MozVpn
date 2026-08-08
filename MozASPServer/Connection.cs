using Microsoft.AspNetCore.SignalR.Protocol;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using MozUtil;
namespace DirtySocksASP
{
   internal class Connection
   {
      int CopyBufferLength = 1300;
      private ushort _ConnectionID;
      public ushort ConnectionID
      {
         get { return _ConnectionID; }
         set { _ConnectionID = value; }
      }
      private TcpClient _TcpClientToServer;
      public TcpClient TcpClientToServer
      {
         get { return _TcpClientToServer; }
         set { _TcpClientToServer = value; }
      }
      NetworkStream tcpClientToServerStream;
      private IPEndPoint _RemoteEndpoint;

      public IPEndPoint ClientRemoteEndpoint
      {
         get { return _RemoteEndpoint; }
         set { _RemoteEndpoint = value; }
      }
      UdpClient UdpSrv;
      public event EventHandler<ushort>? ConnectionClosed;
      public Connection(ushort ConnectionID, TcpClient Client, IPEndPoint RemoteEP, UdpClient _UdpSrv)
      {
         _ConnectionID = ConnectionID;
         _TcpClientToServer = Client;
         _RemoteEndpoint = RemoteEP;
         UdpSrv = _UdpSrv;
         tcpClientToServerStream = Client.GetStream();
      }
      public void Close()
      {
         ConnectionClosed?.Invoke(this, ConnectionID);
         tcpClientToServerStream.Close();
         //tcpClientToServerStream.Dispose();
         _TcpClientToServer.Close();
         //_TcpClientToServer.Dispose();
      }
      public async Task HandleWriteConnectionAsync() //Reads from server and sends to client
      {
         TcpClientToServer.NoDelay = true;
         byte[] ReadBuffer = new byte[CopyBufferLength + 2];
         BitConverter.GetBytes(ConnectionID).CopyTo(ReadBuffer, 0);
         try
         {
            await Task.Run(async () =>
            {
               try
               {
                  //EncryptionProvider EncProv = new EncryptionProvider();
                  int i = 0;
                  while ((i = await tcpClientToServerStream.ReadAsync(ReadBuffer, 2, CopyBufferLength)) > 0)
                  {
                     //byte[] SendBufferEnc = EncProv.Encrypt(ReadBuffer,0,i+2);
                     //await UdpSrv.SendAsync(SendBufferEnc, SendBufferEnc.Length, ClientRemoteEndpoint);
                     try
                     {
                        await UdpSrv.SendAsync(ReadBuffer, i + 2, ClientRemoteEndpoint);
                     }
                     catch (Exception ex)
                     {
                        Logger.Log(ex.StackTrace);
                        break;
                     }
                     //Console.WriteLine("Server is sending {0} bytes to a client Con ID {1}", i + 2, ConnectionID);
                     //Logger.Log($"Server is sending {i + 2} bytes to a client Con ID {ConnectionID}");
                  }

               }
               catch (Exception)
               {
                  //Logger.Log(ex.Message + Environment.NewLine + ex.StackTrace);
               }
            });
            byte[] CloseConReqBuffer = new byte[4];
            BitConverter.GetBytes(ConnectionID).CopyTo(CloseConReqBuffer, 2);
            await UdpSrv.SendAsync(CloseConReqBuffer, CloseConReqBuffer.Length, ClientRemoteEndpoint);
         }
         catch (Exception ex)
         {
            Logger.Log(ex.Message + Environment.NewLine + ex.StackTrace);
         }
         this.Close();
         //_TcpClientToServer.Dispose();
         //tcpClientToServerStream.Dispose();
         //UdpSrv.Dispose();
         //ConnectionClosed?.Invoke(this, ConnectionID);
      }
      public async Task SendDataToServerAsync(ArraySegment<byte> Data) //Send data from client to server
      {
         try
         {
            await tcpClientToServerStream.WriteAsync(Data);
         }
         catch (Exception ex)
         {
            Logger.Log(ex.StackTrace);
         }
         //Console.WriteLine("Server received {0} bytes of real data from a client", Data.Count);
         //Logger.Log($"Server received {Data.Count} bytes of real data from a client");
      }
   }
}
