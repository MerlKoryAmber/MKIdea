using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using LinuxOps.Models;
using LinuxOps.Services;
using Microsoft.Win32;

namespace LinuxOps;

/// <summary>Модули PowerCLI (интернет / ZIP / папка / экспорт) и профили vCenter.</summary>
public partial class VsphereWindow : Window
{
    private readonly ObservableCollection<VCenterProfile> _profiles;
    private bool _filling;
    private bool _busy;

    public VsphereWindow()
    {
        InitializeComponent();
        _profiles = new ObservableCollection<VCenterProfile>(DefaultsStore.Load().VCenters);
        Profiles.ItemsSource = _profiles;
        if (_profiles.Count > 0) Profiles.SelectedIndex = 0;
        RefreshModules();
    }

    public static bool Open(Window? owner)
    {
        var w = new VsphereWindow { Owner = owner };
        return w.ShowDialog() == true;
    }

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

    // ---------- Профили vCenter ----------

    private VCenterProfile? Current => Profiles.SelectedItem as VCenterProfile;

    private void Profiles_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _filling = true;
        var p = Current;
        Form.IsEnabled = p is not null;
        NameBox.Text = p?.Name ?? "";
        ServerBox.Text = p?.Server ?? "";
        UserBox.Text = p?.User ?? "";
        CertBox.IsChecked = p?.IgnoreCert ?? false;
        _filling = false;
    }

    private void Form_Changed(object sender, RoutedEventArgs e)
    {
        if (_filling || Current is not { } p) return;
        p.Name = NameBox.Text;
        p.Server = ServerBox.Text;
        p.User = UserBox.Text;
        p.IgnoreCert = CertBox.IsChecked == true;
        Profiles.Items.Refresh();
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var p = new VCenterProfile { Name = "vCenter", User = "administrator@vsphere.local" };
        _profiles.Add(p);
        Profiles.SelectedItem = p;
        ServerBox.Focus();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Current is not { } p) return;
        if (!AppDialog.Confirm(this, $"Удалить профиль «{p.Title}»?", "vSphere")) return;
        _profiles.Remove(p);
        if (_profiles.Count > 0) Profiles.SelectedIndex = 0;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_profiles.FirstOrDefault(p => string.IsNullOrWhiteSpace(p.Server)) is { } empty)
        {
            Profiles.SelectedItem = empty;
            AppDialog.Info(this, "Укажите адрес vCenter.", "vSphere");
            return;
        }

        DefaultsStore.SaveVCenters(_profiles);
        DialogResult = true;
    }
}
