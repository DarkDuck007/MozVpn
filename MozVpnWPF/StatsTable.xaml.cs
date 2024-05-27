using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
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
   /// Interaction logic for StatsTable.xaml
   /// </summary>
   public partial class StatsTable : UserControl
   {
      private Dictionary<string, StatsTableItem> TableContentDictionary { get; set; } = new Dictionary<string, StatsTableItem>();
      public StatsTable()
      {
         InitializeComponent();
      }
      public bool RemoveItemWithKey(string key)
      {
         if (TableContentDictionary.ContainsKey(key))
         {
            this.Dispatcher.Invoke(() =>
            {
               ServerStatsDataWrapPanel.Children.Remove(TableContentDictionary[key]);
            });
            return TableContentDictionary.Remove(key);
         }
         return false;
      }
      public void ClearItems()
      {
         this.Dispatcher.Invoke(() =>
         {
            TableContentDictionary.Clear();
            ServerStatsDataWrapPanel.Children.Clear();
         });
      }
      public StatsTableItem GetItemByKey(string key)
      {
         return TableContentDictionary[key];
      }
      public StatsTableItem CreateOrGetItemWithKey(string key)
      {
         if (TableContentDictionary.ContainsKey(key))
         {
            return TableContentDictionary[key];
         }
         else
         {
            return CreateNewItem(key);
         }
      }
      public StatsTableItem CreateNewItem(string key)
      {
         if (TableContentDictionary.ContainsKey(key))
            throw new ArgumentException("Key already exists.");
         bool Result = false;
         this.Dispatcher.Invoke(() =>
         {
            StatsTableItem statsTableItem = new StatsTableItem();
            if (AddItem(key, statsTableItem))
            {
               Result = true;
            }
         });

         if (Result)
         {
            return TableContentDictionary[key];
         }
         else
         {
            throw new Exception("Couldn't add the item... why I wonder.");
         }
      }
      public bool AddItem(string key, StatsTableItem item)
      {

         bool res = TableContentDictionary.TryAdd(key, item);
         if (res)
         {
            this.Dispatcher.Invoke(() =>
            {
               ServerStatsDataWrapPanel.Children.Add(item);
            });
         }
         return res;
      }
   }
}
