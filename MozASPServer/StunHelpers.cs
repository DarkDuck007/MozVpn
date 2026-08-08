using STUN;
using STUN.Attributes;
using System.Net.Sockets;
using System.Net;
using System.Diagnostics.CodeAnalysis;

namespace DirtySocksASP
{
   public class StunHelpers
   {
      public static STUNQueryResult GetStunResult(Socket SockToUse)
      {
         //string StunSrv1 = "iphone-stun.strato-iphone.de:3478";
         string StunSrv2 = "stun.schlund.de:3478";

         if (!STUNUtils.TryParseHostAndPort(StunSrv2, out IPEndPoint stunEndPoint))
            throw new Exception("Failed to resolve STUN server address");

         STUNClient.ReceiveTimeout = 1000;
         //var queryResult = STUNClient.Query(stunEndPoint, STUNQueryType.ExactNAT, true);
         var queryResult = STUNClient.Query(SockToUse, stunEndPoint, STUNQueryType.ExactNAT, NATTypeDetectionRFC.Rfc3489);
         if (queryResult.QueryError != STUNQueryError.Success)
            throw new Exception("Query Error: " + queryResult.QueryError.ToString());

         //Console.WriteLine("PublicEndPoint: {0}", queryResult.PublicEndPoint);
         //Console.WriteLine("LocalEndPoint: {0}", queryResult.LocalEndPoint);
         //Console.WriteLine("NAT Type: {0}", queryResult.NATType);
         return queryResult;
      }
      public static async Task<string> GetPublicIPAsync()
      {
         using (HttpClient client = new HttpClient())
         {
            try
            {
               // The ipify service returns the IP address as plain text.
               string response = await client.GetStringAsync("https://api.ipify.org");
               return (response);
            }
            catch (HttpRequestException ex)
            {
               Console.WriteLine($"Error retrieving IP: {ex.Message}");
               return null;
            }
         }
      }
      public static async Task<STUNQueryResult> ForceStunAsync(Socket socket)
      {
         STUNQueryResult ServerStunResult = StunHelpers.GetStunResult(socket);
         for (int i = 0; i < 10; i++)
         {
            if (ServerStunResult.QueryError != STUNQueryError.Success)
            {
               ServerStunResult = StunHelpers.GetStunResult(socket);
               if (i == 10)
               {
                  throw new Exception(ServerStunResult.QueryError.ToString());
               }
               await Task.Delay(1000 * i);
            }
            else
               return ServerStunResult;
         }
         return ServerStunResult;
      }
   }
}
