using System.Windows;
using EveDeck.Models;
using EveDeck.ViewModels;

namespace EveDeck.Views;

public partial class SetupWizardWindow : Window
{
    private readonly SetupWizardViewModel _vm;

    public int ResultClientCount { get; private set; }
    public string ResultMonitorId { get; private set; } = "";
    public bool ResultFocusPreviewOnClick { get; private set; }
    public int ResultMasterSeat { get; private set; }
    public IReadOnlyList<SlotAssignment> ResultSlotAssignments { get; private set; } = [];

    public SetupWizardWindow(IEnumerable<MonitorInfo> monitors)
    {
        InitializeComponent();
        _vm = new SetupWizardViewModel(monitors);
        DataContext = _vm;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        FitToWorkArea();
    }

    // The wizard is a fixed-size NoResize dialog (600x580). On a small display at a high OS scaling
    // value that can exceed the monitor, putting the Back/Next buttons off-screen and unreachable --
    // the same class of issue Microsoft Store certification flagged on the main window under policy
    // 10.1.2.10 ("fully accessible on common resolution scaling values"). Clamp to the current
    // monitor work area and pull the window fully on-screen so navigation is always reachable.
    private void FitToWorkArea()
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd == 0) return;
            var wa = System.Windows.Forms.Screen.FromHandle(hwnd).WorkingArea; // physical pixels
            var src = PresentationSource.FromVisual(this);
            double dpiX = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            double dpiY = src?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
            if (dpiX <= 0) dpiX = 1;
            if (dpiY <= 0) dpiY = 1;

            double workW = wa.Width / dpiX, workH = wa.Height / dpiY;
            double waLeft = wa.Left / dpiX, waTop = wa.Top / dpiY;

            if (Width > workW) Width = workW;
            if (Height > workH) Height = workH;

            // CenterOwner can leave it partly off a small screen once resized -- pull it back in.
            if (Left < waLeft) Left = waLeft;
            if (Top < waTop) Top = waTop;
            if (Left + Width > waLeft + workW) Left = waLeft + workW - Width;
            if (Top + Height > waTop + workH) Top = waTop + workH - Height;
        }
        catch { /* if the screen can't be resolved, leave the declared size */ }
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
