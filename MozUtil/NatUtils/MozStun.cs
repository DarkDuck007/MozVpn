using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Linq;
using System.Threading.Tasks;
using STUN;
using STUN.Attributes;

namespace MozUtil.NatUtils
{
   public static class MozStun
   {
      private static readonly object PortRangeCacheLock = new object();
      private static readonly Dictionary<string, CachedPortRange> PortRangeCache =
         new Dictionary<string, CachedPortRange>(StringComparer.Ordinal);
      private static readonly Dictionary<string, TaskCompletionSource<PortRange>> PortRangeDetections =
         new Dictionary<string, TaskCompletionSource<PortRange>>(StringComparer.Ordinal);
      private static readonly TimeSpan PortRangeCacheLifetime = TimeSpan.FromMinutes(1);
      private static Task<PortRange>? FirstPortRangeDetection;

      private sealed class CachedPortRange
      {
         public CachedPortRange(PortRange range, DateTimeOffset detectedAt)
         {
            Range = range;
            DetectedAt = detectedAt;
         }

         public PortRange Range { get; }
         public DateTimeOffset DetectedAt { get; }
      }

      public static STUNQueryResult IPDiscoverOnly(Socket SockToUse, string Address, int Timeout = 2000)
      {
         if (!STUNUtils.TryParseHostAndPort(Address, SockToUse.AddressFamily, out IPEndPoint stunEndPoint))
            throw new Exception($"Failed to resolve a {SockToUse.AddressFamily}-compatible address for STUN server '{Address}'");
         STUNClient.ReceiveTimeout = Timeout;
         var queryResult =
            STUNClient.Query(SockToUse, stunEndPoint, STUNQueryType.PublicIP, NATTypeDetectionRFC.Rfc3489);
         EnsureMappedEndpoint(queryResult, Address);
         return queryResult;
      }
      public static STUNQueryResult GetStunResult(Socket SockToUse, string Address, int Timeout = 2000)
      {
         //string StunServerDef = "stun.schlund.de:3478";
         //string StunServerOrg = "stunserver.stunprotocol.org:3478";
         if (!STUNUtils.TryParseHostAndPort(Address, SockToUse.AddressFamily, out IPEndPoint stunEndPoint))
            throw new Exception($"Failed to resolve a {SockToUse.AddressFamily}-compatible address for STUN server '{Address}'");

         STUNClient.ReceiveTimeout = Timeout;
         //var queryResult = STUNClient.Query(stunEndPoint, STUNQueryType.ExactNAT, true);
         var queryResult =
            STUNClient.Query(SockToUse, stunEndPoint, STUNQueryType.ExactNAT, NATTypeDetectionRFC.Rfc3489);
         EnsureMappedEndpoint(queryResult, Address);
         //if (queryResult.QueryError != STUNQueryError.Success)
         //   throw new Exception("Query Error: " + queryResult.QueryError.ToString());

         //Console.WriteLine("PublicEndPoint: {0}", queryResult.PublicEndPoint);
         //Console.WriteLine("LocalEndPoint: {0}", queryResult.LocalEndPoint);
         //Console.WriteLine("NAT Type: {0}", queryResult.NATType);
         return queryResult;
      }

      private static void EnsureMappedEndpoint(STUNQueryResult result, string serverAddress)
      {
         if (result.QueryError == STUNQueryError.Success && result.PublicEndPoint == null)
         {
            result.QueryError = STUNQueryError.BadResponse;
            throw new InvalidOperationException(
               $"STUN server '{serverAddress}' reported success without a mapped public endpoint.");
         }
      }

      /// <summary>
      /// Reuses a freshly detected symmetric-NAT port range when the validating STUN query
      /// reports the exact same mapped public endpoint. On a cold cache, concurrent callers
      /// wait for one initial detection; matching callers reuse it and non-matching callers
      /// are then released to detect their own ranges concurrently.
      /// </summary>
      public static async Task<PortRange> GetPortRangeCachedAsync(STUNQueryResult validationResult,
         int stunCount, string stunAddress, int stunTimeout = 5000)
      {
         if (validationResult.PublicEndPoint == null)
            return await Task.Run(() => GetPortRange(stunCount, stunAddress, stunTimeout)).ConfigureAwait(false);

         string endpointKey = validationResult.PublicEndPoint.ToString();
         while (true)
         {
            Task<PortRange>? waitFor = null;
            TaskCompletionSource<PortRange>? leader = null;
            DateTimeOffset now = DateTimeOffset.UtcNow;

            lock (PortRangeCacheLock)
            {
               foreach (string expiredKey in PortRangeCache
                  .Where(item => now - item.Value.DetectedAt >= PortRangeCacheLifetime)
                  .Select(item => item.Key).ToArray())
                  PortRangeCache.Remove(expiredKey);

               if (PortRangeCache.TryGetValue(endpointKey, out CachedPortRange? cached))
               {
                  Logger.Log($"Reusing cached symmetric port range {cached.Range.PortStart}-{cached.Range.PortEnd} " +
                     $"for {validationResult.PublicEndPoint} (age {(now - cached.DetectedAt).TotalSeconds:0.#}s).");
                  return cached.Range;
               }

               if (PortRangeDetections.TryGetValue(endpointKey, out TaskCompletionSource<PortRange>? existing))
               {
                  waitFor = existing.Task;
               }
               else if (PortRangeCache.Count == 0 && FirstPortRangeDetection != null)
               {
                  // Cold-start barrier: let one profile finish first. Once it has populated the
                  // cache, matching profiles reuse it and all remaining profiles continue together.
                  waitFor = FirstPortRangeDetection;
               }
               else
               {
                  leader = new TaskCompletionSource<PortRange>(TaskCreationOptions.RunContinuationsAsynchronously);
                  PortRangeDetections[endpointKey] = leader;
                  if (PortRangeCache.Count == 0) FirstPortRangeDetection = leader.Task;
               }
            }

            if (leader == null)
            {
               Logger.Log($"Waiting for an in-progress symmetric port-range detection before handling " +
                  $"{validationResult.PublicEndPoint}...");
               try { await waitFor!.ConfigureAwait(false); }
               catch { /* Retry after the leader has removed its failed in-flight entry. */ }
               continue;
            }

            try
            {
               PortRange range = await Task.Run(() => GetPortRange(stunCount, stunAddress, stunTimeout))
                  .ConfigureAwait(false);
               lock (PortRangeCacheLock)
               {
                  PortRangeCache[endpointKey] = new CachedPortRange(range, DateTimeOffset.UtcNow);
                  PortRangeDetections.Remove(endpointKey);
                  if (ReferenceEquals(FirstPortRangeDetection, leader.Task)) FirstPortRangeDetection = null;
               }
               Logger.Log($"Cached symmetric port range {range.PortStart}-{range.PortEnd} for " +
                  $"{validationResult.PublicEndPoint} for {PortRangeCacheLifetime.TotalSeconds:0} seconds.");
               leader.TrySetResult(range);
               return range;
            }
            catch (Exception)
            {
               lock (PortRangeCacheLock)
               {
                  PortRangeDetections.Remove(endpointKey);
                  if (ReferenceEquals(FirstPortRangeDetection, leader.Task)) FirstPortRangeDetection = null;
               }
               // Wake waiters so one of them can retry without publishing an unobserved
               // exception from this coordination task. The leader still throws the real error.
               leader.TrySetCanceled();
               throw;
            }
         }
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
                  if (StunRes.QueryError == STUNQueryError.Success && StunRes.PublicEndPoint != null)
                  {
                     lock (StunResults)
                     {
                        StunResults.Add(StunRes);
                        PublicPortsList.Add(StunRes.PublicEndPoint.Port);
                     }
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
         foreach (var stunQueryResult in StunResults)
         {
            Logger.Log($"Local {stunQueryResult.LocalEndPoint} Pub {stunQueryResult.PublicEndPoint} NAT: {stunQueryResult.NATType}");
         }
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
            PortStart = PortRangeStart,
            PortEnd = PortRangeEnd,
            PortsCount = PortRangeCount,
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
