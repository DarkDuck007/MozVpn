namespace DirtySocksASP
{
   public class TCClient
   {
      HttpContext? _context;
      volatile Dictionary<ushort, LNConnection?> connections = new Dictionary<ushort, LNConnection?>();
      public TCClient(HttpContext context)
      {
         _context = context;
      }

   }
}
