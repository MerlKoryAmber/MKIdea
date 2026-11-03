namespace AdServerGroups.Services;

/// <summary>Краткие подсказки к типичным ошибкам WinNT/SMB при работе с Administrators.</summary>
public static class RemoteErrorHints
{
    public static string Annotate(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return message;

        var tip = Suggest(message);
        if (tip is null)
            return message;
        if (message.Contains(tip, StringComparison.OrdinalIgnoreCase))
            return message;
        return $"{message} — {tip}";
    }

    public static string Annotate(Exception ex)
    {
        if (ex is null)
            return string.Empty;

        // AggregateException / TargetInvocationException прячут смысл во Inner*.
        var parts = new List<string>();
        foreach (var leaf in Flatten(ex))
        {
            var msg = leaf.Message?.Trim();
            if (string.IsNullOrWhiteSpace(msg))
                continue;
            if (parts.Any(p => p.Equals(msg, StringComparison.OrdinalIgnoreCase)))
                continue;
            // Пропускаем бессмысленные обёртки, если есть более глубокое сообщение.
            if (IsWrapperMessage(msg) && leaf.InnerException is not null)
                continue;
            parts.Add(msg);
        }

        if (parts.Count == 0)
            parts.Add(ex.GetBaseException().Message);

        return Annotate(string.Join(" → ", parts));
    }

    private static bool IsWrapperMessage(string message) =>
        message.Contains("One or more errors occurred", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("Exception has been thrown by the target of an invocation", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("See the inner exception", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<Exception> Flatten(Exception ex)
    {
        if (ex is AggregateException agg)
        {
            foreach (var inner in agg.Flatten().InnerExceptions)
            foreach (var leaf in Flatten(inner))
                yield return leaf;
            yield break;
        }

        yield return ex;
        if (ex.InnerException is not null)
        {
            foreach (var leaf in Flatten(ex.InnerException))
                yield return leaf;
        }
    }

    public static string? Suggest(string message)
    {
        var text = message ?? string.Empty;

        if (ContainsAny(text,
                "недоступен", "нет ответа на порт 445", "445"))
        {
            return "проверьте сеть, firewall и SMB (TCP 445) до сервера";
        }

        if (ContainsAny(text,
                "access is denied", "access denied", "отказано в доступе",
                "0x80070005", "0x8007052e", "unauthorized", "логон", "logon failure",
                "неверное имя пользователя", "password"))
        {
            return "сервер жив, но нет прав на Remote SAM/Administrators — «Переподключить» под DOMAIN\\user (локальный SSO часто даёт Access denied), учётка должна быть локальным админом на сервере";
        }

        if (ContainsAny(text,
                "network path was not found", "сетевой путь не найден",
                "rpc server is unavailable", "сервер RPC недоступен",
                "0x800706ba", "0x80070775"))
        {
            return "сервер не отвечает по RPC/WinNT — firewall, служба Server, DNS-имя";
        }

        if (ContainsAny(text, "timed out", "timeout", "нет ответа за"))
        {
            return "сервер тормозит или режет удалённые вызовы — увеличьте ожидание только если сеть стабильна";
        }

        return null;
    }

    private static bool ContainsAny(string text, params string[] needles)
    {
        foreach (var n in needles)
        {
            if (text.Contains(n, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
