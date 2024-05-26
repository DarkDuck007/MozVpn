using System;
using System.Buffers;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MozUtil.NatUtils;
using MozUtil.Types;
using STUN;

namespace MozUtil
{
   public class MozStatic
   {
      public static ArrayPool<byte> BufferByteArrayPool { get; } = ArrayPool<byte>.Create(4096, 512);

      public static byte[] SerializePunchInfo(STUNQueryResult stunResult, int HolePunchTimeout)
      {
         using (MemoryStream SendDataMemS = new MemoryStream())
         {
            SendDataMemS.WriteByte((byte)stunResult.NATType);

            byte[] AddressFamilyBytes = BitConverter.GetBytes((int)stunResult.PublicEndPoint.AddressFamily);
            SendDataMemS.Write(AddressFamilyBytes);

            byte[] ipAddressBytes = stunResult.PublicEndPoint.Address.GetAddressBytes();
            SendDataMemS.Write(ipAddressBytes);

            byte[] HolePunchTimeoutBytes = BitConverter.GetBytes(HolePunchTimeout);
            SendDataMemS.Write(HolePunchTimeoutBytes);

            byte[] portBytes =
               BitConverter.GetBytes(stunResult.PublicEndPoint.Port); //integer - should be PortRange for symmetric NATs
            SendDataMemS.Write(portBytes);

            byte[] portCountBytes = BitConverter.GetBytes(1); //integer 
            SendDataMemS.Write(portCountBytes);

            SendDataMemS.Flush();
            return SendDataMemS.ToArray();
         }
      }

      public static byte[] SerializeUdpConnectionInfo(TransportMode udpMode, IPAddress ip, int port,
         byte[] ConnectionKey = null)
      {
         using (MemoryStream SendDataMemS = new MemoryStream())
         {
            SendDataMemS.WriteByte((byte)udpMode);

            byte[] AddressFamilyBytes = BitConverter.GetBytes((int)ip.AddressFamily);
            SendDataMemS.Write(AddressFamilyBytes);

            byte[] ipAddressBytes = ip.GetAddressBytes();
            SendDataMemS.Write(ipAddressBytes);

            byte[] portBytes = BitConverter.GetBytes(port); //integer 
            SendDataMemS.Write(portBytes);

            if (ConnectionKey != null) SendDataMemS.Write(ConnectionKey);

            SendDataMemS.Flush();
            return SendDataMemS.ToArray();
         }
      }

      public static udpConnectionInfo DeserializeUdpConnectionInfo(byte[] Data, int Offset)
      {
         int Position = Offset;
         udpConnectionInfo udpInfo = new udpConnectionInfo();
         udpInfo.udpMode = (TransportMode)Data[Position];
         Position += 1;
         udpInfo.addressFamily = (AddressFamily)BitConverter.ToInt32(Data, Position);
         Position += 4;
         if (udpInfo.addressFamily == AddressFamily.InterNetwork)
         {
            //byte[] ipBytes = (byte[])Data.Take(new Range(Position, Position + 4));
            //byte[] ipBytes = (byte[])Data.Skip(Position).Take(Position + 4);
            byte[] ipBytes = Data[Position..(Position + 4)];
            udpInfo.ipAddress = new IPAddress(ipBytes);
            Position += 4;
         }
         else if (udpInfo.addressFamily == AddressFamily.InterNetworkV6)
         {
            //byte[] ipBytes = (byte[])Data.Take(new Range(Position, Position + 16));
            //byte[] ipBytes = (byte[])Data.Skip(Position).Take(Position + 16);
            byte[] ipBytes = Data[Position..(Position + 16)];
            udpInfo.ipAddress = new IPAddress(ipBytes);
            Position += 16;
         }
         else
         {
            throw new NotSupportedException("Unsupported Address Family. Only IPv4 and IPv6 are supported.");
         }

         udpInfo.Port = BitConverter.ToInt32(Data, Position);

         Position += 4;
         if (Data.Length > Position)
            udpInfo.ConnectionKey = Convert.ToBase64String(Data, Position, Data.Length - Position);
         //udpInfo.ConnectionKey = Encoding.ASCII.GetString(Data, Position, Data.Length - Position);
         return udpInfo;
      }

      public static byte[] SerializePunchInfo(STUNQueryResult stunResult, int PortBegin, int PortCount,
         int HolePunchTimeout)
      {
         using (MemoryStream SendDataMemS = new MemoryStream())
         {
            SendDataMemS.WriteByte((byte)stunResult.NATType);

            byte[] AddressFamilyBytes = BitConverter.GetBytes((int)stunResult.PublicEndPoint.AddressFamily);
            SendDataMemS.Write(AddressFamilyBytes);

            byte[] ipAddressBytes = stunResult.PublicEndPoint.Address.GetAddressBytes();
            SendDataMemS.Write(ipAddressBytes);

            byte[] HolePunchTimeoutBytes = BitConverter.GetBytes(HolePunchTimeout);
            SendDataMemS.Write(HolePunchTimeoutBytes);

            byte[] portBytes = BitConverter.GetBytes(PortBegin); //integer 
            SendDataMemS.Write(portBytes);

            byte[] portCountBytes = BitConverter.GetBytes(PortCount); //integer 
            SendDataMemS.Write(portCountBytes);

            SendDataMemS.Flush();
            return SendDataMemS.ToArray();
         }
      }

      public static HolePunchPeerInfo DeserializePunchInfo(byte[] Data, int Offset)
      {
         int Position = Offset;
         HolePunchPeerInfo Peerinfo = new HolePunchPeerInfo();

         Peerinfo.NatType = (STUNNATType)Data[Position];
         Position += 1;
         Peerinfo.addressFamily = (AddressFamily)BitConverter.ToInt32(Data, Position);
         Position += 4;
         if (Peerinfo.addressFamily == AddressFamily.InterNetwork)
         {
            //byte[] ipBytes = (byte[])Data.Take(new Range(Position, Position + 4));
            //byte[] ipBytes = (byte[])Data.Skip(Position).Take(Position + 4);
            byte[] ipBytes = Data[Position..(Position + 4)];
            Peerinfo.ipAddress = new IPAddress(ipBytes);
            Position += 4;
         }
         else if (Peerinfo.addressFamily == AddressFamily.InterNetworkV6)
         {
            //byte[] ipBytes = (byte[])Data.Take(new Range(Position, Position + 16));
            //byte[] ipBytes = (byte[])Data.Skip(Position).Take(Position + 16);
            byte[] ipBytes = Data[Position..(Position + 16)];
            Peerinfo.ipAddress = new IPAddress(ipBytes);
            Position += 16;
         }
         else
         {
            throw new NotSupportedException("Unsupported Address Family. Only IPv4 and IPv6 are supported.");
         }

         Peerinfo.HolePunchTimeout = BitConverter.ToInt32(Data, Position);
         Position += 4;
         Peerinfo.Port = BitConverter.ToInt32(Data, Position);
         Position += 4;
         Peerinfo.PortsCount = BitConverter.ToInt32(Data, Position);
         return Peerinfo;
      }

      public static async Task KeepStreamAliveAsync(Stream s, int interval = 30000, CancellationToken CT = default,
         string KeepAliveMessage = ":D")
      {
         bool Aborted = false;
         CT.Register(() => { Aborted = true; });
         while (!Aborted)
            try
            {
               await WriteLineString(s, ":D");
               await Task.Delay(interval);
            }
            catch (Exception)
            {
               Aborted = true;
               return;
            }
      }

      public static async Task WriteLineString(Stream S, string DataToWrite)
      {
         if (S == null || DataToWrite == null) return;
         await S.WriteAsync(Encoding.ASCII.GetBytes(DataToWrite + Environment.NewLine));
         await S.FlushAsync();
      }

     

   }
}