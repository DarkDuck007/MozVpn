using STUN;
using System.Runtime.CompilerServices;

namespace DirtySocksASP
{
   public static class StaticData
   {
        //public static string? CurrentServerAddress { get; set; } = "147.93.122.82";
        public static string? CurrentServerAddress { get; set; } = null;
        public static int HttpKeepAliveConnectionsCount = 0;
      public static DateTime ServerStartDateTime { get; set; }
      //public static NatType STUNNATType
      static string[] _Stuns = Properties.Resources.StunServers.Split('\n');
      public static string[] StunServers { get { return _Stuns; } }
      public static string PreferredStunServer = _Stuns[0];
      public static int _TotalActiveConnections = 0;
      public static int TotalActiveConnections { get { return _TotalActiveConnections; } }
      private static long _TotalLocalTcpClientsMade = 0;
      public static long TotalLocalTcpClientsMade { get { return _TotalLocalTcpClientsMade; } set { _TotalLocalTcpClientsMade = value; } }
      public static long LastTcpClientMakeDelayTicks { get; set; }
      public static List<long> TcpClientCreationDelayTicksList { get; set; } = new List<long>();

      public static void AddTcpClientCreationDelayTick(long delayTicks)
      {
         lock (TcpClientCreationDelayTicksList)
         {
            TcpClientCreationDelayTicksList.Add(delayTicks);
            if (TcpClientCreationDelayTicksList.Count > 256)
            {
               TcpClientCreationDelayTicksList.RemoveAt(0);
            }
            LastTcpClientMakeDelayTicks = delayTicks;
            Interlocked.Increment(ref _TotalLocalTcpClientsMade);
         }
      }
      public static long GetAvgTcpClientCreationDelayTicks()
      {
         lock (TcpClientCreationDelayTicksList)
         {
            if (TcpClientCreationDelayTicksList.Count < 1)
            {
               return 0;
            }
            else
            {
               long sum = 0;
               long i = 0;
               foreach (long item in TcpClientCreationDelayTicksList)
               {
                  sum += item;
                  i++;
               }
               long Mean = sum / i;
               return Mean;
            }
         }
      }
   }
}
