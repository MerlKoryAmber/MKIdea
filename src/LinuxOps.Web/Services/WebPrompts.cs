using LinuxOps.Models;
using LinuxOps.Services;

namespace LinuxOps.Web.Services;

/// <summary>Вопрос ядра, показанный в браузере: пароль sudo / ключа или доверие ключу хоста.</summary>
public sealed class PromptRequest
{
    public required string Kind { get; init; } // password | hostkey
    public required string Title { get; init; }
    public required string Message { get; init; }
    public bool Changed { get; init; }
    public TaskCompletionSource<string?> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>Канал вопросов одного окна браузера (цепи Blazor). Фоновый поток ядра ждёт ответа до 10 минут.</summary>
public sealed class PromptChannel
{
    private readonly SemaphoreSlim _one = new(1);
    public PromptRequest? Current { get; private set; }
    public event Action? Changed;

    public string? Ask(PromptRequest req)
    {
        _one.Wait();
        try
        {
            Current = req;
            Changed?.Invoke();
            return req.Answer.Task.Wait(TimeSpan.FromMinutes(10)) ? req.Answer.Task.Result : null;
        }
        finally
        {
            Current = null;
            Changed?.Invoke();
            _one.Release();
        }
    }
}

/// <summary>
/// IUserPrompts ядра → канал окна, которое запустило действие (AsyncLocal доходит до потоков PlanRunner через Task.Run).
/// Нет канала (фоновая задача) — отказ.
/// </summary>
public sealed class WebPrompts : IUserPrompts
{
    public static readonly AsyncLocal<PromptChannel?> Channel = new();

    public string? KeyPassphrase(HostConfig host, string keyPath, string? error) =>
        Channel.Value?.Ask(new PromptRequest
        {
            Kind = "password", Title = "Пароль ключа SSH",
            Message = (error is null ? "" : error + "\n") + $"Ключ для {host.Username}@{host.Host} зашифрован. Пароль хранится только в памяти сервиса до его перезапуска."
        });

    public bool TrustHostKey(HostConfig host, string message, bool changed) =>
        Channel.Value?.Ask(new PromptRequest { Kind = "hostkey", Title = "Ключ хоста", Message = message, Changed = changed }) == "1";

    public string? SudoPassword(HostConfig host) =>
        Channel.Value?.Ask(new PromptRequest
        {
            Kind = "password", Title = "Пароль sudo",
            Message = $"Пароль sudo для {host.Username}@{host.Host}. Хранится только в памяти сервиса до его перезапуска."
        });
}
