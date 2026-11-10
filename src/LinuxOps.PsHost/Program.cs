using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using LinuxOps.Services;

namespace LinuxOps.PsHost;

/// <summary>Канал к LinuxOps: строки JSON, запросы ввода ждут ответа по Id.</summary>
public sealed class Channel
{
    private readonly StreamWriter _out;
    private readonly object _lock = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<List<string>?>> _asks = new();
    private int _askId = 1_000_000;

    public Channel(StreamWriter output) => _out = output;

    public void Send(PsMsg m)
    {
        lock (_lock) { _out.WriteLine(m.ToLine()); }
    }

    /// <summary>Запрос ввода у пользователя; null — отменил (или выполнение остановлено).</summary>
    public List<string>? Ask(PsMsg prompt)
    {
        prompt.T = "prompt";
        prompt.Id = Interlocked.Increment(ref _askId);
        var tcs = new TaskCompletionSource<List<string>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _asks[prompt.Id] = tcs;
        Send(prompt);
        try { return tcs.Task.GetAwaiter().GetResult(); }
        finally { _asks.TryRemove(prompt.Id, out _); }
    }

    public void Answer(PsMsg reply)
    {
        if (_asks.TryGetValue(reply.Id, out var tcs)) tcs.TrySetResult(reply.Ok ? reply.Values ?? new List<string>() : null);
    }

    public void CancelAsks()
    {
        foreach (var t in _asks.Values) t.TrySetResult(null);
    }
}

public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("LinuxOps.PsHost запускается из LinuxOps.");
            return 2;
        }

        using var input = new AnonymousPipeClientStream(PipeDirection.In, args[0]);
        using var output = new AnonymousPipeClientStream(PipeDirection.Out, args[1]);
        var reader = new StreamReader(input, new UTF8Encoding(false));
        var writer = new StreamWriter(output, new UTF8Encoding(false)) { AutoFlush = true };
        var channel = new Channel(writer);
        var engine = new PsEngine(channel);
        channel.Send(new PsMsg { T = "ready", Location = engine.Location, Text = engine.Version });

        while (reader.ReadLine() is { } line)
        {
            PsMsg? m;
            try { m = PsMsg.Parse(line); }
            catch { continue; }
            if (m is null) continue;
            switch (m.T)
            {
                case "run": engine.Start(m.Id, m.Text ?? ""); break;
                case "stop": channel.CancelAsks(); engine.Stop(); break;
                case "reply": channel.Answer(m); break;
                case "width": engine.Host.Width = m.Width; break;
                case "exit": Environment.Exit(0); break;
            }
        }

        // LinuxOps закрылся (канал оборван) — уходим вместе с ним, не дожидаясь потоков runspace.
        Environment.Exit(0);
        return 0;
    }
}
