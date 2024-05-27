using LiteNetLib;
using MozUtil.Clients;
using MozUtil.NatUtils;
using MozUtil.Types;
using System.Net;
using System.Net.Sockets;
using System.Reflection;

namespace BinaryConverterTests
{
   internal class Program
   {

      static void Main(string[] args)
      {
         //Console.WriteLine("Test 1");
         //CustomPipeInformation pipeInformation = new CustomPipeInformation(IPAddress.Parse("100.100.100.100"), 1000, 9, ProtocolType.Udp, DeliveryMethod.ReliableOrdered);
         //byte[] CommandBytes = ClientCommandUtils.BuildCustomPipeCommand(pipeInformation);

         //CustomPipeInformation Pipeinfo2 = ClientCommandUtils.ReadPipeInfo(CommandBytes, 0);

         //Console.WriteLine($"{pipeInformation.IPAddress.ToString()} vs {Pipeinfo2.IPAddress.ToString()} = {pipeInformation.IPAddress.Equals(Pipeinfo2.IPAddress)}");
         //Console.WriteLine($"{pipeInformation.DestinationPort} vs {Pipeinfo2.DestinationPort} = {pipeInformation.DestinationPort == Pipeinfo2.DestinationPort}");
         //Console.WriteLine($"{pipeInformation.Channel} vs {Pipeinfo2.Channel} = {pipeInformation.Channel == Pipeinfo2.Channel}");
         //Console.WriteLine($"{pipeInformation.SockProto} vs {Pipeinfo2.SockProto} = {pipeInformation.SockProto == Pipeinfo2.SockProto}");
         //Console.WriteLine($"{pipeInformation.DeliveryReliability} vs {Pipeinfo2.DeliveryReliability} = {pipeInformation.DeliveryReliability == Pipeinfo2.DeliveryReliability}");

         //Console.WriteLine("Test 2");
         //int interval = 3000;
         //byte[] StatsReqData = ClientCommandUtils.BuildServerStatsRequestCommand(interval);
         //int interval2 = ClientCommandUtils.ReadServerStatsRequestCommand(StatsReqData);
         //Console.WriteLine($"{interval} vs {interval2} = {interval == interval2}");
         Random RND = new Random();
         ServerStatusInformation StatusInfo = new ServerStatusInformation();
         //foreach (PropertyInfo item in members)
         //{
         //   if (item.Name == "TotalUpstreamBytes" || item.Name == "TotalDownstreamBytes" || item.Name == "Uptime")
         //   {
         //      item.SetValue(StatusInfo, RND.Next(1000, 1000000));
         //   }
         //   else
         //   {
         //      item.SetValue(StatusInfo, RND.Next(1, 100));
         //   }
         //}

         StatusInfo.LastTcpSocketCreationLatency = RND.Next(1, 100);
         StatusInfo.AverageTcpSocketCreationLatency = RND.Next(1, 100);
         StatusInfo.TotalTcpSocketCreations = RND.Next(1, 100);
         StatusInfo.ActiveTcpSockets = RND.Next(1, 100);
         StatusInfo.TotalLNServers = RND.Next(1, 100);
         StatusInfo.TotalThreads = RND.Next(1, 100);
         StatusInfo.GCTotalMemory = RND.Next(1, 100);
         StatusInfo.GCMemoryLoadBytes = RND.Next(1, 100);
         StatusInfo.GCHeapSizeBytes = RND.Next(1, 100);
         StatusInfo.GCTotalCommittedBytes = RND.Next(1, 100);
         StatusInfo.TotalUpstreamBytes = RND.Next(1, 100000);
         StatusInfo.TotalDownstreamBytes = RND.Next(1, 100000);
         StatusInfo.Uptime = RND.Next(1, 10000000);
         StatusInfo.TotalKeepAliveHttpConnections = RND.Next(1, 100);

         byte[] StatusInfoBytes = ServerCommandUtils.BuildServerStatusInformation(StatusInfo);
         Console.WriteLine("Bytes length: " + StatusInfoBytes.Length);

         ServerStatusInformation StatusInfo2 = ServerCommandUtils.ServerStatusInformationFromBytes(StatusInfoBytes, 0);

         PropertyInfo[] ServerStatusInformationPropertyInfo = typeof(ServerStatusInformation).GetProperties();
         foreach (PropertyInfo item in ServerStatusInformationPropertyInfo)
         {
            Console.WriteLine($"{item.GetValue(StatusInfo)} vs {item.GetValue(StatusInfo2)} = {item.GetValue(StatusInfo).Equals(item.GetValue(StatusInfo2))}");
         }
         Console.ReadLine();
      }
   }
}
