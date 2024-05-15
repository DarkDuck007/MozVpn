using Android;
using Android.App;
using Android.Net;
using Java.Nio.Channels;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MozVpnMAUI.Platforms.Android
{
   [Service(Permission = Manifest.Permission.BindVpnService, Exported = false)]
   [IntentFilter(new[] { "android.net.VpnService" })]

   public class AndroidPacketCapture : VpnService
   {
      public AndroidPacketCapture()
      {

      }

   }
}
