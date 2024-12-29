using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace MozUtil.NatUtils
{
   public class udpPuncher
   {
      public delegate void TimerEventHandler(uint id, uint msg,
         ref int userCtx, int rsv1, int rsv2);

      private int aa = 1;
      private readonly int bb = 1;
      private readonly ushort interv = 1;
      private uint QuickTimer;
      private TimerEventHandler TEH;
      private readonly AutoResetEvent wh = new AutoResetEvent(false);

      [DllImport("WinMM.dll", SetLastError = true)]
      private static extern uint timeSetEvent(int msDelay, int msResolution,
         TimerEventHandler handler, ref int userCtx, int eventType);

      [DllImport("WinMM.dll", SetLastError = true)]
      private static extern uint timeKillEvent(uint timerEventId);

      private void MREv(uint id, uint msg, ref int userCtx, int rsv1, int rsv2)
      {
         //lock (wh)
         //{
         wh.Set();

         //}
      }

      ~udpPuncher()
      {
         timeKillEvent(QuickTimer);
      }

      private void RegisterTimer()
      {
         TEH = MREv;
         QuickTimer = timeSetEvent(interv, interv, TEH, ref aa, bb);
         //Task.Run(() =>
         //{
         //   //Thread.CurrentThread.Priority = ThreadPriority.Lowest;
         //   try
         //   {
         //      while (true)
         //      {
         //         wh.Set();
         //      }
         //   }
         //   catch (Exception ex)
         //   {
         //      Console.WriteLine(ex.Message);

         //   }
         //});
      }

      private void KillTimer()
      {
         timeKillEvent(QuickTimer);
      }

      public async Task<int[]> PRNatPunchToSymmetricAsync(UdpClient Client, int PortStart, int PortEnd,
         string DestinationAddress, int Timeout = 20000)
      {
         CancellationTokenSource CTS = new CancellationTokenSource();
         bool StopReceiving = false;
         byte[] PunchData = new byte[2];
         string IP = DestinationAddress;
         int PortRangeStart = PortStart;
         int PortRangeEnd = PortEnd;
         Random RND = new Random();
         Logger.WriteLineWithColor($"PortRange Count: {PortRangeEnd - PortRangeStart}", ConsoleColor.Green);
         Logger.WriteLineWithColor(
            $"Waiting for incoming packets from any of the ports {PortRangeEnd - PortRangeStart}", ConsoleColor.Cyan);

         List<int> ReceivedPorts = new List<int>();
         var ReceiveTask = Task.Run(async () =>
         {
            while (!StopReceiving)
            {
               if (CTS.IsCancellationRequested)
                  break;
               var res = await Client.ReceiveAsync();
               Logger.WriteLineWithColor($"Received Punch from: {res.RemoteEndPoint}", ConsoleColor.Magenta);
               if (!ReceivedPorts.Contains(res.RemoteEndPoint.Port)) ReceivedPorts.Add(res.RemoteEndPoint.Port);
            }
         }, CTS.Token);
         for (int Att = 0; Att < 5; Att++)
         {
            Logger.WriteLineWithColor("Sending 4 packets to each dest port, 5 times.", ConsoleColor.Cyan);
            for (int Pck = 0; Pck < 4; Pck++)
            {
               for (int i = PortRangeStart; i <= PortRangeEnd; i++)
               {
                  RND.NextBytes(PunchData);
                  Client.Send(PunchData, PunchData.Length, IP, i);
               }

               Thread.Sleep(50);
            }

            Logger.WriteLineWithColor($"Send done, Attempt {Att}/5, Waiting 500ms", ConsoleColor.Cyan);
            Thread.Sleep(500);
         }

         Logger.WriteLineWithColor("Waiting 1000ms just in case.", ConsoleColor.Cyan);
         if (ReceivedPorts.Count == 0)
            Logger.WriteLineWithColor("No ports were punched :(", ConsoleColor.Red);
         else
            Logger.WriteLineWithColor($"{ReceivedPorts.Count} ports were punched!! >*-*<", ConsoleColor.Green);
         //And the rest of the code.
         StopReceiving = true;
         CTS.Cancel();
         return ReceivedPorts.ToArray();
         //IPEndPoint EP = new IPEndPoint(IPAddress.Parse(IpPortSep[0].Trim()), int.Parse(IpPortSep[1].Trim()));
      }

      public async Task<int[]> SymmetricNatPunchToPR(IPEndPoint EP, int ClientCount = 1000, int Timeout = 20000)
      {
         try
         {
            Random RND = new Random();
            List<UdpClient> Clients = new List<UdpClient>();
            List<Task> StunTasks = new List<Task>();
            while (Clients.Count <= ClientCount) Clients.Add(new UdpClient());
            byte[] PunchData = new byte[1];
            Logger.WriteWithColor("Enter the IP:Port of the other peer (They'd better NOT be symmetric.): ",
               ConsoleColor.Green);
            List<Task> WaitingTasks = new List<Task>();
            List<UdpClient> PunchedClients = new List<UdpClient>();
            Logger.WriteLineWithColor($"Waiting for incoming packets on all {Clients.Count} ports from {EP}",
               ConsoleColor.Cyan);
            foreach (UdpClient item in Clients)
            {
               var T = Task.Run(async () =>
               {
                  var res = await item.ReceiveAsync();
                  Logger.WriteLineWithColor($"Wow, Received data from {res.RemoteEndPoint}", ConsoleColor.Magenta);
                  PunchedClients.Add(item);
               });
               WaitingTasks.Add(T);
            }

            for (int Att = 0; Att < 5; Att++)
            {
               Logger.WriteLineWithColor(
                  $"Sending 1 packet from each of the {Clients.Count} ports to {EP}, 1 Time, 5 attempts.",
                  ConsoleColor.Cyan);
               for (int i = 0; i < 1; i++)
               {
                  foreach (UdpClient item in Clients)
                  {
                     RND.NextBytes(PunchData);
                     await item.SendAsync(PunchData, PunchData.Length, EP);
                     //wh.WaitOne();
                  }

                  Thread.Sleep(100);
               }

               Logger.WriteLineWithColor($"Send done, Attempt {Att}/5. waiting 500ms before sending again.",
                  ConsoleColor.Cyan);
               Thread.Sleep(500);
            }

            Logger.WriteLineWithColor("Waiting for 1000ms just in case.", ConsoleColor.Cyan);
            Thread.Sleep(1000);
            if (PunchedClients.Count == 0)
               Logger.WriteLineWithColor("No clients were able to punch a hole :(", ConsoleColor.Red);
            else
               Logger.WriteLineWithColor($"{PunchedClients.Count} clients were able to punch a hole!! >*-*<",
                  ConsoleColor.Green);
            Logger.WriteLineWithColor("Disposing failures...", ConsoleColor.Cyan);
            UdpClient[] tempArr = Clients.ToArray();
            foreach (UdpClient item in tempArr)
               if (!PunchedClients.Contains(item))
               {
                  item.Dispose();
                  Clients.Remove(item);
               }

            Logger.WriteLineWithColor(
               $"Clients: {Clients.Count}, PunchedClients: {PunchedClients.Count} (These two numbers MUST " +
               "be the same now.", Clients.Count == PunchedClients.Count ? ConsoleColor.Green : ConsoleColor.Red);

            Stopwatch St = Stopwatch.StartNew();
            GC.Collect();
            St.Stop();
            Console.WriteLine($"Garbage collection done in {St.ElapsedTicks} Ticks.");
            St.Reset();
            int[] PunchedPorts = new int[PunchedClients.Count];
            for (int i = 0; i < PunchedClients.Count; i++)
               PunchedPorts[i] = (PunchedClients[i].Client.LocalEndPoint as IPEndPoint).Port;
            foreach (UdpClient item in Clients) item.Close();
            foreach (int item in PunchedPorts) Console.Write(item + ", ");
            return PunchedPorts;
         }
         catch (Exception ex)
         {
            Console.WriteLine(ex.Message + ex.StackTrace);
            return new int[] { };
         }
      }

      public async Task<int[]> PR2SymmetricPunch(UdpClient Client, int PortStart, int PortEnd,
         string DestinationAddress, int Timeout = 20000)
      {
         byte[] sendPunch = { 0xFF, 0xFF };
         byte[] receivedPunch = { 0xFE, 0xFF };
         CancellationTokenSource CTS = new CancellationTokenSource();
         if (PortStart > PortEnd)
            throw new InvalidOperationException("PortStart should be smaller or equal to PortEnd");
         if (PortStart == 0 || PortEnd == 0)
            throw new InvalidOperationException("PortStart and PortEnd cannot be 0");
         byte[] PunchData = new byte[1];
         List<int> MaybePunchedPorts = new List<int>();
         List<int> PunchedPorts = new List<int>();
         Random RND = new Random();
         CTS.CancelAfter(Timeout);
         //RegisterTimer();
         Task SenderTask = Task.Run(async () =>
         {
            for (int j = 0; j < 5; j++)
            {
               for (int k = 0; k < 2; k++)
               {
                  if (CTS.IsCancellationRequested)
                     break;
                  for (int i = PortStart; i <= PortEnd; i++)
                  {
                     if (CTS.IsCancellationRequested)
                        break;
                     RND.NextBytes(PunchData);
                     if (PunchedPorts.Contains(i))
                        await Client.SendAsync(receivedPunch, PunchData.Length, DestinationAddress, i);
                     else
                        await Client.SendAsync(PunchData, PunchData.Length, DestinationAddress, i);
                     //await item.SendAsync(PunchData, PunchData.Length, DestinationEP);
                     //wh.Reset();
                     //wh.WaitOne();
                  }

                  Thread.Sleep(1 * (PortEnd - PortStart));
               }

               if (MaybePunchedPorts.Count == 0)
                  await Task.Delay(100);
               //else
               //   break;
            }
         }, CTS.Token);

         Task ReceiverTask = Task.Run(async () =>
         {
            while (!CTS.IsCancellationRequested)
            {
               var res = await Client.ReceiveAsync();
               //Logger.Logger.WriteLineWithColor($"Wow, Received data from {res.RemoteEndPoint.ToString()}", ConsoleColor.Magenta);
               if (!PunchedPorts.Contains(res.RemoteEndPoint.Port)) MaybePunchedPorts.Add(res.RemoteEndPoint.Port);
               if (res.Buffer.SequenceEqual(sendPunch))
               {
                  await Client.SendAsync(receivedPunch, PunchData.Length, DestinationAddress, res.RemoteEndPoint.Port);
                  PunchedPorts.Add(res.RemoteEndPoint.Port);
                  Logger.Log("SequenceEqualTriggered");
                  //break;
               }
               //if (CTS.IsCancellationRequested)
               //{
               //   break;
               //}
            }
         }, CTS.Token);


         //Task SenderTask = Task.Run(async () =>
         //{
         //   for (int i = PortStart; i <= PortEnd; i++)
         //   {
         //      RND.NextBytes(PunchData);
         //      await Client.SendAsync(PunchData, PunchData.Length, DestinationAddress, i);
         //   }
         //});
         while (PunchedPorts.Count < 4)
         {
            await Task.Delay(200);
            if (CTS.IsCancellationRequested) break;
         }

         await Task.Delay(2000);
         if (!CTS.IsCancellationRequested)
         {
            Logger.Log($"{PunchedPorts.Count} Ports were punched and triggered early cancellation!");
            CTS.Cancel();
         }
         //await SenderTask;
         //CTS.Cancel();

         //KillTimer();
         Logger.Log($"{PunchedPorts.Count} Ports were punched.");
         try
         {
            CTS.Dispose();
         }
         catch (Exception ex)
         {
            Logger.Log(ex.Message + ex.StackTrace);
         }

         return PunchedPorts.ToArray();
      }

      /// <summary>
      ///    Attempts to do udp hole punching using the specified number of sockets.
      ///    Returns the local ports of successfull punches with the destination, empty if none was punched.
      /// </summary>
      /// <param name="DestinationEP"></param>
      /// <param name="ClientCount"></param>
      /// <param name="Timeout"></param>
      /// <returns>An array of int containing punched ports, empty in case of failure.</returns>
      public async Task<int[]> Symmetric2PRPunch(IPEndPoint DestinationEP, int ClientCount = 1000, int Timeout = 20000)
      {
         CancellationTokenSource CTS = new CancellationTokenSource();
         List<UdpClient> udpClients = new List<UdpClient>();
         List<int> PunchedPorts = new List<int>();
         byte[] PunchData = new byte[1];
         Random RND = new Random();
         for (int i = 0; i < ClientCount; i++) udpClients.Add(new UdpClient(0));
         CTS.CancelAfter(Timeout);
         List<Task> WaitingTasks = new List<Task>();
         List<UdpClient> MaybePunchedClients = new List<UdpClient>();
         byte[] receivedPunch = { 0xFE, 0xFF };
         Logger.Log("Punch init sym2pr");
         RegisterTimer();
         Task SenderTask = Task.Run(async () =>
         {
            Logger.Log("sender task started.");
            for (int j = 0; j < 4; j++) //return this to 4
            {
               for (int i = 0; i < 3; i++)
                  try
                  {
                     if (CTS.IsCancellationRequested)
                        break;
                     foreach (UdpClient item in udpClients)
                     {
                        if (CTS.IsCancellationRequested)
                           break;
                        RND.NextBytes(PunchData);
                        item.Send(PunchData, PunchData.Length, DestinationEP);
                        wh.WaitOne();
                     }
                     //System.Threading.Thread.Sleep(1 * ClientCount);
                  }
                  catch (Exception ex)
                  {
                     Logger.Log(ex.Message + ex.StackTrace);
                  }

               if (MaybePunchedClients.Count == 0)
                  await Task.Delay(100);
               else
                  break;
            }
         } /*, CTS.Token*/);
         foreach (UdpClient item in udpClients)
         {
            var T = Task.Run(async () =>
            {
               while (!CTS.IsCancellationRequested)
                  try
                  {
                     var res = await item.ReceiveAsync();
                     try
                     {
                        Logger.WriteLineWithColor(
                      $"Wow, Received data from {res.RemoteEndPoint} +{item.Client.LocalEndPoint} + {res.Buffer.Length}",
                      ConsoleColor.Magenta);
                     }
                     catch (ObjectDisposedException)
                     {

                     }
                     if (!MaybePunchedClients.Contains(item)) MaybePunchedClients.Add(item);
                     if (res.Buffer.SequenceEqual(receivedPunch))
                        PunchedPorts.Add((item.Client.LocalEndPoint as IPEndPoint).Port);
                     if (res.Buffer.Length != 1) Console.WriteLine("BufSize2!");
                  }
                  catch (Exception ex)
                  {
                     //Logger.Log(ex.Message + ex.StackTrace);
                     break;
                  }
            } /*, CTS.Token*/);
            WaitingTasks.Add(T);
         }

         Logger.Log("Receiver taskts started.");
         await SenderTask;
         //Cleanup

         #region Cleanups

         //Logger.Log("Disposing failures...");
         //foreach (UdpClient item in udpClients)
         //{
         //   if (!MaybePunchedClients.Contains(item))
         //   {
         //      try
         //      {
         //         item.Close();
         //         //item?.Dispose();
         //      }
         //      catch
         //      {
         //      }
         //   }
         //}
         //Testing Hypothetically punched ports
         byte[] sendPunch = { 0xFF, 0xFF };
         Logger.Log($"Validating {MaybePunchedClients.Count} successors...");
         for (int i = 0; i < 15; i++)
            for (int j = 0; j < MaybePunchedClients.Count; j++)
            {
               if (CTS.IsCancellationRequested)
                  break;
               try
               {
                  await MaybePunchedClients[j].SendAsync(sendPunch, sendPunch.Length, DestinationEP);
                  wh.WaitOne();
               }
               catch (Exception)
               {
               }
            }

         //await Task.Delay(200 + (1 * (MaybePunchedClients.Count)));
         //if (PunchedPorts.Count > 0)
         //   break;
         int[]? MaybePunchedPorts = null;
         if (PunchedPorts.Count == 0 && MaybePunchedClients.Count > 0)
         {
            MaybePunchedPorts = new int[MaybePunchedClients.Count];
            for (int i = 0; i < MaybePunchedClients.Count; i++)
               MaybePunchedPorts[i] = (MaybePunchedClients[i].Client.LocalEndPoint as IPEndPoint).Port;
         }

         Logger.Log("Closing sockets...");
         foreach (UdpClient item in udpClients) item.Close();
         Logger.Log("disposing tasks...");
         Stopwatch ST = Stopwatch.StartNew();
         CTS.Cancel();
         ST.Stop();
         Logger.Log($"CTS.Cancel took {ST.ElapsedMilliseconds}ms");
         ST.Restart();
         foreach (var item in WaitingTasks)
            try
            {
               item?.Dispose();
            }
            catch
            {
            }

         ST.Stop();
         Logger.Log($"Task disposal took {ST.ElapsedMilliseconds}ms");
         udpClients.Clear();
         WaitingTasks.Clear();

         #endregion


         KillTimer();
         try
         {
            CTS.Dispose();
         }
         catch (Exception ex)
         {
            Logger.Log(ex.Message + ex.StackTrace);
         }

         Logger.Log($"{PunchedPorts.Count} validated punched ports.");
         if (PunchedPorts.Count == 0 && MaybePunchedPorts != null) return MaybePunchedPorts;
         return PunchedPorts.ToArray();
      }

      public async Task<bool> PR2PRPunch(UdpClient Client, IPEndPoint DestinationEP, int Timeout = 10000)
      {
         CancellationTokenSource CTS = new CancellationTokenSource();
         bool SendPunched = false;
         bool ReceivePunched = false;
         try
         {
            byte[] sendPunch = { 0xFF, 0xFF };
            byte[] receivedPunch = { 0xFE, 0xFF };
            CTS.CancelAfter(Timeout);
            var ReceiveTask = Task.Run(async () =>
            {
               while (!CTS.Token.IsCancellationRequested)
                  try
                  {
                     var RecRes = await Client.ReceiveAsync();
                     if (RecRes.RemoteEndPoint.Equals(DestinationEP))
                     {
                        ReceivePunched = true;
                        await Client.SendAsync(receivedPunch, receivedPunch.Length, DestinationEP);
                        if (RecRes.Buffer.SequenceEqual(receivedPunch)) SendPunched = true;
                        if (SendPunched && ReceivePunched) break;
                     }
                     else
                     {
                        Logger.Log($"Endpoint mismatch. Expected {DestinationEP} Got {RecRes.RemoteEndPoint}");
                     }
                  }
                  catch (Exception ex)
                  {
                     Logger.Log(ex.Message + ex.StackTrace);
                  }
            }, CTS.Token);

            var SendTask = Task.Run(async () =>
            {
               while (!CTS.Token.IsCancellationRequested)
                  try
                  {
                     if (SendPunched && ReceivePunched) break;
                     if (ReceivePunched)
                        await Client.SendAsync(receivedPunch, receivedPunch.Length, DestinationEP);
                     else
                        await Client.SendAsync(sendPunch, sendPunch.Length, DestinationEP);
                     await Task.Delay(75);
                  }
                  catch (Exception ex)
                  {
                     Logger.Log(ex.Message + ex.StackTrace);
                  }
            }, CTS.Token);
            await SendTask;
            SendTask.Dispose();
            await ReceiveTask;
            ReceiveTask.Dispose();
         }
         catch (Exception ex)
         {
            Logger.Log(ex.Message + ex.StackTrace);
         }
         finally
         {
            CTS.Dispose();
         }

         return SendPunched && ReceivePunched;
      }
   }
}