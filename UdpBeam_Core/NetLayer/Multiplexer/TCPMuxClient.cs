
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace UdpBeam_Core.NetLayer.Multiplexer
{
   public class TCPMuxClient : IDisposable
   {
      private ushort _connectionID;
      private TcpClient _innerClient;
      private Channel<byte[]> _outboundDataChannel;
      private NetworkStream _innerClientNetworkStream;

      private Task _outboundChannelConsumerTask;
      private Task _clientInboundReaderTask;
      private ChannelWriter<TcpMuxClientData> _inboundWriter;
      public NetworkStream ClientNetworkStream => _innerClientNetworkStream;
      public ChannelWriter<byte[]> OutboundChannelWriter => _outboundDataChannel.Writer;
      public delegate void ClientDisconnectedHandler(ushort connectionID);
      public ClientDisconnectedHandler? OnDisconnected;
      private CancellationToken ct;
      public TCPMuxClient(ushort id, TcpClient client, ChannelWriter<TcpMuxClientData> inboundWriter, int outboundChannelBufferSize = 256, CancellationToken ct = default, ClientDisconnectedHandler? onDisconnected = null)
      {
         _connectionID = id;
         _innerClient = client;
         _innerClientNetworkStream = client.GetStream();
         _inboundWriter = inboundWriter;
         _outboundDataChannel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(outboundChannelBufferSize) { SingleReader = true, SingleWriter = true });
         _outboundChannelConsumerTask = ConsumeOutboundChannelAsync();
         _clientInboundReaderTask = ReadFromClientToInboundWriterAsync();
         OnDisconnected = onDisconnected;
         this.ct = ct;
      }
      private async Task ConsumeOutboundChannelAsync()
      {

         OnDisconnected?.Invoke(_connectionID);
      }
      private async Task ReadFromClientToInboundWriterAsync()
      {

      }
      public void Dispose()
      {
         throw new NotImplementedException();
      }
   }
}
