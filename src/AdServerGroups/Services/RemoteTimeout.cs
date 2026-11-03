using System.Net.Sockets;

namespace AdServerGroups.Services;

public static class RemoteTimeout
{
    public static readonly TimeSpan DefaultReach = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan DefaultOperation = TimeSpan.FromSeconds(20);

    public static bool CanReachHost(string host, int port = 445, TimeSpan? timeout = null)
    {
        var wait = timeout ?? DefaultReach;
        try
        {
            using var client = new TcpClient();
            var connect = client.ConnectAsync(host, port);
            if (!connect.Wait(wait))
                return false;
            return connect.Status == TaskStatus.RanToCompletion && client.Connected;
        }
        catch
        {
            return false;
        }
    }

    public static void EnsureReachable(string computerName)
    {
        if (!CanReachHost(computerName))
        {
            throw new TimeoutException(RemoteErrorHints.Annotate(
                $"Сервер {computerName} недоступен (нет ответа на порт 445 за {DefaultReach.TotalSeconds:0} с)."));
        }
    }

    public static T Run<T>(string operation, Func<T> action, TimeSpan? timeout = null)
    {
        var wait = timeout ?? DefaultOperation;
        var task = Task.Run(action);
        if (!task.Wait(wait))
        {
            throw new TimeoutException(
                $"{operation}: нет ответа за {wait.TotalSeconds:0} с.");
        }

        return task.GetAwaiter().GetResult();
    }

    public static void Run(string operation, Action action, TimeSpan? timeout = null)
    {
        Run(operation, () =>
        {
            action();
            return true;
        }, timeout);
    }
}
