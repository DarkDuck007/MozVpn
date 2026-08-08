namespace DirtySocksASP
{
   public class TunWithEndpoint
   {
      HttpContext _Context;
      public TunWithEndpoint(HttpContext context)
      {
         _Context = context;
      }
      public async Task BeginTun()
      {

      }
   }
}
