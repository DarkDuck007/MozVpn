using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MozUtil.Types
{
  public interface ISnetClientCalls
   {
      public Task<bool> DataAvailable(byte ConnectionID,  byte[] ConnectionData);
      public Task<bool> KillConnection(byte ConnectionID);
   }
   public interface ISnetServerCalls:ISnetClientCalls
   {

   }
}
