using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace EveWindowCommander.Views;

public partial class SplashWindow : Window
{
    public SplashWindow()
    {
        InitializeComponent();
        LoadBackgroundImage();
    }

    private void LoadBackgroundImage()
    {
        var appDir = AppContext.BaseDirectory;
        var bgPath = Path.Combine(appDir, "Assets", "bg.png");
        if (!File.Exists(bgPath)) bgPath = Path.Combine(appDir, "bg.png");
        if (!File.Exists(bgPath)) return;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(bgPath);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            BgImage.Source = bmp;
        }
        catch { }
    }
}
