using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using MozUtil;

namespace DirtySocksASP;

/// <summary>
/// Minimal unauthenticated SOCKS5 CONNECT server used as the tunnel's local exit.
/// </summary>
internal sealed class SimpleSocks5Server : IDisposable
{
   private readonly TcpListener _listener;
   private readonly CancellationTokenSource _stop = new();

   public SimpleSocks5Server(IPEndPoint endpoint) => _listener = new TcpListener(endpoint);

   public async Task StartAsync()
   {
      _listener.Start();
      try
      {
         while (!_stop.IsCancellationRequested)
         {
            TcpClient client = await _listener.AcceptTcpClientAsync(_stop.Token);
            _ = HandleClientAsync(client, _stop.Token);
         }
      }
      catch (OperationCanceledException) when (_stop.IsCancellationRequested)
      {
      }
   }

   public void Stop()
   {
      if (_stop.IsCancellationRequested)
         return;
      _stop.Cancel();
      _listener.Stop();
   }

   private static async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
   {
      using (client)
      {
         NetworkStream source = client.GetStream();
         try
         {
            byte[] greeting = await ReadBytesAsync(source, 2, cancellationToken);
            if (greeting[0] != 5)
               return;

            byte[] methods = await ReadBytesAsync(source, greeting[1], cancellationToken);
            if (!methods.Contains((byte)0))
            {
               await source.WriteAsync(new byte[] { 5, 0xff }, cancellationToken);
               return;
            }
            await source.WriteAsync(new byte[] { 5, 0 }, cancellationToken);

            byte[] request = await ReadBytesAsync(source, 4, cancellationToken);
            if (request[0] != 5 || request[1] != 1)
            {
               await WriteReplyAsync(source, 7, null, cancellationToken);
               return;
            }

            string host = request[3] switch
            {
               1 => new IPAddress(await ReadBytesAsync(source, 4, cancellationToken)).ToString(),
               3 => await ReadDomainAsync(source, cancellationToken),
               4 => new IPAddress(await ReadBytesAsync(source, 16, cancellationToken)).ToString(),
               _ => throw new InvalidDataException("Unsupported SOCKS5 address type.")
            };
            int port = BinaryPrimitives.ReadUInt16BigEndian(await ReadBytesAsync(source, 2, cancellationToken));

            using TcpClient destination = new();
            try
            {
               await destination.ConnectAsync(host, port, cancellationToken);
            }
            catch (SocketException ex)
            {
               await WriteReplyAsync(source, MapSocketError(ex.SocketErrorCode), null, cancellationToken);
               return;
            }

            await WriteReplyAsync(source, 0, destination.Client.LocalEndPoint as IPEndPoint, cancellationToken);
            NetworkStream target = destination.GetStream();
            using CancellationTokenSource closed = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task upstream = source.CopyToAsync(target, closed.Token);
            Task downstream = target.CopyToAsync(source, closed.Token);
            await Task.WhenAny(upstream, downstream);
            closed.Cancel();
            await IgnorePipeEndAsync(upstream);
            await IgnorePipeEndAsync(downstream);
         }
         catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
         {
         }
         catch (EndOfStreamException)
         {
         }
         catch (Exception ex)
         {
            Logger.Log($"SOCKS5 connection failed: {ex.Message}");
         }
      }
   }

   private static async Task<string> ReadDomainAsync(Stream stream, CancellationToken cancellationToken)
   {
      int length = (await ReadBytesAsync(stream, 1, cancellationToken))[0];
      return System.Text.Encoding.ASCII.GetString(await ReadBytesAsync(stream, length, cancellationToken));
   }

   private static async Task<byte[]> ReadBytesAsync(Stream stream, int length, CancellationToken cancellationToken)
   {
      byte[] buffer = new byte[length];
      int offset = 0;
      while (offset < length)
      {
         int read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken);
         if (read == 0)
            throw new EndOfStreamException();
         offset += read;
      }
      return buffer;
   }

   private static async Task WriteReplyAsync(Stream stream, byte result, IPEndPoint? endpoint, CancellationToken cancellationToken)
   {
      endpoint ??= new IPEndPoint(IPAddress.Any, 0);
      byte[] address = endpoint.Address.GetAddressBytes();
      byte[] response = new byte[4 + address.Length + 2];
      response[0] = 5;
      response[1] = result;
      response[3] = endpoint.AddressFamily == AddressFamily.InterNetworkV6 ? (byte)4 : (byte)1;
      address.CopyTo(response, 4);
      BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(4 + address.Length), (ushort)endpoint.Port);
      await stream.WriteAsync(response, cancellationToken);
   }

   private static byte MapSocketError(SocketError error) => error switch
   {
      SocketError.NetworkUnreachable => 3,
      SocketError.HostUnreachable or SocketError.HostNotFound => 4,
      SocketError.ConnectionRefused => 5,
      SocketError.TimedOut => 6,
      _ => 1
   };

   private static async Task IgnorePipeEndAsync(Task task)
   {
      try { await task; }
      catch (OperationCanceledException) { }
      catch (IOException) { }
   }

   public void Dispose()
   {
      Stop();
      _stop.Dispose();
   }
}
