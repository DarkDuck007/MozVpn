using LiteNetLib;
using MozUtil;
using System.Buffers;
using System.Net;
using System.Net.Cache;

internal class Program
{
   private static void Main(string[] args)
   {
      string URL = "http://localhost/gcc-13.2.0-no-debug.7z";
      for (int i = 0; i < 32; i++)
      {
         Task.Run(() =>
         {
            var buffer = ArrayPool<byte>.Shared.Rent(4096);
            
            try
            {
               while (true)
               {
                  HttpWebRequest Req = WebRequest.CreateHttp(URL);
                  Req.Proxy = new WebProxy("HTTP://127.0.0.1:6387");
                  var resp = Req.GetResponse();
                  var resps = resp.GetResponseStream();
                  int k = 0;
                  while ((k = resps.Read(buffer, 0, buffer.Length)) > 0)
                  {

                  }
                  resps.Dispose();
                  resp.Dispose();
                  //WebClient WC = new WebClient();
                  //WC.Proxy = new WebProxy("HTTP://127.0.0.1:63850");
                  ////WC.CachePolicy = new RequestCachePolicy(RequestCacheLevel.BypassCache);
                  //WC.DownloadData(URL);
                  ////Console.WriteLine(R);
                  //WC.Dispose();
                  GC.Collect();
               }
            }
            catch (Exception ex)
            {
               Console.WriteLine(ex.Message);
            }
            ArrayPool<byte>.Shared.Return(buffer);
         });
         //System.Threading.Thread.Sleep(1);
      }
      //for (int i = 0; i < 64; i++)
      //{
      //   MozManager Manager = new MozManager("http://127.0.0.1:5209/", 64, "stun.schlund.de:3478", 60000 + i, 40000 + i, 10000, true, MozUtil.NatUtils.udpMode.LiteNet, false);
      //   //Manager.NewLogArrived += Manager_NewLogArrived;
      //   //Manager.LatencyUpdated += Manager_LatencyUpdated;
      //   //Manager.StatusUpdated += Manager_StatusUpdated;

      //   Task.Run(async () =>
      //   {
      //      bool Result = await Manager.InitiateConnection();
      //      if (!Result)
      //      {
      //         Console.WriteLine("FALSE");
      //         if (!object.ReferenceEquals(Manager, null))
      //         {
      //            Manager.Dispose();
      //         }
      //      }
      //      else
      //      {
      //         for (int j = 0; j < 8; j++)
      //         {
      //            await Task.Run(() =>
      //            {
      //               try
      //               {
      //                  byte[] Crap;
      //                  WebClient WC = new WebClient();
      //                  while (true)
      //                  {
      //                     WC.Proxy = new WebProxy("HTTP://127.0.0.1:" + 40000 + i);
      //                     //WC.CachePolicy = new RequestCachePolicy(RequestCacheLevel.BypassCache);
      //                     Crap = WC.DownloadData(URL);
      //                     break;
      //                     //Console.WriteLine(R);
      //                  }
      //                  WC.Dispose();
      //               }
      //               catch (Exception ex)
      //               {
      //                  Console.WriteLine(ex.Message);
      //               }
      //            });
      //         }
      //      }
      //   });
      //}
      Console.ReadLine();
   }
}