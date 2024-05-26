using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LiteNetLib;
using MozUtil.Clients;
using MozUtil.NatUtils;
using MozUtil.Types;
using STUN;

namespace MozUtil
{
   public class MozManager : IDisposable
   {
      public int DataPadding { get; set; } = 0;
      private readonly Random RandomGen = new Random();
      private readonly string? _ProxyAddress;
      private readonly string _StunServerAddress;
      private readonly bool _UseProxy;
      private readonly byte ChannelCount;
      private readonly CancellationTokenSource CTS;
      private readonly Process GoLangBaleTunProcess = new Process();

      private readonly int HolePunchingTimeout = 10000;
      private bool isPunchInProgress;
      private readonly byte[] KeepAlivePacket = { 255, 255 };
      private readonly bool LocalMode;
      public LiteNetMozClient MClient;
      private readonly string ServerURL;
      private UdpClient? stunClient;
      private STUNQueryResult stunResult;
      private int[] SymPunchedPorts;
      private readonly TransportMode uMode;
      private HttpWebRequest WebReq;

      public MozManager(string ServerHost, byte MaxChannels, string StunServerAddress, int SocksPort = 63600,
         int HttpPort = 63700,
         int HolePunchTimeout = 10000, bool IsServerLocal = false, TransportMode ConnectionMode = TransportMode.LiteNet,
         bool useProxy = false, string? proxyAddress = null, bool ForceActSymmetric = false)
      {
         ServerURL = ServerHost;
         ChannelCount = MaxChannels;
         _SocksPort = SocksPort;
         _HttpPort = HttpPort;
         HolePunchingTimeout = HolePunchTimeout;
         LocalMode = IsServerLocal;
         uMode = ConnectionMode;
         _StunServerAddress = StunServerAddress;
         CTS = new CancellationTokenSource();
         _UseProxy = useProxy;
         _ProxyAddress = proxyAddress;
         _ForceSymmetric = ForceActSymmetric;
      }

      public NetStatistics? LiteNetStats
      {
         get
         {
            try
            {
               if (!ReferenceEquals(MClient.NetStats, null))
                  return MClient.NetStats;
               return null;
            }
            catch (Exception)
            {
               return null;
            }
         }
      }

      //public event EventHandler<StatusUpdate>? StatusUpdated;
      public int symmetricConnectionClientCount { get; set; } = 100;

      private int _SocksPort;
      public int SocksPort { get { return _SocksPort; } }

      private int _HttpPort;
      public int HTTPPort { get { return _HttpPort; } }

      public int TotalConnections => MClient.ConnectiounsCount;
      public byte TotalChannels => MClient.ChannelsCount;
      private bool _ForceSymmetric { get; }

      public void Dispose()
      {
         try
         {
            CTS.Cancel();
            //MClient?.Dispose();
            //stunClient?.Dispose();
            if (!ReferenceEquals(MClient, null)) MClient?.Dispose();
            if (!ReferenceEquals(stunClient, null)) stunClient?.Dispose();
            if (!ReferenceEquals(WebReq, null)) WebReq.Abort();

            //StatusUpdated = null;
         }
         catch (Exception ex)
         {
            Logger.Log(ex.Message + ex.StackTrace);
         }

         ServerCommandOverHTTP = null;
         NewLogArrived = null;
         LatencyUpdated = null;
      }

      private event EventHandler<byte[]>? ServerCommandOverHTTP;
      public event EventHandler<string>? NewLogArrived;
      public event EventHandler<int>? LatencyUpdated;
      public event EventHandler<StatusResult>? StatusUpdated;

      public async Task<bool> InitiateConnection()
      {
         Logger.OnNewLogArrived += (Sender, e) => { NewLogArrived?.Invoke(Sender, e); };
         ServerCommandOverHTTP += Program_ServerCommandOverHTTP;
         try
         {
            //
            //if (true)
            //{
            //   PortRange PRange = new PortRange() { PortStart = 40000, PortEnd = 60000, PortsCount = 20000, StunResults = new STUNQueryResult[] { new STUNQueryResult() { NATType = STUNNATType.Symmetric, PublicEndPoint = new IPEndPoint(IPAddress.Parse("2a01:5ec0:981a:13ae:59d9:89d9:7554:ff8a"), 40000) } } };
            //   byte[] PunchInfoBytes = MozStatic.SerializePunchInfo(PRange.StunResults[0], PRange.PortStart - 500, PRange.PortsCount + 1000, HolePunchingTimeout);
            //   _ = PollHttpServer(PunchInfoBytes, uMode, false, CTS.Token);
            //   return true;
            //}
            //
            if (uMode == TransportMode.TCP)
            {
               StatusUpdated?.Invoke(this, StatusResult.StunT);
            }
            else if (uMode == TransportMode.LiteNet || uMode == TransportMode.Normal)
            {
               StatusUpdated?.Invoke(this, StatusResult.SendingStun);
               int StunTimeout = 400;
               int StunTimeoutIncrementor = 1000;
               while (true)
               {
                  stunClient = new UdpClient();
                  if (LocalMode)
                  {
                     await stunClient.SendAsync(new byte[] { 0 }, 1, new IPEndPoint(IPAddress.Loopback, 65535));
                     stunResult = new STUNQueryResult
                     {
                        LocalEndPoint = new IPEndPoint(IPAddress.Parse("127.0.0.1"), 0),
                        PublicEndPoint = new IPEndPoint(IPAddress.Parse("127.0.0.1"), 0)
                     };
                     break;
                  }

                  stunResult = MozStun.GetStunResult(stunClient.Client, _StunServerAddress, StunTimeout);
                  if (stunResult.QueryError == STUNQueryError.Success)
                  {
                     StatusUpdated?.Invoke(this, StatusResult.StunSuccess);
                     break;
                  }

                  if (stunResult.QueryError == STUNQueryError.Timedout)
                  {
                     stunClient.Dispose();
                     Logger.Log("Stun query timed out after :" + StunTimeout);
                     StunTimeout += StunTimeoutIncrementor;
                     if (StunTimeout > 6000)
                     {
                        Logger.Log("Stun query failed due to timeouts");
                        StatusUpdated?.Invoke(this, StatusResult.StunFailed);
                        return false;
                     }
                  }
                  else
                  {
                     StatusUpdated?.Invoke(this, StatusResult.StunFailed);
                     throw new Exception("StunQueryError: " + stunResult.QueryError + stunResult.ServerErrorPhrase);
                  }
               }

               if (LocalMode)
               {
                  stunResult.PublicEndPoint.Address = IPAddress.Parse("127.0.0.1");
                  stunResult.PublicEndPoint.Port = stunResult.LocalEndPoint.Port;
               }

               if (_ForceSymmetric) stunResult.NATType = STUNNATType.Symmetric;
               Logger.Log(
                  $"Nat type: {stunResult.NATType} Local EP{stunResult.LocalEndPoint} Pub EP: {stunResult.PublicEndPoint}");
               if ((int)stunResult.NATType <= 4)
               {
                  //it isn't symmetric.
                  byte[] PunchInfoBytes = MozStatic.SerializePunchInfo(stunResult, HolePunchingTimeout);

                  _ = PollHttpServer(PunchInfoBytes, uMode, false, CTS.Token);
                  return true;
                  //udpPuncher Puncher = new udpPuncher();
               }
               else
               {
                  //it is symmetric.
                  var PRange = MozStun.GetPortRange(10, _StunServerAddress, 2000);
                  int multiplyer = 1;
                  if (symmetricConnectionClientCount > 1000) multiplyer = symmetricConnectionClientCount / 1000;
                  byte[] PunchInfoBytes = MozStatic.SerializePunchInfo(PRange.StunResults[0],
                     PRange.PortStart - 500 * multiplyer, PRange.PortsCount + 1000 * multiplyer, HolePunchingTimeout);
                  _ = PollHttpServer(PunchInfoBytes, uMode, false, CTS.Token);
                  return true;
               }
            }
            else if (uMode == TransportMode.BaleTun)
            {
               GoLangBaleTunProcess.StartInfo.FileName = "D:\\Programming\\Go\\main.exe";
               GoLangBaleTunProcess.StartInfo.RedirectStandardError = true;
               GoLangBaleTunProcess.StartInfo.RedirectStandardOutput = true;
               GoLangBaleTunProcess.StartInfo.RedirectStandardInput = true;
               GoLangBaleTunProcess.EnableRaisingEvents = true;
               //p.ErrorDataReceived += P_ErrorDataReceived;
               //p.OutputDataReceived += P_OutputDataReceived;
               bool b = GoLangBaleTunProcess.Start();
               string PubEP = string.Empty;
               //Console.WriteLine(b);
               try
               {
                  while (!GoLangBaleTunProcess.StandardError.EndOfStream)
                  {
                     string data = GoLangBaleTunProcess.StandardError.ReadLine();
                     Console.WriteLine(data);
                     try
                     {
                        if (data.Split("=")[0].Split(" ")[2].Trim() == "relayed-address")
                        {
                           string endpoint = data.Split("=")[1];
                           PubEP = endpoint;
                           Logger.Log($"EP: {endpoint}");
                           break;
                           //p.StandardInput.WriteLine("127.0.0.1:54545");
                        }
                     }
                     catch (Exception ex)
                     {
                        Logger.Log(ex.Message + ex.StackTrace);
                     }
                  }
               }
               catch (Exception ex)
               {
                  Logger.Log(ex.Message + ex.StackTrace);
               }

               if (PubEP != string.Empty)
               {
                  STUNQueryResult Stunres = new STUNQueryResult
                  {
                     NATType = STUNNATType.PortRestricted,
                     PublicEndPoint = new IPEndPoint(IPAddress.Parse(PubEP.Split(':')[0]),
                        int.Parse(PubEP.Split(':')[1]))
                  };
                  stunResult = Stunres;

                  byte[] PunchInfoBytes = MozStatic.SerializePunchInfo(stunResult, HolePunchingTimeout);

                  _ = PollHttpServer(PunchInfoBytes, TransportMode.LiteNet, false, CTS.Token);
                  return true;
               }
            }
         }
         catch (Exception ex)
         {
            Logger.Log(ex.StackTrace + ex.Message);
         }

         return false;
      }

      private async Task StartUdpTun(udpConnectionInfo udpInfo)
      {
         try
         {
            //Normal for now, hardcoded
            Logger.WriteLineWithColor("Disposed old udp client.", ConsoleColor.Yellow);
            IPEndPoint LocalEP = stunResult.LocalEndPoint;
            IPEndPoint ServerEP = new IPEndPoint(udpInfo.ipAddress, udpInfo.Port);
            if (!ReferenceEquals(stunClient, null)) stunClient.Dispose();
            if (udpInfo.udpMode == TransportMode.Normal)
            {
               if (stunResult.NATType != STUNNATType.Symmetric)
               {
                  NormalMozClient Mclient = new NormalMozClient(LocalEP, ServerEP, _StunServerAddress);
                  await Mclient.Start();
               }
               else
               {
                  while (isPunchInProgress) await Task.Delay(100);
                  if (SymPunchedPorts != null)
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
                              NormalMozClient Mclient =
                                 new NormalMozClient(LendPoint, ServerEP, _StunServerAddress, 64900 + i);
                              await Mclient.Start();
                              MozClientCollection.Add(Mclient);
                           });
                           MozClientTaskCollection.Add(T);
                        }

                        foreach (Task item in MozClientTaskCollection)
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
            else if (udpInfo.udpMode == TransportMode.LiteNet && uMode == TransportMode.LiteNet)
            {
               MClient = new LiteNetMozClient(LocalEP, ServerEP, ChannelCount, SocksPort, HTTPPort);
               //MClient.StatusUpdate += (object sender, StatusResult e) => {
               //   StatusUpdated?.Invoke(sender, e);
               //};
               MClient.LatencyUpdate += (sender, e) => { LatencyUpdated?.Invoke(sender, e); };
               MClient.StatusUpdate += (sender, e) => { StatusUpdated?.Invoke(sender, e); };
               MClient.PortsChanged += MClient_PortsChanged;
               MClient.symmetricConnectionClientCount = symmetricConnectionClientCount;
               await MClient.Start(udpInfo, stunResult.NATType);
               //StatusUpdated?.Invoke(this, StatusResult.Disconnected);
            }
            else if (uMode == TransportMode.BaleTun)
            {
               GoLangBaleTunProcess.StandardInput.WriteLine(ServerEP.ToString());
               Logger.Log(GoLangBaleTunProcess.StandardError.ReadLine());
               MClient = new LiteNetMozClient(new IPEndPoint(IPAddress.Any, 0), new IPEndPoint(IPAddress.Any, 8185),
                  ChannelCount, SocksPort, HTTPPort);
               //MClient.StatusUpdate += (object sender, StatusResult e) => {
               //   StatusUpdated?.Invoke(sender, e);
               //};
               MClient.LatencyUpdate += (sender, e) => { LatencyUpdated?.Invoke(sender, e); };
               MClient.StatusUpdate += (sender, e) => { StatusUpdated?.Invoke(sender, e); };
               MClient.symmetricConnectionClientCount = symmetricConnectionClientCount;
               await MClient.Start(udpInfo, stunResult.NATType);
            }
         }
         catch (Exception ex)
         {
            Logger.Log(ex.Message + ex.StackTrace);
         }
         finally
         {
            if (!CTS.IsCancellationRequested) InitiateConnection();
         }
      }

      private void MClient_PortsChanged(object? sender, Tuple<int, int> e)
      {
         this._SocksPort = e.Item1;
         this._HttpPort = e.Item2;
      }

      private async void Program_ServerCommandOverHTTP(object? sender, byte[] e)
      {
         if (e[0] == 255)
            //Http connection Closed.
            try
            {
               if (!ReferenceEquals(MClient, null))
                  if (!CTS.IsCancellationRequested && MClient.isRunning)
                     _ = PollHttpServer(e, uMode, true, CTS.Token);
            }
            catch (Exception ex)
            {
               Logger.Log(ex.Message + Environment.NewLine + ex.StackTrace);
            }

         ServerCommands Command = (ServerCommands)e[0];
         if (e == KeepAlivePacket || (int)Command == 58)
         {
            //yeah, no. do nothing.
         }
         else
         {
            switch (Command)
            {
               case ServerCommands.BeginHolePunching:
                  HolePunchPeerInfo peerInfo = MozStatic.DeserializePunchInfo(e, 1);
                  _ = Task.Run(async () =>
                  {
                     Stopwatch ST = Stopwatch.StartNew();
                     await BeginHolePunching(peerInfo);
                     ST.Stop();
                     Logger.WriteLineWithColor(
                        $"Punching took {ST.ElapsedTicks.ToString()} Ticks ({ST.ElapsedMilliseconds.ToString()} ms)",
                        ConsoleColor.Green);
                  });
                  break;
               case ServerCommands.PunchResult:
                  if (e[1] == 0xaa)
                  {
                     //Failed
                  }
                  else if (e[1] == 0xbb)
                  {
                     Logger.WriteLineWithColor("Punched! ", ConsoleColor.Green);
                     if (e.Length > 2) Program_ServerCommandOverHTTP(sender, e[2..]);
                     //Success
                  }
                  else
                  {
                     throw new Exception("Unknown punch status received from the server.");
                  }

                  break;
               case ServerCommands.BeginUdpClient:
                  Logger.WriteLineWithColor("Starting udp client...", ConsoleColor.Magenta);
                  udpConnectionInfo udpInfo = MozStatic.DeserializeUdpConnectionInfo(e, 1);
                  _ = StartUdpTun(udpInfo);
                  break;
               case ServerCommands.KeepAlive:
                  //Do nothing
                  break;
            }
         }
      }

      public async Task PollHttpServer(byte[] SendData, TransportMode Mode, bool isConnected = false,
         CancellationToken CT = default)
      {
         try
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
            }

            StatusUpdated?.Invoke(this, StatusResult.HTTPConnecting);
            WebReq = WebRequest.CreateHttp(url + "?" + RandomGen.Next().ToString());
            ServicePointManager.ServerCertificateValidationCallback = delegate { return true; };
            //WebReq.Timeout = 5000;
            if (_UseProxy)
            {
               if (_ProxyAddress != null)
                  WebReq.Proxy = new WebProxy(_ProxyAddress);
               else
                  Logger.Log("Proxy address was null, connecting without a proxy...");
            }

            //WebReq.Proxy = new WebProxy("127.0.0.1", 2081);
            WebReq.Headers.Add("isConnected", isConnected.ToString());
            WebReq.Headers.Add("KeepAlive", "false");
            DataPadding = RandomGen.Next(20, 1000);
            WebReq.Headers.Add("PD", DataPadding.ToString());
            //WebReq.KeepAlive = false;
            WebReq.Method = "POST";
            using (Stream S = WebReq.GetRequestStream())
            {
               byte[] PaddBytes = new byte[DataPadding];
               RandomGen.NextBytes(PaddBytes);
               S.Write(PaddBytes, 0, DataPadding);
               S.Write(SendData);
               S.Flush();
            }

            var resp = await WebReq.GetResponseAsync();
            //HttpClient Client = new HttpClient();
            //HttpContent Cont = new HttpContent(SendData);
            //var httpResult = await Client.PostAsync(ServerURL,);
            using (var stream = resp.GetResponseStream())
            {
               using (CT.Register(() => stream.Close()))
               {
                  try
                  {
                     StatusUpdated?.Invoke(this, StatusResult.HTTPConnected);
                     byte[] ReadBuffer = new byte[4096];
                     int BytesRead;
                     while ((BytesRead = await stream.ReadAsync(ReadBuffer, 0, ReadBuffer.Length, CT)) > 0 &&
                            !CT.IsCancellationRequested)
                     {
                        CTS.Token.ThrowIfCancellationRequested();
                        if (BytesRead == 0)
                           //Stream closed
                           break;

                        string ServerMessage = Encoding.UTF8.GetString(ReadBuffer, 0, BytesRead);
                        if (ServerMessage.Trim() != "KAP") Logger.Log(ServerMessage);
                        if (BytesRead > DataPadding)
                        {
                           ServerCommandOverHTTP?.Invoke(this, ReadBuffer[DataPadding..BytesRead]);
                        }
                        else
                        {
                           ServerCommandOverHTTP?.Invoke(this, ReadBuffer[..BytesRead]);
                        }
                     }
                  }
                  catch (Exception ex)
                  {
                     Logger.Log(ex.Message + ex.StackTrace);
                     StatusUpdated?.Invoke(this, StatusResult.HTTPDisconnected);
                  }
                  //throw new Exception("Http connection closed.");

                  //Client.Dispose();
                  resp?.Dispose();
               }
            }

            StatusUpdated?.Invoke(this, StatusResult.HTTPDisconnected);
            if (CTS.IsCancellationRequested) StatusUpdated = null;
            ServerCommandOverHTTP?.Invoke(this, new byte[] { 255, 255, 0, 0 });
            Logger.Log("HttpConnection Closed.");
         }
         catch (Exception ex)
         {
            StatusUpdated?.Invoke(this, StatusResult.HTTPError);
            Logger.Log(ex.Message + ex.StackTrace);
         }
      }

      private async Task BeginHolePunching(HolePunchPeerInfo PeerInfo)
      {
         if ((int)PeerInfo.NatType == 5 && (int)stunResult.NATType == 5)
            //throw new NotSupportedException("Symmetric To Symmetric hole punching isn't supported. at least one peer must be non-symmetric.");
            Logger.Log(
               "Symmetric To Symmetric hole punching isn't supported. at least one peer must be non-symmetric.");
         udpPuncher Puncher = new udpPuncher();
         if ((int)stunResult.NATType <= 4 && (int)PeerInfo.NatType <= 4)
         {
            await Puncher.PR2PRPunch(stunClient, new IPEndPoint(PeerInfo.ipAddress, PeerInfo.Port),
               PeerInfo.HolePunchTimeout);
         }
         else if ((int)stunResult.NATType == 5 && (int)PeerInfo.NatType != 5)
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
   }
}