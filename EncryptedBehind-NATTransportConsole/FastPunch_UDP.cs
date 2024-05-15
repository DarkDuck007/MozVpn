using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace EncryptedBehind_NATTransportConsole
{
   class FastPunch_UDP
   {
      /// <summary>
      /// Returns the remote punched ports.
      /// </summary>
      /// <param name="Client"></param>
      /// <param name="Destination"></param>
      /// <param name="Timeout"></param>
      /// <param name="DestinationPortRanges"></param>
      /// <returns></returns>
      /// <exception cref="ArgumentException"></exception>
      public static async Task<int[]> PunchNatAsync(UdpClient Client, string Destination, int Timeout = 5000, bool OnlyNeedOne = false, params PortRange[] DestinationPortRanges)
      {
         CancellationTokenSource CTS = new CancellationTokenSource();
         byte[] PunchPayload = new byte[] { 0, 255, 255, 0 };
         List<int> PunchedPorts = new List<int>();
         bool Punched = false;
         if (DestinationPortRanges.Length == 0)
         {
            throw new ArgumentException("Port range should at least contain a single value with at least the same start and end port number or more.");
         }
         Stopwatch ST = Stopwatch.StartNew();
         Task T = Task.Run(async () =>
           {
              while (ST.ElapsedMilliseconds < Timeout)
              {
                 if (Punched)
                 {
                    break;
                 }
                 var res = await Client.ReceiveAsync(CTS.Token);
                 if (!PunchedPorts.Contains(res.RemoteEndPoint.Port))
                 {
                    PunchedPorts.Add(res.RemoteEndPoint.Port);
                    if (OnlyNeedOne)
                    {
                       Punched = true; 
                    }
                 }
              }
           }, CTS.Token);
         while (ST.ElapsedMilliseconds < Timeout)
         {
            foreach (PortRange item in DestinationPortRanges)
            {
               for (int i = item.PortStart; i <= item.PortEnd; i++)
               {
                  if (Punched)
                  {
                     break;
                  }
                  await Client.SendAsync(PunchPayload, Destination, i);
                  //punch stuff
               }
            }
         }
         CTS.Cancel();
         await T;
         T.Dispose();
         return PunchedPorts.ToArray();
      }
   }
   public class PortRange
   {
      public ushort PortStart { get; set; }
      public ushort PortEnd { get; set; }
      public PortRange(ushort start, ushort end)
      {
         PortStart = start;
         PortEnd = end;
      }
      public PortRange() { }
   }
}
