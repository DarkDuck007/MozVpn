using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Moz_Avalonia.Services;

namespace Moz_Avalonia.Views;

public partial class StunNatChoiceDialog : Window
{
    public StunNatChoiceDialog() : this([]) { }

    public StunNatChoiceDialog(IReadOnlyList<StunNatGroup> groups)
    {
        InitializeComponent();
        Groups = new ObservableCollection<StunNatGroup>(groups);
        DataContext = this;
        GroupsList.SelectedItem = Groups.OrderByDescending(x => x.Count)
            .ThenBy(x => x.FastestLatencyMs).FirstOrDefault();
    }

    public ObservableCollection<StunNatGroup> Groups { get; }

    private void CancelClicked(object? sender, RoutedEventArgs e) => Close(null);

    private void AcceptClicked(object? sender, RoutedEventArgs e)
    {
        if (GroupsList.SelectedItem is StunNatGroup selected) Close(selected);
    }
}
