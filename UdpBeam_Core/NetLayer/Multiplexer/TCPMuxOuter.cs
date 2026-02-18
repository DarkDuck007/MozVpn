using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace UdpBeam_Core.NetLayer.Multiplexer
{
   public class TCPMuxOuter : INetOuter, IDisposable
   {
      private const int inboundDataChannelBufferSize = 512; //approx 2MB buffer. (with 4KB buffers)
      private const int perClientOutboundChannelBufferSize = 256; //approx 1MB buffer. (with 4KB buffers)
      private Pipe _internalPipe;
      private TcpListener _tcpListener;
      private Task? _listeningTask;
      private Task? _inboundChannelConsumerTask;
      private Task? _pipeReaderTask;
      private CancellationTokenSource? _cts;
      private Channel<TcpMuxClientData> _inboundDataChannel;
      private readonly ConcurrentDictionary<ushort, TCPMuxClient> _clients = new();
      public IPEndPoint? LocalEndpoint => (_tcpListener.LocalEndpoint as IPEndPoint);
      public int ActiveConnections => _clients.Count;
      public TCPMuxOuter(IPEndPoint localEP)
      {
         _internalPipe = new();
         _tcpListener = new TcpListener(localEP);
         _inboundDataChannel = Channel.CreateBounded<TcpMuxClientData>(inboundDataChannelBufferSize);
         //_inboundDataChannel = Channel.CreateUnbounded<TcpMuxClientData>();
      }
      public TCPMuxOuter(int port) : this(new IPEndPoint(IPAddress.Any, port))
      {
      }
      public TCPMuxOuter(IPAddress localaddr, int port) : this(new IPEndPoint(localaddr, port))
      {
      }
      public void InitializeOutput(Pipe OutputPipe)
      {
         _cts = new CancellationTokenSource();
         _tcpListener.Start();
         _listeningTask = RunListenerLoopAsync(_cts.Token);
         _inboundChannelConsumerTask = ConsumeDataChannelAsync(_cts.Token);
         _pipeReaderTask = ReadPipeIntoClientsAsync(_cts.Token);
         _internalPipe = OutputPipe;
      }
      private async Task ConsumeDataChannelAsync(CancellationToken ct)
      {
         PipeWriter MainPipeWriter = _internalPipe.Writer;
         Memory<byte> pipeMem;
         await foreach (var clientData in _inboundDataChannel.Reader.ReadAllAsync(ct))
         {
            pipeMem = _internalPipe.Writer.GetMemory(clientData.dataLength + 2);
            try
            {
               BitConverter.GetBytes(clientData.connectionID).CopyTo(pipeMem);
               clientData.data.CopyTo(pipeMem.Slice(2));
               MainPipeWriter.Advance(clientData.dataLength + 2);
               await MainPipeWriter.FlushAsync(ct);
            }
            finally
            {
               // Return the buffer to the pool
               ArrayPool<byte>.Shared.Return(clientData.data);
            }
         }
      }
      private async Task ReadPipeIntoClientsAsync(CancellationToken ct)
      {
         PipeReader reader = _internalPipe.Reader;
         ReadResult result;
         while (!ct.IsCancellationRequested)
         {
            result = await reader.ReadAsync(ct);
            ReadOnlySequence<byte> buffer = result.Buffer;
            if (buffer.Length > 0)
            {
               try
               {

                  SequencePosition consumed = ProcessBuffer(buffer, out SequencePosition examined);

                  // Tell the pipe what we did
                  reader.AdvanceTo(consumed, examined);

                  if (result.IsCompleted) break;
               }
               finally { }
            }

         }
         await reader.CompleteAsync();
      }
      private async Task<SequencePosition> ProcessBuffer(ReadOnlySequence<byte> Buffer)
      {

      }
      private void RouteToClient(ushort id, ReadOnlySequence<byte> payload)
      {
         // ONE lookup to find the client object
         if (_clients.TryGetValue(id, out var client))
         {
            byte[] copy = ArrayPool<byte>.Shared.Rent((int)payload.Length);
            payload.CopyTo(copy);

            // Accessing .Writer here is "free" thanks to JIT inlining
            if (!client.Writer.TryWrite(copy))
            {
               ArrayPool<byte>.Shared.Return(copy);
               // Optional: TerminateSlowClient(id);
            }
         }
      }
      private async Task RunListenerLoopAsync(CancellationToken ct)
      {
         try
         {
            ushort connectionIDCounter = 0;
            while (!ct.IsCancellationRequested)
            {
               if (_clients.ContainsKey(connectionIDCounter))
               {
                  Debug.WriteLine($"Connection ID {connectionIDCounter} already in use. Skipping.");
                  connectionIDCounter++;
                  continue;
               }
               if (_clients.Count >= ushort.MaxValue)
               {
                  Debug.WriteLine("Maximum number of connections reached. Cannot accept new clients.");
                  await Task.Delay(1000, ct); // Wait before checking again
                  continue;
               }
               TcpClient client = await _tcpListener.AcceptTcpClientAsync(ct);
               TCPMuxClient muxClient = new TCPMuxClient(connectionIDCounter, client, _inboundDataChannel.Writer, perClientOutboundChannelBufferSize, ct);
               muxClient.OnDisconnected += muxClient_OnDisconnected;
               //Task ClientTask = HandleClientAsync(client, connectionIDCounter++, ct).ContinueWith(t =>
               //{
               //   if (t.IsFaulted)
               //      Debug.WriteLine($"Error handling client: {t.Exception?.GetBaseException().Message}");
               //   _activeConnectionIDs.TryRemove(connectionIDCounter, out _);
               //   _outboundStreams.TryRemove(connectionIDCounter, out _);

               //});
               if (!_clients.TryAdd(connectionIDCounter, muxClient))
               {
                  Debug.WriteLine("Possible Connection ID collision. This should not happen.");
               }

            }
         }
         catch (OperationCanceledException) { /* Normal shutdown */ }
         catch (Exception ex)
         {
            Debug.WriteLine($"Listener crashed: {ex.Message}");
         }
         finally
         {
            _tcpListener.Stop();
         }
      }

        private void muxClient_OnDisconnected(ushort connectionID)
        {
             
        }

        //private async Task HandleClientAsync(TcpClient client, ushort connectionID, CancellationToken ct)
        //{
        //   using (client)
        //   {
        //      var localPipe = new Pipe(); // One pipe per client
        //      NetworkStream clientStream = client.GetStream();
        //      var fillTask = ReadToChannelAsync(client.GetStream(), localPipe.Writer, ct);
        //      var mergeTask = MergeToMainPipeAsync(connectionID, localPipe.Reader, ct);
        //      await Task.WhenAll(fillTask, mergeTask);

        //   }
        //}
        //private async Task ReadToChannelAsync(NetworkStream stream, PipeWriter writer, CancellationToken ct)
        //{
        //   try
        //   {
        //      while (!ct.IsCancellationRequested)
        //      {
        //         var buffer = ArrayPool<byte>.Shared.Rent(4096);

        //         BinaryPrimitives.ReadUInt16LittleEndian(connectionID).CopyTo(buffer);
        //         int bytesRead = await clientStream.ReadAsync(buffer.Slice(2), ct);
        //         if (bytesRead == 0) break; // Client disconnected
        //         _internalPipe.Writer.Advance(bytesRead + 2);
        //         await _internalPipe.Writer.FlushAsync(ct);
        //      }
        //   }
        //   catch (OperationCanceledException) { /* Normal shutdown */ }
        //   catch (Exception ex)
        //   {
        //      Debug.WriteLine($"Error in client {connectionID}: {ex.Message}");
        //   }
        //   while (true)
        //   {
        //      Memory<byte> memory = writer.GetMemory(4096);
        //      int bytesRead = await stream.ReadAsync(memory, ct);
        //      if (bytesRead == 0) break;

        //      writer.Advance(bytesRead);
        //      await writer.FlushAsync(ct);
        //   }
        //   await writer.CompleteAsync();
        //}

        public void Dispose()
      {
         _cts?.Cancel();
         _tcpListener.Stop();
         _cts?.Dispose();

      }
   }
}
