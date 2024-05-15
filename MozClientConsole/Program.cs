using MozUtil;
using MozUtil.Clients;
using MozUtil.NatUtils;
using MozUtil.Types;
using STUN;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace MozClientConsole
{
   internal class Program
   {
      bool LocalMode = true;
      string ServerURL = "http://127.0.0.1:5000";
      private event EventHandler<byte[]>? ServerCommandOverHTTP;
      byte[] KeepAlivePacket = new byte[] { 255, 255 };
      int HolePunchingTimeout = 10000;
      UdpClient? stunClient;
      string StunServerOrg = "stunserver.stunprotocol.org:3478";
      STUNQueryResult? stunResult;
      TransportMode uMode = TransportMode.LiteNet; //Default
      static void Main(string[] args)
      {
         Program P = new Program();
         P.Run().Wait();
      }
      private async Task Run()
      {
         //byte[] TestBytes = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 };
         //byte[] ResBytes = TestBytes[3..5];
         //foreach (var item in ResBytes)
         //{
         //   Console.WriteLine(item);
         //}
         //Environment.Exit(0);
         ServerCommandOverHTTP += Program_ServerCommandOverHTTP;
         if (LocalMode)
            ServerURL = "http://localhost:5209/";
         else
            ServerURL = "http://dirtypx.somee.com/";
         //ServerURL = "https://ordakblack.bsite.net/";

         try
         {
            int StunTimeout = 400;
            int StunTimeoutIncrementor = 1000;
            while (true)
            {
               stunClient = new UdpClient();
               stunResult = MozStun.GetStunResult(stunClient.Client, StunServerOrg, StunTimeout);
               if (stunResult.QueryError == STUNQueryError.Success)
               {
                  break;
               }
               else if (stunResult.QueryError == STUNQueryError.Timedout)
               {
                  stunClient.Dispose();
                  StunTimeout += StunTimeoutIncrementor;
                  Logger.Log("Stun query timed out after :" + StunTimeout);
                  if (StunTimeout > 10000)
                  {
                     Logger.Log("Stun query failed due to timeouts");
                     break;
                  }
               }
               else
               {
                  throw new Exception("StunQueryError: " + stunResult.QueryError + stunResult.ServerErrorPhrase);
               }
            }
            if (LocalMode)
            {
               stunResult.PublicEndPoint.Address = IPAddress.Parse("127.0.0.1");
               stunResult.PublicEndPoint.Port = stunResult.LocalEndPoint.Port;
            }
            Logger.Log($"Nat type: {stunResult.NATType} Local EP{stunResult.LocalEndPoint} Pub EP: {stunResult.PublicEndPoint}");
            if (((int)stunResult.NATType) <= 4)
            {
               //it isn't symmetric.
               byte[] PunchInfoBytes = MozStatic.SerializePunchInfo(stunResult, HolePunchingTimeout);

               await PollHttpServer(PunchInfoBytes, uMode);
               //udpPuncher Puncher = new udpPuncher();
            }
            else
            {
               //it is symmetric.
               var PRange = MozStun.GetPortRange(10, StunServerOrg, 2000);

               byte[] PunchInfoBytes = MozStatic.SerializePunchInfo(PRange.StunResults[0], PRange.PortStart, PRange.PortsCount, HolePunchingTimeout);
               await PollHttpServer(PunchInfoBytes, uMode);

            }
         }
         catch (Exception ex)
         {
            Logger.Log(ex.StackTrace + ex.Message);
         }
         Console.ReadLine();
      }
      void WriteLineWithColor(string Text, ConsoleColor Color)
      {
         ConsoleColor ConCol = Console.ForegroundColor;
         Console.ForegroundColor = Color;
         Console.WriteLine(Text);
         Console.ForegroundColor = ConCol;
      }
      void WriteWithColor(string Text, ConsoleColor Color)
      {
         ConsoleColor ConCol = Console.ForegroundColor;
         Console.ForegroundColor = Color;
         Console.Write(Text);
         Console.ForegroundColor = ConCol;
      }
      private async Task StartUdpTun(udpConnectionInfo udpInfo)
      {
         //Normal for now, hardcoded
         Logger.WriteLineWithColor("Disposed old udp client.", ConsoleColor.Yellow);
         IPEndPoint LocalEP = stunClient.Client.LocalEndPoint as IPEndPoint;
         IPEndPoint ServerEP = new IPEndPoint(udpInfo.ipAddress, udpInfo.Port);
         stunClient.Dispose();
         if (udpInfo.udpMode == TransportMode.Normal)
         {
            if (stunResult.NATType != STUNNATType.Symmetric)
            {
               NormalMozClient Mclient = new NormalMozClient(LocalEP, ServerEP, StunServerOrg, 64900, 64901);
               await Mclient.Start();
            }
            else
            {
               while (isPunchInProgress)
               {
                  await Task.Delay(100);
               }
               if (SymPunchedPorts != null)
               {
                  if (SymPunchedPorts.Length != 0)
                  {
                     List<NormalMozClient> MozClientCollection = new List<NormalMozClient>();
                     List<Task> MozClientTaskCollection = new List<Task>();
                     int i = 0;
                     foreach (int item in SymPunchedPorts)
                     {
                        Task T = Task.Run(async () =>
                         {
                            IPEndPoint LendPoint = new IPEndPoint(LocalEP.Address, item);
                            NormalMozClient Mclient = new NormalMozClient(LendPoint, ServerEP, StunServerOrg, 64900 + i);
                            await Mclient.Start();
                            MozClientCollection.Add(Mclient);
                         });
                        MozClientTaskCollection.Add(T);
                     }
                     foreach (Task item in MozClientTaskCollection)
                     {
                        try
                        {
                           await item;
                        }
                        catch (Exception ex)
                        {
                           Logger.Log(ex.Message + Environment.NewLine + ex.StackTrace);
                        }
                     }
                  }
               }
            }
         }
         else if (udpInfo.udpMode == TransportMode.LiteNet)
         {
            LiteNetMozClient MClient = new LiteNetMozClient(LocalEP, ServerEP, 16, 64950);
            await MClient.Start(udpInfo, stunResult.NATType);
         }
      }
      int[] SymPunchedPorts;
      bool isPunchInProgress;
      private async Task BeginHolePunching(HolePunchPeerInfo PeerInfo)
      {
         if (((int)PeerInfo.NatType) == 5 && ((int)stunResult.NATType) == 5)
         {
            //throw new NotSupportedException("Symmetric To Symmetric hole punching isn't supported. at least one peer must be non-symmetric.");
            Logger.Log("Symmetric To Symmetric hole punching isn't supported. at least one peer must be non-symmetric.");
         }
         udpPuncher Puncher = new udpPuncher();
         if (((int)stunResult.NATType) <= 4 && ((int)PeerInfo.NatType) <= 4)
         {
            await Puncher.PR2PRPunch(stunClient, new IPEndPoint(PeerInfo.ipAddress, PeerInfo.Port), PeerInfo.HolePunchTimeout);
         }
         else if (((int)stunResult.NATType) == 5 && ((int)PeerInfo.NatType) != 5)
         {
            isPunchInProgress = true;
            SymPunchedPorts = await Puncher.Symmetric2PRPunch(new IPEndPoint(PeerInfo.ipAddress, PeerInfo.Port));
            //SymPunchedPorts = await Puncher.SymmetricNatPunchToPR(new IPEndPoint(PeerInfo.ipAddress, PeerInfo.Port));
            isPunchInProgress = false;
         }
         else
         {
            isPunchInProgress = true;
            SymPunchedPorts = await Puncher.Symmetric2PRPunch(new IPEndPoint(PeerInfo.ipAddress, PeerInfo.Port));
            //SymPunchedPorts = await Puncher.SymmetricNatPunchToPR(new IPEndPoint(PeerInfo.ipAddress, PeerInfo.Port));
            isPunchInProgress = false;
         }
      }
      private async void Program_ServerCommandOverHTTP(object? sender, byte[] e)
      {
         ServerCommand Command = (ServerCommand)e[0];
         if (e == KeepAlivePacket || ((int)Command) == 58)
         {
            //yeah, no. do nothing.
         }
         else
         {
            switch (Command)
            {
               case ServerCommand.BeginHolePunching:
                  HolePunchPeerInfo peerInfo = MozStatic.DeserializePunchInfo(e, 1);
                  _ = Task.Run(async () =>
                    {
                       Stopwatch ST = Stopwatch.StartNew();
                       await BeginHolePunching(peerInfo);
                       ST.Stop();
                       WriteLineWithColor($"Punching took {ST.ElapsedTicks.ToString()} Ticks ({ST.ElapsedMilliseconds.ToString()} ms)", ConsoleColor.Green);
                    });
                  break;
               case ServerCommand.PunchResult:
                  if (e[1] == 0xaa)
                  {
                     //Failed
                  }
                  else if (e[1] == 0xbb)
                  {
                     WriteLineWithColor("Punched! ", ConsoleColor.Green);
                     if (e.Length > 2)
                     {
                        Program_ServerCommandOverHTTP(sender, e[2..]);
                     }
                     //Success
                  }
                  else
                  {
                     throw new Exception("Unknown punch status received from the server.");
                  }
                  break;
               case ServerCommand.BeginUdpClient:
                  WriteLineWithColor("Starting udp client...", ConsoleColor.Magenta);
                  udpConnectionInfo udpInfo = MozStatic.DeserializeUdpConnectionInfo(e, 1);
                  _ = StartUdpTun(udpInfo);
                  break;
               case ServerCommand.KeepAlive:
                  //Do nothing
                  break;
               default:
                  break;
            }
         }
      }
      public async Task PollHttpServer(byte[] SendData, TransportMode Mode)
      {
         string url = ServerURL;
         switch (Mode)
         {
            case TransportMode.Normal:
               url += "INIT";
               break;
            case TransportMode.LiteNet:
               url += "InitLN";
               break;
            default:
               break;
         }
         HttpWebRequest WebReq = WebRequest.CreateHttp(url);
         //WebReq.Proxy = new WebProxy("127.0.0.1", 2081);
         WebReq.Method = "POST";
         using (Stream S = WebReq.GetRequestStream())
         {
            S.Write(SendData);
            S.Flush();
         }
         var resp = await WebReq.GetResponseAsync();
         HttpClient Client = new HttpClient();
         //HttpContent Cont = new HttpContent(SendData);
         //var httpResult = await Client.PostAsync(ServerURL,);
         using (var stream = resp.GetResponseStream())
         {
            byte[] ReadBuffer = new byte[4096];
            int BytesRead;
            while ((BytesRead = await stream.ReadAsync(ReadBuffer, 0, ReadBuffer.Length)) > 0)
            {
               if (BytesRead == 0)
               {
                  //Stream closed
                  break;
               }
               else
               {
                  Console.WriteLine(Encoding.UTF8.GetString(ReadBuffer, 0, BytesRead));
                  ServerCommandOverHTTP?.Invoke(this, ReadBuffer[0..BytesRead]);
               }
            }
            //throw new Exception("Http connection closed.");
            Console.WriteLine("HttpConnection Closed.");
         }
      }

   }
}