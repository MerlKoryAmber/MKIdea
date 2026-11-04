using System.Windows;
using LinuxOps.Services;

namespace LinuxOps;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        ThemeManager.Init();
        base.OnStartup(e);
    }
}
