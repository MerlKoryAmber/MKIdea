using System.Text.Json;
using System.Text.Json.Serialization;

namespace LinuxOps.Services;

/// <summary>
/// Обмен LinuxOps ↔ LinuxOps.PsHost: одна строка JSON на сообщение, по анонимным каналам.
/// Файл общий — подключён в помощник ссылкой (LinuxOps.PsHost.csproj).
/// </summary>
public sealed class PsMsg
{
    // В помощник: run, stop, reply, width, exit.
    // Из помощника: ready, out, prompt, progress, done.
    public string T { get; set; } = "";
    public int Id { get; set; }
    public string? Text { get; set; }

    /// <summary>out: out | err | warn | verbose | debug | host. prompt: line | secure | fields | credential | choice.</summary>
    public string? Kind { get; set; }

    /// <summary>Цвет Write-Host (ConsoleColor), пусто — обычный.</summary>
    public string? Color { get; set; }
    public bool NoNewLine { get; set; }

    public string? Caption { get; set; }
    public string? Message { get; set; }
    public List<PsField>? Fields { get; set; }
    public List<string>? Choices { get; set; }
    public int Default { get; set; }

    /// <summary>reply: значения полей по порядку; null — отмена.</summary>
    public List<string>? Values { get; set; }
    public bool Ok { get; set; }
    public string? Location { get; set; }
    public int Width { get; set; }

    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault
    };

    public string ToLine() => JsonSerializer.Serialize(this, Json);
    public static PsMsg? Parse(string line) => JsonSerializer.Deserialize<PsMsg>(line, Json);
}

public sealed class PsField
{
    public string Name { get; set; } = "";
    public string Label { get; set; } = "";
    public bool Secret { get; set; }
    public string? Value { get; set; }
}
