using STUN;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace MozUtil.Types
{
   public struct UdpRelayRequestInfo
   {
      [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
      public string Desthostname;
      [MarshalAs(UnmanagedType.I4)]
      public int DestPort;
      [MarshalAs(UnmanagedType.Bool)]
      public bool _isStringUpright = true;
      [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 16)]
      public string nonce;

      public STUNNATType SourceNatType;
      public AddressFamily SourceAddressFamily;

      [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
      public byte[] SourceipAddress;

      public int HolePunchTimeout;
      public int SourcePort;
      public int SourcePortsCount;


      public UdpRelayRequestInfo(string DestHost, int _DestPort, STUNNATType SNT, AddressFamily SAF, byte[] SIA,
         int HPT, int SP, int SPC, string _nonce = null)
      {
         Desthostname = DestHost;
         this.DestPort = _DestPort;
         SourceNatType = SNT;
         SourceAddressFamily = SAF;
         SourceipAddress = SIA;
         HolePunchTimeout = HPT;
         SourcePort = SP;
         SourcePortsCount = SPC;

         if (_nonce == null)
         {
            _nonce = new Random().Next(0, int.MaxValue).ToString();
         }
         nonce = _nonce.ToString();
      }
      public static byte[] getBytes(UdpRelayRequestInfo str)
      {
         if (str._isStringUpright)
         {
            str.Desthostname = new String(str.Desthostname.ToCharArray().Reverse().ToArray());
            str._isStringUpright = false;
         }
         int size = Marshal.SizeOf(str);
         byte[] arr = new byte[size];

         IntPtr ptr = IntPtr.Zero;
         try
         {
            ptr = Marshal.AllocHGlobal(size);
            Marshal.StructureToPtr(str, ptr, true);
            Marshal.Copy(ptr, arr, 0, size);
         }
         finally
         {
            Marshal.FreeHGlobal(ptr);
         }
         return arr;
      }
      public static UdpRelayRequestInfo fromBytes(byte[] arr)
      {
         UdpRelayRequestInfo str = new UdpRelayRequestInfo();

         int size = Marshal.SizeOf(str);
         IntPtr ptr = IntPtr.Zero;
         try
         {
            ptr = Marshal.AllocHGlobal(size);

            Marshal.Copy(arr, 0, ptr, size);

            str = (UdpRelayRequestInfo)Marshal.PtrToStructure(ptr, str.GetType());
         }
         finally
         {
            Marshal.FreeHGlobal(ptr);
         }
         if (!str._isStringUpright)
         {
            str.Desthostname = new String(str.Desthostname.ToCharArray().Reverse().ToArray());
            str._isStringUpright = true;
         }
         return str;
      }
   }
}
