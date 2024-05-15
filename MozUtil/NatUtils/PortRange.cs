using STUN;

namespace MozUtil.NatUtils
{
   public class PortRange
   {
      public int PortStart { get; set; }
      public int PortEnd { get; set; }
      public int PortsCount { get; set; }
      public STUNQueryResult[] StunResults { get; set; }
   }
}