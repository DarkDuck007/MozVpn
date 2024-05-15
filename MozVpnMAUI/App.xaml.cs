using MozUtil;

namespace MozVpnMAUI
{
   public partial class App : Application
   {
      public App()
      {
         InitializeComponent();
         MainPage = new AppShell();
         MainPage.Unloaded += (object sender, EventArgs e) =>
         {
            MozWin32.unsetProxy();
         };
      }
   }
}