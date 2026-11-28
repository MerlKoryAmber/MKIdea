using LinuxOps.Models;
using LinuxOps.Services;

namespace LinuxOps.Web.Services;

/// <summary>Окно поверх страницы (как модальные окна настольного): ждёт ответа пользователя.</summary>
public sealed class UiDialog
{
    public required string Kind { get; init; } // info | confirm | params | adduser | host | folder | action
    public string Title { get; set; } = "LinuxOps";
    public string Message { get; init; } = "";
    public string Hint { get; set; } = "";
    /// <summary>params: действия с параметрами; значения — в Values по ключу «id:Name».</summary>
    public List<ActionDefinition> Actions { get; init; } = new();
    public Dictionary<string, string> Values { get; init; } = new();
    public StoredHost? Host { get; init; }
    public HostFolder? Folder { get; init; }
    public ActionDefinition? Action { get; init; }
    public List<HostConfig> Hosts { get; init; } = new();
    /// <summary>access, baseusers: отмеченные хосты сервиса (с id и папкой — для прав и подключения).</summary>
    public List<StoredHost> Targets { get; init; } = new();
    public TaskCompletionSource<object?> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>Итог окна «Хост»: пароль / ключ — null не менять, "" убрать.</summary>
public sealed class HostEdit
{
    public required HostConfig Config { get; init; }
    public string? Password { get; init; }
    public string? Key { get; init; }
    public string? KeyName { get; init; }
}

/// <summary>Очередь окон одного браузера. Компонент DialogHost показывает верхнее.</summary>
public sealed class UiDialogs
{
    private readonly List<UiDialog> _stack = new();
    public UiDialog? Top => _stack.LastOrDefault();
    public event Action? Changed;

    public async Task<object?> Show(UiDialog d)
    {
        _stack.Add(d);
        Changed?.Invoke();
        try { return await d.Result.Task; }
        finally
        {
            _stack.Remove(d);
            Changed?.Invoke();
        }
    }

    /// <summary>Окно поменяло свой заголовок — перерисовать рамку.</summary>
    public void Touch() => Changed?.Invoke();

    public Task Info(string message, string title = "LinuxOps") => Show(new UiDialog { Kind = "info", Message = message, Title = title });

    public async Task<bool> Confirm(string message, string title = "LinuxOps") =>
        await Show(new UiDialog { Kind = "confirm", Message = message, Title = title }) is true;

    /// <summary>Окно «Параметры действий» (как PsRunWindow): null — отмена.</summary>
    public async Task<Dictionary<string, string>?> Params(IReadOnlyList<ActionDefinition> actions, string title = "Параметры действий", string hint = "",
        Dictionary<string, string>? remembered = null)
    {
        var d = new UiDialog
        {
            Kind = "params", Title = title, Actions = actions.Where(a => a.Parameters.Count > 0).ToList(),
            Hint = hint.Length > 0 ? hint : "Значения передаются в скрипт как есть. Последние запоминаются до закрытия страницы."
        };
        foreach (var a in d.Actions)
        foreach (var p in a.Parameters)
        {
            var key = a.Id + ":" + p.Name;
            d.Values[key] = remembered is not null && remembered.TryGetValue(key, out var v) ? v : p.Default;
        }
        return await Show(d) is true ? d.Values : null;
    }
}
