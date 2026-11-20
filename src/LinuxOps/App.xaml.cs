using System.Windows;
using LinuxOps.Services;

namespace LinuxOps;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        CrashLog.Install(this);
        ThemeManager.Init();
        base.OnStartup(e);
    }
}
