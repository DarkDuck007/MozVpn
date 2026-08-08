using System.Collections.Concurrent;

namespace DirtySocksASP
{
   public static class ClientManager
   {
      public static volatile ConcurrentDictionary<int, LNClient> ReliableClients = new ConcurrentDictionary<int, LNClient>();
      public static bool AddReliableClient(int IPEP, LNClient Cl)
      {
         return ReliableClients.TryAdd(IPEP, Cl);
      }
      public static bool RemoveReliableClient(int IPEP)
      {
         var res = ReliableClients.Remove(IPEP, out LNClient? CL);
         //CL?.Dispose();
         CL = null;
         return res;
      }
   }
}
