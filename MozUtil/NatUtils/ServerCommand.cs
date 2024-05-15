namespace MozUtil.NatUtils
{
   public enum ServerCommand
   {
      BeginHolePunching,
      PunchResult,
      BeginUdpClient,

      KeepAlive = 255
   }
}