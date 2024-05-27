using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MozUtil
{
   public static class Logger
   {
      private static readonly StringBuilder SB = new StringBuilder();
      private static readonly List<Stream> LogStreams = new List<Stream>();
      public static event EventHandler<string>? OnNewLogArrived;

      public static bool RegisterLogStream(Stream stream)
      {
         lock (LogStreams)
         {
            if (LogStreams.Contains(stream))
            {
               return false;
            }

            LogStreams.Add(stream);
            return true;
         }
      }

      public static bool UnregisterLogStream(Stream stream)
      {
         lock (LogStreams)
         {
            if (LogStreams.Contains(stream))
            {
               LogStreams.Remove(stream);
               return true;
            }

            return false;
         }
      }

      public static void WriteLineWithColor(string Text, ConsoleColor Color)
      {
#if WINDOWS
 ConsoleColor ConCol = Console.ForegroundColor;
         Console.ForegroundColor = Color;
         Console.WriteLine(Text);
         Console.ForegroundColor = ConCol;
#else
         Console.WriteLine(Text);
#endif
         SilentLog(Text);
      }

      public static void WriteWithColor(string Text, ConsoleColor Color)
      {
#if WINDOWS
  ConsoleColor ConCol = Console.ForegroundColor;
         Console.ForegroundColor = Color;
         Console.Write(Text);
         Console.ForegroundColor = ConCol;
#else
         Console.WriteLine(Text);
#endif
         SilentLog(Text);
      }

      //public static FileStream LogPath = File.Open(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "/Log.txt"), FileMode.OpenOrCreate);
      //static string LogString = "";
      public static void Log(string? message)
      {
         if (message == null)
            return;
         Console.WriteLine(DateTime.Now.ToString("HH:mm:ss:fff") + message);
         //LogString += message + "\n";
         AppendLog(message);
         //LogPath.Write(Encoding.UTF8.GetBytes(message));
      }
      public static void LogException(Exception message)
      {
         Log(message.Message + Environment.NewLine + message.StackTrace);
      }
      private static void SilentLog(string? message)
      {
         if (message != null) AppendLog(message);
      }

      public static void Log(byte[] message)
      {
         string LogText = Encoding.UTF8.GetString(message);
         Console.WriteLine(LogText);
         //LogPath.Write(message);
         //LogString += LogText + "\n";
         AppendLog(LogText);
      }

      private static async void AppendLog(string Text)
      {
         lock (SB)
         {
            Text = $"[{DateTime.Now.ToString("HH:mm:ss:fff")}]{Text}";
            //if (SB.Length > 2097152 / 2)//1MB
            //{
            //   SB.Remove(0, 524288);
            //}
            //SB.AppendLine(Text);
            if (SB.Length > 2097152 / 2) //1MB
               SB.Remove(0, 524288);
            SB.AppendLine(Text);
            OnNewLogArrived?.Invoke("Logger", Text);
         }

         for (int i = 0; i < LogStreams.Count; i++)
            try
            {
               await LogStreams[i].WriteAsync(Encoding.ASCII.GetBytes(Text + Environment.NewLine));
               await LogStreams[i].FlushAsync();
            }
            catch (Exception)
            {
               LogStreams.RemoveAt(i);
            }
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