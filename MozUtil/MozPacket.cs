namespace MozUtil
{
   public class MozPacket
   {
      public byte[]? RawData { get; set; }
      public int StartIndex { get; set; }
      public int Length { get; set; }
      public byte ChannelID { get; set; }
      public int PeerID { get; set; }
   }
}