using LinuxOps.Models;

namespace LinuxOps.Services;

/// <summary>
/// Вопросы пользователю из ядра (SSH): пароль ключа, доверие ключу хоста, пароль sudo. Ядро не знает, кто спрашивает:
/// настольный LinuxOps — окном WPF, LinuxOps.Web — диалогом в браузере. Вызывается из фонового потока и ждёт ответа.
/// </summary>
public interface IUserPrompts
{
    /// <summary>Пароль зашифрованного ключа; null — отказ.</summary>
    string? KeyPassphrase(HostConfig host, string keyPath, string? error);

    /// <summary>Доверять ли ключу хоста (новый или изменившийся).</summary>
    bool TrustHostKey(HostConfig host, string message, bool changed);

    /// <summary>Пароль sudo; null — отказ.</summary>
    string? SudoPassword(HostConfig host);
}

public static class UserPrompts
{
    /// <summary>Задаёт приложение при старте. Нет — вопросы получают отказ.</summary>
    public static IUserPrompts? Current { get; set; }
}
