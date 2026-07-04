using System.Windows;
using EveDeck.Models;
using EveDeck.ViewModels;

namespace EveDeck.Views;

public partial class SetupWizardWindow : Window
{
    private readonly SetupWizardViewModel _vm;

    public int ResultClientCount { get; private set; }
    public string ResultMonitorId { get; private set; } = "";
    public bool ResultUseWgc { get; private set; }
    public bool ResultFocusPreviewOnClick { get; private set; }
    public int ResultMasterSeat { get; private set; }
    public IReadOnlyList<SlotAssignment> ResultSlotAssignments { get; private set; } = [];

    public SetupWizardWindow(IEnumerable<MonitorInfo> monitors)
    {
        InitializeComponent();
        _vm = new SetupWizardViewModel(monitors);
        DataContext = _vm;
    }

    private void Back_Click(object sender, RoutedEventArgs e) => _vm.Back();

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (!_vm.IsLastStep)
        {
            _vm.Next();
            return;
        }

        ResultClientCount = _vm.ClientCount;
        ResultMonitorId = _vm.SelectedMonitor?.Id ?? "";
        ResultUseWgc = _vm.UseWgc;
        ResultFocusPreviewOnClick = _vm.FocusPreviewOnClick;
        ResultMasterSeat = _vm.MasterSeatNumber;
        ResultSlotAssignments = _vm.WizardSlots.ToList();
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
