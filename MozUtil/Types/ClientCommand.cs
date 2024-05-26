using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace MozUtil.Types
{
   public class ClientCommandUtils
   {
      public static byte[] BuildServerStatsRequestCommand(int interval)
      {
         if (interval < 15)
            throw new NotSupportedException("Bro how fast do you want the reports to be? 16ms or higher is more than enough imo.");
         return BitConverter.GetBytes(interval);
      }
      public static int ReadServerStatsRequestCommand(byte[] command)
      {
         return BitConverter.ToInt32(command, 0);
      }
      public static byte[] BuildCustomPipeCommand(CustomPipeInformation PipeInfo)
      {
         return BuildCustomPipeCommand(PipeInfo.IPAddress, PipeInfo.DestinationPort, PipeInfo.Channel, PipeInfo.SockProto, PipeInfo.DeliveryReliability);
      }
      public static byte[] BuildCustomPipeCommand(IPAddress DestinationIPAddress, int destinationPort, byte ChannelNumber, ProtocolType SocketProtocolType, LiteNetLib.DeliveryMethod Reliability)
      {
         if (SocketProtocolType == ProtocolType.Tcp && Reliability != LiteNetLib.DeliveryMethod.ReliableOrdered)
            throw new Exception("Unreliable TCP is on another level... can't let you do it for several reasons.");

         using (MemoryStream CommandStream = new MemoryStream())
         {
            byte[] AddressFamilyBytes = BitConverter.GetBytes((int)DestinationIPAddress.AddressFamily);
            CommandStream.Write(AddressFamilyBytes);
            byte[] IPBytes = DestinationIPAddress.GetAddressBytes();
            CommandStream.Write(IPBytes);
            byte[] PortBytes = BitConverter.GetBytes(destinationPort);
            CommandStream.Write(PortBytes);
            CommandStream.WriteByte(ChannelNumber);//Only matters if it's reliable. otherwise it can by any value. doesn't really matter as long as it's not null.
            byte[] ProtocolTypeBytes = BitConverter.GetBytes((int)SocketProtocolType);
            CommandStream.Write(ProtocolTypeBytes);
            byte[] ReliabilityBytes = BitConverter.GetBytes((int)Reliability);
            CommandStream.Write(ReliabilityBytes);
            CommandStream.Flush();
            return CommandStream.ToArray();
         }
      }
      public static CustomPipeInformation ReadPipeInfo(byte[] PipeInfoBytes, int Offset)
      {
         CustomPipeInformation PipeInfo = new CustomPipeInformation();
         int Position = Offset;

         AddressFamily IPAddressFamily = (AddressFamily)BitConverter.ToInt32(PipeInfoBytes, Position);

         Position += 4;
         if (IPAddressFamily == AddressFamily.InterNetwork)
         {
            //byte[] ipBytes = (byte[])Data.Take(new Range(Position, Position + 4));
            //byte[] ipBytes = (byte[])Data.Skip(Position).Take(Position + 4);
            byte[] ipBytes = PipeInfoBytes[Position..(Position + 4)];
            PipeInfo.IPAddress = new IPAddress(ipBytes);
            Position += 4;
         }
         else if (IPAddressFamily == AddressFamily.InterNetworkV6)
         {
            //byte[] ipBytes = (byte[])Data.Take(new Range(Position, Position + 16));
            //byte[] ipBytes = (byte[])Data.Skip(Position).Take(Position + 16);
            byte[] ipBytes = PipeInfoBytes[Position..(Position + 16)];
            PipeInfo.IPAddress = new IPAddress(ipBytes);
            Position += 16;
         }
         PipeInfo.DestinationPort = BitConverter.ToInt32(PipeInfoBytes, Position);
         Position += 4;
         PipeInfo.Channel = PipeInfoBytes[Position];
         Position += 1;
         PipeInfo.SockProto = (ProtocolType)BitConverter.ToInt32(PipeInfoBytes, Position);
         Position += 4;
         PipeInfo.DeliveryReliability = (LiteNetLib.DeliveryMethod)BitConverter.ToInt32(PipeInfoBytes, Position);

         return PipeInfo;
      }

   }
   public class ClientCommand
   {
      public ClientCommands CommandType { get; set; }
      public byte[]? CommandValue { get; set; }
   }
   public enum ClientCommands
   {
      //Command value: 4 bytes (32bit) signed integer for interval miliseconds
      RequestServerStats,
      //Command value: Destination address family (ipv4 or v6), Destination IP, Destination port (Binary),
      //Channel number (current client),ProtocolType(int from enum),
      //Reliability if protocoltype is udp(ignored if tcp)(int from enum)
      //OR (Not decided yet) ASCII encoded destination "ChannelNumber\nIP:Port\nProtocol:reliability(ignoredIfTCP)"
      OpenEndToEndCustomPipe
   }
}
