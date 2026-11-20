using System.Windows;
using LinuxOps.Services;

namespace LinuxOps;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        CrashReporter.Install(this);
        UserPrompts.Current = new WpfPrompts();
        ThemeManager.Init();
        base.OnStartup(e);
    }
}
