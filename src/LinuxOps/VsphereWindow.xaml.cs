using System.Diagnostics;
using System.IO;
using System.Windows;
using LinuxOps.Services;
using Microsoft.Win32;

namespace LinuxOps;

/// <summary>Модули PowerCLI: интернет / ZIP / папка / экспорт. Сами vCenter — в Inventory.</summary>
public partial class VsphereWindow : Window
{
    private bool _busy;

    public VsphereWindow()
    {
        InitializeComponent();
        RefreshModules();
    }

    public static void Open(Window? owner) => new VsphereWindow { Owner = owner }.ShowDialog();

    // ---------- Модули ----------

    private void RefreshModules()
    {
        var core = PowerCliInstaller.CoreInstalled();
        CoreText.Text = core is null
            ? "PowerCLI не установлен — vSphere-действия работать не будут."
            : $"PowerCLI установлен: {PowerCliInstaller.CoreModule} {core} · модулей: {PowerCliInstaller.Installed().Count}";
        DirText.Text = "Папка: " + PowerCliInstaller.ModulesDir;
    }

    private async Task Work(string title, Func<IProgress<string>, Task> work)
    {
        if (_busy) return;
        _busy = true;
        IsEnabled = false;
        var progress = new Progress<string>(t => ProgressText.Text = t);
        try
        {
            ProgressText.Text = title + "…";
            await work(progress);
        }
        catch (Exception ex)
        {
            ProgressText.Text = "";
            AppDialog.Info(this, title + ": " + ex.Message, "vSphere");
        }
        finally
        {
            _busy = false;
            IsEnabled = true;
            RefreshModules();
        }
    }

    private async void Download_Click(object sender, RoutedEventArgs e) =>
        await Work("Скачивание PowerCLI", p => PowerCliInstaller.DownloadAsync(p, CancellationToken.None));

    private async void FromZip_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Архив модулей (*.zip)|*.zip" };
        if (dlg.ShowDialog(this) != true) return;
        await Work("Установка из ZIP", p => Task.Run(() => PowerCliInstaller.InstallFrom(dlg.FileName, p)));
    }

    private async void FromFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Папка с модулями PowerShell" };
        if (dlg.ShowDialog(this) != true) return;
        await Work("Установка из папки", p => Task.Run(() => PowerCliInstaller.InstallFrom(dlg.FolderName, p)));
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (PowerCliInstaller.Installed().Count == 0)
        {
            AppDialog.Info(this, "Модулей нет — сначала скачайте или установите их.", "vSphere");
            return;
        }

        var dlg = new SaveFileDialog { Filter = "Архив (*.zip)|*.zip", FileName = "LinuxOps-Modules.zip" };
        if (dlg.ShowDialog(this) != true) return;
        await Work("Сохранение в ZIP", p => Task.Run(() =>
        {
            PowerCliInstaller.ExportZip(dlg.FileName);
            p.Report("сохранено: " + dlg.FileName);
        }));
    }

    private void OpenDir_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(PowerCliInstaller.ModulesDir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{PowerCliInstaller.ModulesDir}\"") { UseShellExecute = true });
    }
}
