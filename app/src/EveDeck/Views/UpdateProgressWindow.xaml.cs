using System.Windows;

namespace EveDeck.Views;

public partial class UpdateProgressWindow : Window
{
    public UpdateProgressWindow()
    {
        InitializeComponent();
    }

    public void SetStatus(string text) => StatusText.Text = text;

    public void SetProgress(double? percent)
    {
        if (percent is { } p)
        {
            Progress.IsIndeterminate = false;
            Progress.Value = p;
        }
        else
        {
            Progress.IsIndeterminate = true;
        }
    }
}
