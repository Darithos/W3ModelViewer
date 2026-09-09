using System.Windows;

namespace Wc3ModelViewer;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        BlpJpegCodec.Install();
    }
}
