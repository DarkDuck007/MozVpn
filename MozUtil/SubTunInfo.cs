using MozUtil.Clients;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace MozUtil
{
   public enum TunStatus
   {
      Requesting,
      Requested,
      Connected,
      Disconnected,
      Failed,
      Rejected
   }
   public enum TunType
   {
      SimpleMozConnection,
      MtProtoConnection,
      CustomUdpTun
   }
   public class SubTunInfo : INotifyPropertyChanged
   {
      public RelayManager RelayManager { get; set; }
      public int PeerID { get; set; } = -1;
      public byte ID { get; set; } = 1; //Starts from 1
      private string destinationHostName = string.Empty; //max 253 chars
      private long totalBytesOut;
      private long totalBytesIn;
      private bool isDead;
      private TunStatus status;
      private IPEndPoint localEndpoint;
      public string TotalInForMuman { get { return MozStatic.HumanReadable((ulong)totalBytesIn); } }
      public string TotalOutForMuman { get { return MozStatic.HumanReadable((ulong)totalBytesOut); } }
      public ushort DestinationPort { get; set; } = 0;
      public TunType Type { get; set; }
      //public ushort DestinationPort
      //{
      //   get => DestinationPort; set
      //   {
      //      DestinationPort = value;
      //      NotifyPropertyChanged();
      //   }
      //}
      public IPEndPoint LocalEndpoint
      {
         get => localEndpoint; set
         {
            localEndpoint = value;
            NotifyPropertyChanged();
         }
      }
      public int LocalPort
      {
         get => LocalEndpoint.Port; set
         {
            LocalEndpoint.Port = value;
            NotifyPropertyChanged();
         }
      }
      public TunStatus Status
      {
         get => status; set
         {
            status = value;
            NotifyPropertyChanged();
         }
      }

      public string DestinationHostName
      {
         get { return destinationHostName; }
         set
         {
            destinationHostName = value;
            NotifyPropertyChanged();
         }
      }
      public long TotalBytesOut
      {
         get => totalBytesOut;
         set
         {
            totalBytesOut = value;
            NotifyPropertyChanged();
            NotifyPropertyChanged(nameof(TotalOutForMuman));
         }
      }
      public long TotalBytesIn
      {
         get => totalBytesIn; set
         {
            totalBytesIn = value;
            NotifyPropertyChanged();
            NotifyPropertyChanged(nameof(TotalInForMuman));
         }
      }
      public bool IsDead
      {
         get => isDead; set
         {
            isDead = value;
            NotifyPropertyChanged();
         }
      }

      public event PropertyChangedEventHandler? PropertyChanged;
      private void NotifyPropertyChanged([CallerMemberName] String propertyName = "")
      {
         PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
      }
   }
}
