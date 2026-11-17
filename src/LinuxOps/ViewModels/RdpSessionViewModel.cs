using CommunityToolkit.Mvvm.ComponentModel;
using LinuxOps.Models;

namespace LinuxOps.ViewModels;

/// <summary>Состояние одной RDP-сессии во вкладке «Подключения». Пароль (в <see cref="Options"/>) — до закрытия вкладки.</summary>
public partial class RdpSessionViewModel : ObservableObject
{
    public RdpSessionViewModel(WindowsHost host, RdpOptions options)
    {
        Host = host;
        Options = options;
    }

    public WindowsHost Host { get; }

    /// <summary>Что выбрано в окне подключения. Переподключение — с ним же, без вопроса.</summary>
    public RdpOptions Options { get; set; }

    [ObservableProperty] private bool connected;
    [ObservableProperty] private bool connecting;
    [ObservableProperty] private string status = "Не подключено";

    /// <summary>Подпись режима входа для строки состояния.</summary>
    public string ModeText => WindowsHost.AuthLabel(Options.Auth) + (Options.AdminConsole ? " · /admin" : "");
}
