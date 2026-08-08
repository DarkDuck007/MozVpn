using System.Text;

namespace DirtySocksASP
{
   public static class LocalLogger
   {
      //public static FileStream LogPath = File.Open(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "/Log.txt"), FileMode.OpenOrCreate);
      //static string LogString = "";
      static StringBuilder SB = new StringBuilder();
      public static void Log(string? message)
      {
         if (message == null)
            return;
         Console.WriteLine(message);
         //LogString += message + "\n";
         if (SB.Length > 2097152)//2MB
         {
            SB.Remove(0, 524288);
         }
         SB.AppendLine(message);
         //LogPath.Write(Encoding.UTF8.GetBytes(message));
      }
      public static void Log(byte[] message)
      {
         string LogText = Encoding.UTF8.GetString(message);
         Console.WriteLine(LogText);
         //LogPath.Write(message);
         //LogString += LogText + "\n";
         if (SB.Length > 2097152)//2MB
         {
            SB.Remove(0, 524288);
         }
         SB.AppendLine(LogText);
      }
      public static string GetLog()
      {
         return SB.ToString();
         //using (StreamReader SR = new StreamReader(LogPath))
         //{
         //   return SR.ReadToEnd();
         //}
      }
   }
}
