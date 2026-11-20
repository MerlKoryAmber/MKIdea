using System.IO;
using System.Windows;
using LinuxOps.Models;
using LinuxOps.Services;

namespace LinuxOps;

/// <summary>Вопросы ядра (SSH) — окнами WPF в потоке интерфейса.</summary>
public sealed class WpfPrompts : IUserPrompts
{
    public string? KeyPassphrase(HostConfig host, string keyPath, string? error) => OnUi(app =>
    {
        var hint = (error is null ? "" : error + "\n")
            + $"Ключ {Path.GetFileName(keyPath)} для {host.Username}@{host.Host} зашифрован. "
            + "Пароль хранится только в памяти до закрытия LinuxOps.";
        var dlg = new SudoPasswordWindow("Пароль ключа SSH", hint)
        {
            Owner = app.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? app.MainWindow
        };
        return dlg.ShowDialog() == true ? dlg.Password : null;
    });

    public bool TrustHostKey(HostConfig host, string message, bool changed) =>
        OnUi(app => new HostKeyWindow(message, changed) { Owner = app.MainWindow }.ShowDialog() == true ? "1" : null) == "1";

    public string? SudoPassword(HostConfig host) => OnUi(app =>
    {
        var dlg = new SudoPasswordWindow(host) { Owner = app.MainWindow };
        return dlg.ShowDialog() == true && !string.IsNullOrEmpty(dlg.Password) ? dlg.Password : null;
    });

    private static string? OnUi(Func<Application, string?> show)
    {
        var app = Application.Current;
        if (app is null) return null;
        return app.Dispatcher.CheckAccess() ? show(app) : app.Dispatcher.Invoke(() => show(app));
    }
}
