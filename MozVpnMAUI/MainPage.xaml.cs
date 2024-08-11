#if WINDOWS
using Microsoft.Maui.Handlers;
#endif

using MozUtil;
using MozUtil.NatUtils;
#if ANDROID
using MozVpnMAUI.Platforms.Android;
#endif
using STUN;
using System.Net.Sockets;

namespace MozVpnMAUI
{
   public partial class MainPage : ContentPage
   {
      bool LogEnabled = false;
      ulong LastBytesSentCount = 0;
      ulong LastBytesReceivedCount = 0;
      Random RND = new Random();
      bool isConnected = false;
      string ServerURL = "http://127.0.0.1:5000";
      private event EventHandler<byte[]>? ServerCommandOverHTTP;
      byte[] KeepAlivePacket = new byte[] { 255, 255 };
      int HolePunchingTimeout = 10000;
      //UdpClient? stunClient;
      //STUNQueryResult? stunResult;
      MozManager? Manager;
      Timer OneSecondTimer;
      TransportMode uMode = TransportMode.LiteNet;
      void PlatformSpecific()
      {
#if WINDOWS
         SwitchHandler.Mapper.AppendToMapping("Custom", (h, v) =>
         {
            // Get rid of On/Off label beside switch, to match other platforms
            h.PlatformView.OffContent = string.Empty;
            h.PlatformView.OnContent = string.Empty;
            h.PlatformView.MinWidth = 0;
         });
         ProxyModeCheckBox.IsEnabled = true;
#endif
      }
      public MainPage()
      {
         InitializeComponent();
         PlatformSpecific();
         OneSecondTimer = new Timer(OneSecondTimerCallback, null, 1000, 1000);
         StunServerSelectorComboBox.ItemsSource = StaticInformation.StunServers;
         ConnectionTypeComboBox.SelectedIndex = 0;
         StunServerSelectorComboBox.SelectedIndex = 0;
         MaxChannelsComboBox.ItemsSource = StaticInformation.PossibleChannelCount;
         //MaxChannelsComboBox.SelectedIndex = 7;
         ServerSelectionComboBox.ItemsSource = StaticInformation.ServerList;
         ServerSelectionComboBox.SelectedIndex = 0;

         //ProxyModeCheckBox.IsEnabled = false;//CHANGE LATER
         //ProxyModeCheckBox.IsEnabled = true;
         TunModeCheckBox.IsEnabled = false;//CHANGE LATER
      }
      private void StartTestService()
      {
#if ANDROID
         Android.Content.Intent intent = new Android.Content.Intent(Android.App.Application.Context, typeof(FGServiceTest));
         Android.App.Application.Context.StartForegroundService(intent);
#endif
      }
      private async void ToggleServerConnectionSwitch_Toggled(object sender, ToggledEventArgs e)
      {
         bool TryReconnect = false;
         try
         {
            //if (isConnected == true) //Manual Disconnection
            //{
            //   TryReconnect = false;
            //}
            //else
            //{
            //   TryReconnect = true;//Unexpected Disconnection
            //}
            if (e.Value) //ON
            {
               StartTestService();
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
               if (UseProxyRadioBtn.IsChecked)
               {
                  if (!string.IsNullOrWhiteSpace(ConInitProxyServerEntry.Text))
                  {
                     ProxyAdr = ConInitProxyServerEntry.Text;
                  }
                  EnableProxy = true;
               }
               bool LocalServer = false;
               if (ServerURL == "http://127.0.0.1:5209/")
               {
                  LocalServer = true;
               }
               Manager = new MozManager(ServerURL, MaxChannels, StunServer, 6075, 6085, 10000, LocalServer, uMode, EnableProxy, ProxyAdr);
               Manager.NewLogArrived += Manager_NewLogArrived;
               Manager.LatencyUpdated += Manager_LatencyUpdated;
               Manager.StatusUpdated += Manager_StatusUpdated;
               if (FuckEmPorts.IsChecked)
               {
                  Manager.symmetricConnectionClientCount = 3000;
               }
               else
               {
                  Manager.symmetricConnectionClientCount = 100;
               }
               Task.Run(async () =>
               {
                  bool Result = await Manager.InitiateConnection();
                  this.Dispatcher.Dispatch(() =>
                  {
                     if (!Result)
                     {
                        if (!object.ReferenceEquals(Manager, null))
                        {
                           Manager.Dispose();
                           isConnected = false;
                        }

                     }
                  });
               });
            }
            else //OFF
            {
               StaticInformation.CallServiceStop();
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
                     isConnected = false;
                  }
               }
            }

         }
         catch (Exception ex)
         {
            Logger.Log(ex.Message + ex.StackTrace);
         }
         //if (TryReconnect)
         //{
         //   await Task.Delay(1000);
         //   ToggleServerConnectionSwitch.IsToggled = true;
         //   ToggleServerConnectionSwitch_Toggled(this, new ToggledEventArgs(true));
         //}
      }
      private void Manager_LatencyUpdated(object? sender, int e)
      {
         if (Manager.LiteNetStats == null)
         {
            return;
         }
         this.Dispatcher.Dispatch(() =>
         {
            int Ping = e * 2;
            PingTextBox.Text = Ping + "ms";
            PacketLossPercentTextBox.Text = Manager.LiteNetStats.PacketLossPercent + "%";
            if (Manager.LiteNetStats.PacketLossPercent > 20)
               PacketLossPercentTextBox.TextColor = Colors.Red;
            else if (Manager.LiteNetStats.PacketLossPercent > 10)
               PacketLossPercentTextBox.TextColor = Colors.Orange;
            else if (Manager.LiteNetStats.PacketLossPercent > 5)
               PacketLossPercentTextBox.TextColor = Colors.Yellow;
            else
               PacketLossPercentTextBox.TextColor = Colors.Lime;
            if (Ping > 250)
               PingTextBox.TextColor = Colors.Red;
            else if (Ping > 175)
               PingTextBox.TextColor = Colors.Orange;
            else if (Ping > 100)
               PingTextBox.TextColor = Colors.Yellow;
            else
               PingTextBox.TextColor = Colors.Lime;
         });
      }

      private void Manager_NewLogArrived(object? sender, string e)
      {
         if (LogEnabled)
         {
            this.Dispatcher.Dispatch(() =>
            {
               if (LogTextBox.Text.Length > 16384)
               {
                  LogTextBox.Text = LogTextBox.Text.Remove(0, 8192);
               }
               LogTextBox.Text += Environment.NewLine + e;
               //VertLayout.Children.Add(new Label() { Text = Addstring });
               ScrlVi.ScrollToAsync(EndOfText, ScrollToPosition.End, false);
            });
         }
      }
      private void Manager_StatusUpdated(object? sender, StatusResult e)
      {
         Logger.Log(e.ToString());
         this.Dispatcher.Dispatch(() =>
         {
            switch (e)
            {
               case StatusResult.UDPConnected:
                  UDPStatusLabel.Text = "Connected";
                  SocksProxyAddress.Text = "socks5://127.0.0.1:" + Manager.SocksPort;
                  HTTPProxyAddress.Text = "http://127.0.0.1:" + Manager.HTTPPort;
                  isConnected = true;
                  UDPStatusLabel.TextColor = Colors.Lime;
                  break;
               case StatusResult.UDPConnecting:
                  UDPStatusLabel.Text = "Connecting...";
                  UDPStatusLabel.TextColor = Colors.Orange;
                  break;
               case StatusResult.UDPError:
                  UDPStatusLabel.Text = "Connection failed.";
                  UDPStatusLabel.TextColor = Colors.Red;
                  break;
               case StatusResult.UDPDisconnected:
                  isConnected = false;
                  StaticInformation.CallServiceStop();
                  ToggleServerConnectionSwitch.IsToggled = false;
                  ToggleServerConnectionSwitch_Toggled(this, new ToggledEventArgs(false));
                  UDPStatusLabel.Text = "Disconnected";
                  UDPStatusLabel.TextColor = Colors.Red;
                  break;
               case StatusResult.SendingStun:
                  UDPStatusLabel.Text = "Sending STUN";
                  UDPStatusLabel.TextColor = Colors.Orange;
                  break;
               case StatusResult.StunFailed:
                  UDPStatusLabel.Text = "STUN Failed, Try another server.";
                  UDPStatusLabel.TextColor = Colors.Red;
                  break;
               case StatusResult.StunSuccess:
                  UDPStatusLabel.Text = "STUN Success.";
                  break;
               case StatusResult.HTTPConnected:
                  HTTPStatusLabel.Text = "Connected";
                  HTTPStatusLabel.TextColor = Colors.Lime;
                  break;
               case StatusResult.HTTPConnecting:
                  HTTPStatusLabel.Text = "Connecting...";
                  HTTPStatusLabel.TextColor = Colors.Orange;
                  break;
               case StatusResult.HTTPError:
                  HTTPStatusLabel.Text = ("Connection failed. Check log.");
                  HTTPStatusLabel.TextColor = Colors.Red;
                  break;
               case StatusResult.HTTPDisconnected:
                  HTTPStatusLabel.Text = "Disconnected";
                  HTTPStatusLabel.TextColor = Colors.Red;
                  break;
               default:
                  break;
            }
         });
      }
      private void OneSecondTimerCallback(object Target)
      {
         if (isConnected)
         {
            if (Manager.LiteNetStats == null)
            {
               return;
            }
            this.Dispatcher.Dispatch(() =>
            {
               try
               {
                  ulong DeltaInBytes = ((ulong)Manager.LiteNetStats.BytesReceived) - LastBytesReceivedCount;
                  ulong DeltaOutBytes = ((ulong)Manager.LiteNetStats.BytesSent) - LastBytesSentCount;

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
      }

      private void ProxyModeCheckBox_CheckedChanged(object sender, CheckedChangedEventArgs e)
      {
         try
         {
            if (e.Value)
            {
               if (!isConnected)
               {
                  DisplayAlert("Error", "Cannot set system proxy when not connected", "OK");
                  ProxyModeCheckBox.IsChecked = false;
                  return;
               }
               string ProxyString = $"http=127.0.0.1:{Manager.HTTPPort};https=127.0.0.1:{Manager.HTTPPort};socks=127.0.0.1:{Manager.SocksPort}";
               //string ProxyString = $"http=127.0.0.1:{Manager.HTTPPort}";
               MozWin32.setProxy(ProxyString, true);
               DisplayAlert("Success", "Successfully set system proxy", "OK");
            }
            else
            {
               MozWin32.unsetProxy();
               DisplayAlert("Success", "Successfully removed system proxy", "OK");
            }
         }
         catch (Exception ex)
         {
            DisplayAlert("Error", "Failed to set system proxy", "OK");
         }
      }
      private void SocksProxyEntryTapped(object sender, TappedEventArgs e)
      {
         Clipboard.Default.SetTextAsync(SocksProxyAddress.Text);
      }
      private void HTTPProxyEntryTapped(object sender, TappedEventArgs e)
      {
         Clipboard.Default.SetTextAsync(HTTPProxyAddress.Text);
      }

      private void LogEnabledCheckBox_CheckedChanged(object sender, CheckedChangedEventArgs e)
      {
         LogEnabled = e.Value;
      }
   }
}