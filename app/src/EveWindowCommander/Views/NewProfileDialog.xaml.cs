using System.Windows;

namespace EveWindowCommander.Views;

// Tiny prompt for the account count when creating a new custom profile; the on-monitor layout
// editor opens right after so the user places each slot visually.
public partial class NewProfileDialog : Window
{
    public int AccountCount => CountCombo.SelectedItem is int n ? n : 4;

    public NewProfileDialog(int defaultCount)
    {
        InitializeComponent();
        foreach (var n in Enumerable.Range(1, 15))
            CountCombo.Items.Add(n);
        CountCombo.SelectedItem = Math.Clamp(defaultCount, 1, 15);
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
