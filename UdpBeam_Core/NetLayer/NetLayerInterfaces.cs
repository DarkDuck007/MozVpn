using System;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace UdpBeam_Core.NetLayer
{
   public interface INetInner
   {
      public void InitializeIO(Pipe Inner, Pipe Outer);
   }
   public interface INetOuter
   {
      public void InitializeOutput(Pipe OutputPipe);
   }
}
