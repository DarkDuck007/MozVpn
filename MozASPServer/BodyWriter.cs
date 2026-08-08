using Microsoft.AspNetCore.Http;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DirtySocksASP
{
   public static class BodyWriter
   {
      public static async Task WriteStringAsync(Stream S, string DataToWrite)
      {
         if (S == null || DataToWrite == null)
         {
            return;
         }
         await S.WriteAsync(Encoding.ASCII.GetBytes(DataToWrite + Environment.NewLine));
         await S.FlushAsync();
      }
   }
}
