using MozUtil.Types;
using System.Runtime.InteropServices;
using System;

namespace MozUtil.NatUtils
{
   public enum ServerCommands
   {
      BeginHolePunching,
      PunchResult,
      BeginUdpClient,
      ServerStatusUpdate,
      EndToEndPipeCreationResult,
      KeepAlive = 255
   }

   public class ServerCommand
   {
      public ServerCommands CommandType { get; set; }
      public byte[]? CommandValue { get; set; }
   }
   public class ServerCommandUtils
   {
      public static byte[] BuildServerStatusInformation(ServerStatusInformation ServerStatusInfo)
      {
         int size = Marshal.SizeOf(ServerStatusInfo);
         byte[] arr = new byte[size];

         IntPtr ptr = IntPtr.Zero;
         try
         {
            ptr = Marshal.AllocHGlobal(size);
            Marshal.StructureToPtr(ServerStatusInfo, ptr, true);
            Marshal.Copy(ptr, arr, 0, size);
         }
         finally
         {
            Marshal.FreeHGlobal(ptr);
         }
         return arr;
      }

      public static ServerStatusInformation ServerStatusInformationFromBytes(byte[] ByteData, int Offset)
      {
         ServerStatusInformation str = new ServerStatusInformation();
         int size = Marshal.SizeOf(str);
         IntPtr ptr = IntPtr.Zero;
         try
         {
            ptr = Marshal.AllocHGlobal(size);

            Marshal.Copy(ByteData, Offset, ptr, size);

            str = (ServerStatusInformation)Marshal.PtrToStructure(ptr, str.GetType());
         }
         finally
         {
            Marshal.FreeHGlobal(ptr);
         }
         return str;
      }
   }
}