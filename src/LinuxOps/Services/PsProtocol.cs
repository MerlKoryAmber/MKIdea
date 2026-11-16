using System.Text.Json;
using System.Text.Json.Serialization;

namespace LinuxOps.Services;

/// <summary>
/// Обмен LinuxOps ↔ LinuxOps.PsHost: одна строка JSON на сообщение, по анонимным каналам.
/// Файл общий — подключён в помощник ссылкой (LinuxOps.PsHost.csproj).
/// </summary>
public sealed class PsMsg
{
    // В помощник: run, stop, reply, width, complete, exit.
    // Из помощника: ready, out, prompt, progress, done, completions.
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

    /// <summary>complete: позиция курсора в Text. completions: что заменить (Start, Length) и чем.</summary>
    public int Start { get; set; }
    public int Length { get; set; }
    public List<PsCompletion>? Completions { get; set; }

    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault
    };

    public string ToLine() => JsonSerializer.Serialize(this, Json);
    public static PsMsg? Parse(string line) => JsonSerializer.Deserialize<PsMsg>(line, Json);
}

/// <summary>Подсказка редактора: что вставить, как показать в списке, тип (Command, Parameter, Variable…), описание.</summary>
public sealed class PsCompletion
{
    public string Text { get; set; } = "";
    public string List { get; set; } = "";
    public string Type { get; set; } = "";
    public string? Tip { get; set; }
}

public sealed class PsField
{
    public string Name { get; set; } = "";
    public string Label { get; set; } = "";
    public bool Secret { get; set; }
    public string? Value { get; set; }
}
