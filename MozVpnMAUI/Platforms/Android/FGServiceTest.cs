using Android.Accessibilityservice.AccessibilityService;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.App;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MozVpnMAUI.Platforms.Android
{
   [Service(ForegroundServiceType = ForegroundService.TypeDataSync)]
   internal class FGServiceTest : Service
   {
      private string NOTIFICATION_CHANNEL_ID = "1000";
      private int NOTIFICATION_ID = 1;
      private string NOTIFICATION_CHANNEL_NAME = "ForegroundServiceNotification";

      private void startForegroundService()
      {
         var notifcationManager = GetSystemService(Context.NotificationService) as NotificationManager;

         if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
         {
            createNotificationChannel(notifcationManager);
         }

         var notification = new NotificationCompat.Builder(this, NOTIFICATION_CHANNEL_ID)
            .SetAutoCancel(false)
            .SetOngoing(true)
            .SetSmallIcon(Resource.Mipmap.appicon)
            .SetContentTitle("ForegroundService")
            .SetContentText("Foreground Service is running")
            .Build();
         StaticInformation.StopServiceEvent += StaticInformation_StopServiceEvent;
         if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
         {
            StartForeground(NOTIFICATION_ID, notification, ForegroundService.TypeDataSync);
         }
         else
         {
            StartForeground(NOTIFICATION_ID, notification);
         }

         //Task.Run(async () => {
         //   await Task.Delay(10000);
         //}).Wait();

      }

      private void StaticInformation_StopServiceEvent(object sender, EventArgs e)
      {
         StopForeground(true);
      }

      private void createNotificationChannel(NotificationManager notificationMnaManager)
      {
         var channel = new NotificationChannel(NOTIFICATION_CHANNEL_ID, NOTIFICATION_CHANNEL_NAME,
         NotificationImportance.Low);
         notificationMnaManager.CreateNotificationChannel(channel);
      }

      public override IBinder OnBind(Intent intent)
      {
         //Task.Run(async () => {
         //   await Task.Delay(10000);
         //}).Wait();
         return null;
      }


      public override StartCommandResult OnStartCommand(Intent intent, StartCommandFlags flags, int startId)
      {
         startForegroundService();
         return StartCommandResult.NotSticky;
      }
   }
}
