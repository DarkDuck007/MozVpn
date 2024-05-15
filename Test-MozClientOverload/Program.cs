using MozUtil;
using System.Buffers;
using System.Net;
using System.Net.Sockets;

internal class Program
{
   private static void Main(string[] args)
   {
      TcpListener Cli = new TcpListener(IPAddress.Any, 64750);
      Cli.Start();
      Console.ReadLine();
      //string URL = "http://localhost/Anime/Windows Activator.exe";
      //List<MozManager> ManagerPool = new List<MozManager>();
      //string ServerHost = "http://127.0.0.1:5209/";
      //MozManager Manager = new MozManager(ServerHost, 64, "iphone-stun.strato-iphone.de:3478", 60000 + 0, 50000 + 0, 10000, true);
      //Manager.NewLogArrived += Manager_NewLogArrived;
      //ManagerPool.Add(Manager);
      //Manager.InitiateConnection();
      //for (int k = 1; k < 6000; k++)
      //{
      //   System.Threading.Thread.Sleep(100);
      //   //Task.Run(() =>
      //   //{
      //      int i = k;
      //      Manager = new MozManager(ServerHost, 64, "iphone-stun.strato-iphone.de:3478", 60000 + i, 50000 + i, 10000, true);
      //      //Manager.NewLogArrived += Manager_NewLogArrived;
      //      ManagerPool.Add(Manager);
      //      Manager.InitiateConnection();
      //      //byte[] buffer = ArrayPool<byte>.Shared.Rent(4096);
      //      //while (true)
      //      //{
      //      //   System.Threading.Thread.Sleep(100);
      //      //   try
      //      //   {
      //      //      HttpWebRequest Req = WebRequest.CreateHttp(URL);
      //      //      Req.Proxy = new WebProxy($"HTTP://127.0.0.1:{50000 + i}");
      //      //      var resp = Req.GetResponse();
      //      //      var resps = resp.GetResponseStream();
      //      //      int k = 0;
      //      //      while ((k = resps.Read(buffer, 0, buffer.Length)) > 0)
      //      //      {

      //      //      }
      //      //      resps.Dispose();
      //      //      resp.Dispose();
      //      //      //WebClient WC = new WebClient();
      //      //      //WC.Proxy = new WebProxy("HTTP://127.0.0.1:63850");
      //      //      ////WC.CachePolicy = new RequestCachePolicy(RequestCacheLevel.BypassCache);
      //      //      //WC.DownloadData(URL);
      //      //      ////Console.WriteLine(R);
      //      //      //WC.Dispose();
      //      //      GC.Collect();
      //      //   }
      //      //   catch (Exception ex)
      //      //   {
      //      //      Console.WriteLine(ex.Message);
      //      //      System.Threading.Thread.Sleep(2000);
      //      //   }
      //      //}
      //      //ArrayPool<byte>.Shared.Return(buffer);
      //   //});
      //}
      //Console.ReadLine();
   }

   private static void Manager_NewLogArrived(object? sender, string e)
   {
      //Console.WriteLine(e);
   }
}