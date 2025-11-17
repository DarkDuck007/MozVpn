using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MozVPNMaui2
{
   public static class StaticInformation
   {
      public static event EventHandler StopServiceEvent;
      public static void CallServiceStop()
      {
         StopServiceEvent?.Invoke("StaticInfo", null);
      }
      public static string[] StunServers { get; set; } = new List<string>(System.Text.Encoding.UTF8.GetString(AppResources.StunList).Split("\n").ToList().Select(x => x.Trim()).Distinct()).ToArray();
      public static byte[] PossibleChannelCount => Enumerable.Range(1, 64).Select(x => (byte)x).ToArray();
      //public static byte[] PossibleChannelCount { get; set; } = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14,
      //   15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41,
      //   42, 43, 44, 45, 46, 47, 48, 49, 50, 51, 52, 53, 54, 55, 56, 57, 58, 59, 60, 61, 62, 63, 64};
      public static string[] ServerList { get; set; } = new List<string>(System.Text.Encoding.UTF8.GetString(AppResources.ServerList).Split("\n").ToList().Select(x => x.Trim()).Distinct()).ToArray();

   }
}
