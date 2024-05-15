using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using STUN;
using STUN.Attributes;

namespace MozUtil.NatUtils
{
   public static class MozStun
   {
      public static STUNQueryResult GetStunResult(Socket SockToUse, string Address, int Timeout = 2000)
      {
         //string StunServerDef = "stun.schlund.de:3478";
         //string StunServerOrg = "stunserver.stunprotocol.org:3478";
         if (!STUNUtils.TryParseHostAndPort(Address, out IPEndPoint stunEndPoint))
            throw new Exception("Failed to resolve STUN server address");

         STUNClient.ReceiveTimeout = Timeout;
         //var queryResult = STUNClient.Query(stunEndPoint, STUNQueryType.ExactNAT, true);
         var queryResult =
            STUNClient.Query(SockToUse, stunEndPoint, STUNQueryType.ExactNAT, NATTypeDetectionRFC.Rfc3489);
         //if (queryResult.QueryError != STUNQueryError.Success)
         //   throw new Exception("Query Error: " + queryResult.QueryError.ToString());

         //Console.WriteLine("PublicEndPoint: {0}", queryResult.PublicEndPoint);
         //Console.WriteLine("LocalEndPoint: {0}", queryResult.LocalEndPoint);
         //Console.WriteLine("NAT Type: {0}", queryResult.NATType);
         return queryResult;
      }

      public static PortRange GetPortRange(int StunCount, string StunAddress, int StunTimeout = 5000)
      {
         List<int> PublicPortsList = new List<int>();
         List<STUNQueryResult> StunResults = new List<STUNQueryResult>();
         List<UdpClient> udpClientsList = new List<UdpClient>();
         List<Task> TasksList = new List<Task>();
         Logger.Log($"Making {StunCount} Stun requests to determine port range...");
         while (udpClientsList.Count < StunCount) udpClientsList.Add(new UdpClient());
         foreach (UdpClient item in udpClientsList)
         {
            var T = Task.Run(() =>
            {
               try
               {
                  var StunRes = GetStunResult(item.Client, StunAddress, StunTimeout);
                  if (StunRes.QueryError == STUNQueryError.Success)
                  {
                     StunResults.Add(StunRes);
                     PublicPortsList.Add(StunRes.PublicEndPoint.Port);
                  }
               }
               catch
               {
               }
            });
            TasksList.Add(T);
         }

         Logger.Log($"Waiting for the {TasksList.Count} tasks to finish...");
         Task.WaitAll(TasksList.ToArray());
         Logger.Log($"Tasks finished, successful stuns: {StunResults.Count}/{StunCount}");
         if (StunResults.Count == 0) throw new Exception("No successful stuns :(");
         foreach (int item in PublicPortsList) Console.Write(item + ", ");
         Console.WriteLine("\b\b \b");
         bool Not5Digits = false;
         bool Has5Digits = false;
         Dictionary<int, int> FirstTwoDigits = new Dictionary<int, int>();
         foreach (int item in PublicPortsList)
         {
            var Portstring = item.ToString();
            if (Portstring.Length < 5)
               Not5Digits = true;
            else if (Portstring.Length == 5) Has5Digits = true;
            int FTDG = int.Parse(Portstring.Substring(0, 2));
            //FirstTwoDigits.Add(FTDG, 0);
            if (FirstTwoDigits.ContainsKey(FTDG))
               FirstTwoDigits[FTDG] += 1;
            else
               FirstTwoDigits.Add(FTDG, 1);
         }

         int Highest = 0;
         foreach (int item in FirstTwoDigits.Values)
            if (item > Highest)
               Highest = item;
         int HighestFreqPortTwoDigits = 0;
         foreach (int item in FirstTwoDigits.Keys)
            if (FirstTwoDigits[item] == Highest)
               HighestFreqPortTwoDigits = item;
         if (HighestFreqPortTwoDigits == 0)
            Logger.WriteLineWithColor("WTF? why is the first two digits EMPTY?", ConsoleColor.Red);
         if (Not5Digits)
         {
            if (Has5Digits)
               Logger.WriteLineWithColor("Some ports werent 5 digits. this may make things harder.", ConsoleColor.Red);
            else
               Logger.WriteLineWithColor("NO ports had 5 digits. well this is weird. going with 4 digit ports then.",
                  ConsoleColor.Red);
         }
         else
         {
            Logger.WriteLineWithColor(
               $"Everything looks fine, Most frequent first two digits: {HighestFreqPortTwoDigits}",
               ConsoleColor.Green);
         }

         Logger.WriteLineWithColor("Frequency of each two first digits: ", ConsoleColor.Cyan);
         Logger.WriteLineWithColor("PortDigits : Frequency", ConsoleColor.Cyan);
         foreach (int item in FirstTwoDigits.Keys)
            Logger.WriteLineWithColor($"{item} : {FirstTwoDigits[item]}", ConsoleColor.DarkMagenta);
         //e.g 14000, That'll be 1000 ports.
         //e.g 1400, 100 ports.

         int PortRangeStart = 0;
         int PortRangeEnd = 0;
         if (Has5Digits)
         {
            PortRangeStart = HighestFreqPortTwoDigits * 1000;
            PortRangeEnd = PortRangeStart + 1000;
         }
         else
         {
            PortRangeStart = HighestFreqPortTwoDigits * 100;
            PortRangeEnd = PortRangeStart + 100;
         }

         int PortRangeCount = PortRangeEnd - PortRangeStart;
         PortRange PR = new PortRange
         {
            PortStart = PortRangeStart, PortEnd = PortRangeEnd, PortsCount = PortRangeCount,
            StunResults = StunResults.ToArray()
         };
         PublicPortsList.Clear();
         StunResults.Clear();

         Logger.WriteLineWithColor("Disposing Clients...", ConsoleColor.Cyan);
         foreach (UdpClient item in udpClientsList)
            if (!ReferenceEquals(item, null))
               item.Dispose();
         udpClientsList.Clear();
         Logger.WriteLineWithColor("Disposing Tasks...", ConsoleColor.Cyan);
         foreach (Task item in TasksList)
            if (!ReferenceEquals(item, null))
               item.Dispose();
         TasksList.Clear();

         return PR;
      }
   }
}