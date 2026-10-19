using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace SptLinuxInstaller.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var previewIndex = Array.IndexOf(e.Args, "--render-preview");
        var preview = previewIndex >= 0 && previewIndex + 1 < e.Args.Length;
        var window = new MainWindow(preview);
        MainWindow = window;
        if (preview)
        {
            window.ShowActivated = false;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -10000;
            window.Top = -10000;
        }
        window.Show();
        if (preview)
        {
            window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                try
                {
                    window.UpdateLayout();
                    var content = (FrameworkElement)window.Content;
                    var target = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    target.Render(content);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(target));
                    var path = Path.GetFullPath(e.Args[previewIndex + 1]);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    using var stream = File.Create(path);
                    encoder.Save(stream);
                }
                finally { Shutdown(); }
            }));
        }
    }
}
