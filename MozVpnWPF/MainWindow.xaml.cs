using MozUtil;
using MozUtil.NatUtils;
using MozUtil.Types;
using ScottPlot;
using STUN;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Security.RightsManagement;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace MozVpnWPF
{
   /// <summary>
   /// Interaction logic for MainWindow.xaml
   /// </summary>
   public partial class MainWindow : Window
   {
      ulong LastBytesSentCount = 0;
      ulong LastBytesReceivedCount = 0;
      bool isConnected = false;
      bool DisconnectClicked = false;
      string ServerURL = "http://127.0.0.1:5000";
      int PreferredSocksPort = 6375;
      int PreferredHTTPPort = 6385;
      MozManager? Manager;
      Timer OneSecondTimer;
      TransportMode uMode = TransportMode.LiteNet;//Default
      public MainWindow()
      {
         InitializeComponent();
         ToggleServerConnectionBtn.Content = "Connect";
         OneSecondTimer = new Timer(OneSecondTimerCallback, null, 1000, 1000);
      }
      int TimerCounter = 0;
      private void ToggleServerConnectionBtn_Click(object sender, RoutedEventArgs e)
      {
         try
         {
            if (ToggleServerConnectionBtn.Content == "Connect") //ON
            {
               DisconnectClicked = false;
               ToggleServerConnectionBtn.Content = "Disconnect";
               if (ServerSelectionComboBox.SelectedItem.ToString() == null || StunServerSelectorComboBox.SelectedItem == null)
               {
                  throw new Exception("Dayum");
               }
               ServerURL = ServerSelectionComboBox.SelectedItem.ToString();
               string StunServer;
               if (StunServerSelectorComboBox.SelectedItem.ToString() == "Auto")
               {
                  StunServer = StaticInformation.StunServers[4];
               }
               else
               {
                  StunServer = StunServerSelectorComboBox.SelectedItem.ToString();
               }
               byte MaxChannels = byte.Parse(MaxChannelsComboBox.SelectedItem.ToString());
               TransportMode uMode;
               switch (ConnectionTypeComboBox.SelectedIndex)
               {
                  case 0:
                     uMode = TransportMode.LiteNet;
                     break;
                  case 1:
                     uMode = TransportMode.Normal;
                     break;
                  case 2:
                     uMode = TransportMode.TCP;
                     break;
                  case 3:
                     uMode = TransportMode.BaleTun;
                     break;
                  default:
                     throw new NotImplementedException();
               }
               bool EnableProxy = false;
               string ProxyAdr = null;
               if (UseProxyRadioBtn.IsChecked == true)
               {
                  if (!string.IsNullOrWhiteSpace(ConInitProxyServerEntry.Text))
                  {
                     ProxyAdr = ConInitProxyServerEntry.Text;
                  }
                  EnableProxy = true;
               }
               bool LocalServer = ServerURL == "http://127.0.0.1:5209/";

               Manager = new MozManager(ServerURL, MaxChannels, StunServer, PreferredSocksPort, PreferredHTTPPort, 10000,
                  LocalServer, uMode, EnableProxy, ProxyAdr, ForceActSymmetricCheckBox.IsChecked ?? false);
               Manager.NewLogArrived += Manager_NewLogArrived;
               Manager.LatencyUpdated += Manager_LatencyUpdated;
               Manager.StatusUpdated += Manager_StatusUpdated;
               if (FuckEmPorts.IsChecked == true)
               {
                  Manager.symmetricConnectionClientCount = 3000;
               }
               else
               {
                  Manager.symmetricConnectionClientCount = 100;
               }
               Task.Run(async () =>
               {
                  try
                  {
                     bool Result = await Manager.InitiateConnection();
                     this.Dispatcher.Invoke(() =>
                     {
                        if (!Result)
                        {
                           if (!ReferenceEquals(Manager, null))
                           {
                              Manager.Dispose();
                              isConnected = false;
                           }
                        }
                     });
                  }
                  catch (Exception ex)
                  {
                     Logger.Log(ex.Message + Environment.NewLine + ex.StackTrace);
                  }
               });
            }
            else //OFF
            {
               DisconnectClicked = true;
               ToggleOff();
            }

         }
         catch (Exception ex)
         {
            Logger.Log(ex.Message + ex.StackTrace);
         }
      }
      void ToggleOff()
      {
         EnableServerStatsUpdate.IsChecked = false;
         if (ToggleServerConnectionBtn.Content.ToString() == "Disconnect")
         {
            try
            {
               ToggleServerConnectionBtn.Content = "Connect";
               Task.Run(() =>
               {
                  if (Manager != null)
                  {
                     //try
                     //{

                     //}
                     //catch (Exception ex)
                     //{
                     //   Logger.Log($"{ex.Message + Environment.NewLine + ex.StackTrace}");
                     //}
                     if (!object.ReferenceEquals(Manager, null))
                     {
                        Manager.Dispose();
                        Manager = null;
                        isConnected = false;
                     }
                  }
               });
            }
            catch (Exception ex)
            {
               Logger.Log(ex.Message + ex.StackTrace);
               //throw;
            }
         }
      }
      private void Manager_StatusUpdated(object? sender, StatusResult e)
      {
         Logger.Log(e.ToString());
         this.Dispatcher.Invoke(() =>
         {
            switch (e)
            {
               case StatusResult.InternalServerStopped:
                  SocksProxyAddress.Text = "-";
                  HTTPProxyAddress.Text = "-";
                  break;
               case StatusResult.InternalServerStarted:
                  SocksProxyAddress.Text = "socks5://127.0.0.1:" + Manager.SocksPort;
                  HTTPProxyAddress.Text = "http://127.0.0.1:" + Manager.HTTPPort;
                  break;
               case StatusResult.UDPConnected:
                  UDPStatusLabel.Text = "Connected";
                  isConnected = true;
                  UDPStatusLabel.Foreground = Brushes.Lime;
                  break;
               case StatusResult.UDPConnecting:
                  UDPStatusLabel.Text = "Connecting...";
                  UDPStatusLabel.Foreground = Brushes.Orange;
                  break;
               case StatusResult.UDPError:
                  UDPStatusLabel.Text = "Connection failed.";
                  UDPStatusLabel.Foreground = Brushes.Red;
                  break;
               case StatusResult.UDPDisconnected:
                  UDPStatusLabel.Text = "Disconnected";
                  ToggleOff();
                  isConnected = false;
                  UDPStatusLabel.Foreground = Brushes.Red;
                  break;
               case StatusResult.SendingStun:
                  UDPStatusLabel.Text = "Sending STUN";
                  UDPStatusLabel.Foreground = Brushes.Orange;
                  break;
               case StatusResult.StunFailed:
                  UDPStatusLabel.Text = "STUN Failed, Try another server.";
                  UDPStatusLabel.Foreground = Brushes.Red;
                  break;
               case StatusResult.StunSuccess:
                  UDPStatusLabel.Text = "STUN Success.";
                  break;
               case StatusResult.HTTPConnected:
                  HTTPStatusLabel.Text = "Connected";
                  HTTPStatusLabel.Foreground = Brushes.Lime;
                  break;
               case StatusResult.HTTPConnecting:
                  HTTPStatusLabel.Text = "Connecting...";
                  HTTPStatusLabel.Foreground = Brushes.Orange;
                  break;
               case StatusResult.HTTPError:
                  HTTPStatusLabel.Text = ("Connection failed. Check log.");
                  HTTPStatusLabel.Foreground = Brushes.Red;
                  break;
               case StatusResult.HTTPDisconnected:
                  HTTPStatusLabel.Text = "Disconnected";
                  HTTPStatusLabel.Foreground = Brushes.Red;
                  break;
               default:
                  break;
            }
         });
      }
      TimeSpan Uptime = TimeSpan.Zero;
      private void OneSecondTimerCallback(object? Target)
      {
         if (isConnected)
         {
            Uptime = Uptime.Add(TimeSpan.FromSeconds(1));
            if (Manager == null)
               return;
            if (ReferenceEquals(Manager, null))
               return;
            if (Manager.LiteNetStats == null)
               return;
            this.Dispatcher.Invoke(() =>
            {
               try
               {
                  ulong DeltaInBytes = ((ulong)Manager.LiteNetStats.BytesReceived) - LastBytesReceivedCount;
                  ulong DeltaOutBytes = ((ulong)Manager.LiteNetStats.BytesSent) - LastBytesSentCount;

                  UptimeTextBox.Text = Uptime.ToString(@"hh\:mm\:ss");
                  LastBytesReceivedCount = (ulong)Manager.LiteNetStats.BytesReceived;
                  LastBytesSentCount = (ulong)Manager.LiteNetStats.BytesSent;
                  TotalInTextBox.Text = Utilities.HumanReadable(LastBytesReceivedCount);
                  TotalOutTextBox.Text = Utilities.HumanReadable(LastBytesSentCount);
                  InboundRateTextBox.Text = Utilities.HumanReadable(DeltaInBytes) + "/sec";
                  OutboundRateTextBox.Text = Utilities.HumanReadable(DeltaOutBytes) + "/sec";
                  ActiveConnectionsTextBox.Text = Manager.TotalConnections.ToString();
                  ChannelsTextBox.Text = Manager.TotalChannels.ToString();
               }
               catch (Exception ex)
               {
                  Logger.Log(ex.Message + ex.StackTrace);
               }
            });
         }
         else
         {
            Uptime = TimeSpan.Zero;
         }
         try
         {
            TimerCounter++;
            if (TimerCounter >= 10)
            {
               TimerCounter = 0;
               GC.Collect();
            }
         }
         catch (Exception ex)
         {
            Logger.Log(ex.StackTrace + Environment.NewLine + ex.Message);
         }
      }
      private void Manager_LatencyUpdated(object? sender, int e)
      {
         this.Dispatcher.Invoke(() =>
         {
            PingTextBox.Text = e * 2 + "ms";
            PakcetLossPercentTextBox.Text = Manager.LiteNetStats.PacketLossPercent + "%";
         });
      }

      private void Manager_NewLogArrived(object? sender, string e)
      {
         this.Dispatcher.Invoke(() =>
         {
            if (LogTextBox.Text.Length > 1024 * 1024)
            {
               LogTextBox.Text = LogTextBox.Text.Remove(0, 1024 * 256);
            }
            LogTextBox.AppendText(Environment.NewLine + e);
            LogTextBox.ScrollToEnd();
         });
      }

      private void ProxyModeCheckBox_Checked(object sender, RoutedEventArgs e)
      {
         if (!isConnected)
         {
            MessageBox.Show("Cannot set system proxy when not connected", "Error");
            ProxyModeCheckBox.IsChecked = false;
            return;
         }
         string ProxyString = $"http=127.0.0.1:{Manager.HTTPPort};https=127.0.0.1:{Manager.HTTPPort};socks=127.0.0.1:{Manager.SocksPort}";
         //string ProxyString = $"http=127.0.0.1:{Manager.HTTPPort}";
         MozWin32.setProxy(ProxyString, true);
         MessageBox.Show("Successfully set system proxy", "Success");
      }

      private void ProxyModeCheckBox_Unchecked(object sender, RoutedEventArgs e)
      {
         MozWin32.unsetProxy();
         MessageBox.Show("Successfully removed system proxy", "Success");
      }

      private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
      {
         MozWin32.unsetProxy();
      }

      private void TextBlock_MouseDown(object sender, MouseButtonEventArgs e)
      {
         GC.Collect();
         GC.Collect(GC.MaxGeneration);
         Logger.Log(GC.GetGCMemoryInfo().HeapSizeBytes.ToString());
      }

      private void EnableServerStatsUpdate_Checked(object sender, RoutedEventArgs e)
      {
         if (!isConnected)
         {
            MessageBox.Show("Cannot stream server stats when not connected", "Error");
            EnableServerStatsUpdate.IsChecked = false;
            return;
         }
         if (!ReferenceEquals(Manager, null))
         {
            Manager.MClient.ServerStatusInformationUpdated += MClient_ServerStatusInformationUpdated;
            bool Res = Manager.MClient.EnableServerStatusInformationStreamingForPeer();
            if (!Res)
            {
               MessageBox.Show("Couldn't enable server status information streaming.", "Error");
            }
            else
            {
               ServerStatsReceivingStatus.Content = "Request sent...";
            }
         }
         else
         {
            MessageBox.Show("Mozmanager not found???! I think I'm not connected but I am... try launching the app again...", "Error");
            EnableServerStatsUpdate.IsChecked = false;
         }
      }

      PropertyInfo[] ServerStatusInformationPropertyInfos = typeof(ServerStatusInformation).GetProperties();
      private void MClient_ServerStatusInformationUpdated(object? sender, MozUtil.Types.ServerStatusInformation e)
      {
         foreach (PropertyInfo item in ServerStatusInformationPropertyInfos)
         {
            string ItemValue = item.GetValue(e).ToString() ?? "null";
            if (item.Name == "Uptime")
            {
               this.Dispatcher.Invoke(() =>
               {
                  if (ItemValue == "-1")
                  {
                     ServerStatsReceivingStatus.Content = "Rejected.";
                     ServerStatsReceivingStatus.Foreground = Brushes.Red;
                  }
                  else
                  {
                     ServerStatsReceivingStatus.Content = "Enabled";
                     ServerStatsReceivingStatus.Foreground = Brushes.Green;
                  }
               });
            }
            StatsTableItem TableItem = ServerStatsTable.CreateOrGetItemWithKey(item.Name);
            TableItem.Dispatcher.Invoke(() =>
            {
               TableItem.Name.Content = item.Name;
               if (item.Name == "Uptime")
               {
                  TableItem.Value.Content = TimeSpan.FromTicks((long)item.GetValue(e)).ToString();
               }
               else
               {
                  TableItem.Value.Content = ItemValue;
               }
            });
         }
      }

      private void EnableServerStatsUpdate_Unchecked(object sender, RoutedEventArgs e)
      {
         if (!ReferenceEquals(Manager, null))
         {
            try
            {
               bool Res = Manager.MClient.EnableServerStatusInformationStreamingForPeer(-1, -1);
               Manager.MClient.ServerStatusInformationUpdated -= MClient_ServerStatusInformationUpdated;
               ServerStatsReceivingStatus.Content = "Not receiving";
               ServerStatsReceivingStatus.Foreground = EnableServerStatsUpdate.Foreground;
            }
            catch (Exception ex)
            {
               Logger.LogException(ex);
            }
         }
      }
   }
}
