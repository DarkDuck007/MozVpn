using System.Net.Sockets;
using System.Net;
using System;

namespace MozUtil.Types
{
   internal class Types
   {
   }

   public struct ServerStatusInformation
   {
      //Ticks
      public int LastTcpSocketCreationLatency { get; set; }
      //Ticks
      public int AverageTcpSocketCreationLatency { get; set; }
      public int TotalTcpSocketCreations { get; set; }
      public int ActiveTcpSockets { get; set; }
      public int TotalLNServers { get; set; }
      public int TotalThreads { get; set; }
      public long GCTotalMemory { get; set; }
      public long GCMemoryLoadBytes { get; set; }
      public long GCHeapSizeBytes { get; set; }
      public long GCTotalCommittedBytes { get; set; }

      //Maybe?
      public long TotalUpstreamBytes { get; set; }
      public long TotalDownstreamBytes { get; set; }
      //Ticks I reckon.
      public long Uptime { get; set; }
      public long TotalKeepAliveHttpConnections { get; set; }
      
   }
   public class CustomPipeInformation
   {
      public CustomPipeInformation() { }
      public IPAddress? IPAddress { get; set; }
      public int DestinationPort { get; set; }
      public byte Channel { get; set; }
      public ProtocolType SockProto { get; set; }
      public LiteNetLib.DeliveryMethod DeliveryReliability { get; set; }

      public CustomPipeInformation(IPAddress DestinationIPAddress, int destinationPort, byte ChannelNumber, ProtocolType SocketProtocolType, LiteNetLib.DeliveryMethod Reliability)
      {
         IPAddress = DestinationIPAddress;
         DestinationPort = destinationPort;
         Channel = ChannelNumber;
         SockProto = SocketProtocolType;
         DeliveryReliability = Reliability;
      }
   }
}