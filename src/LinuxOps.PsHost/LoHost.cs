using System.Collections.ObjectModel;
using System.Globalization;
using System.Management.Automation;
using System.Management.Automation.Host;
using System.Security;
using LinuxOps.Services;

namespace LinuxOps.PsHost;

/// <summary>Хост PowerShell: вывод — в консоль вкладки, ввод (Read-Host, Get-Credential, выбор) — окнами LinuxOps.</summary>
public sealed class LoHost : PSHost
{
    private readonly LoUi _ui;
    private readonly Guid _id = Guid.NewGuid();

    public LoHost(Channel ch) => _ui = new LoUi(ch, this);

    public int Width { get; set; } = 120;

    /// <summary>Сколько ошибок показано за запуск: ошибки идут через Out-Default, HadErrors их не видит.</summary>
    public int Errors { get => _ui.Errors; set => _ui.Errors = value; }
    public override string Name => "LinuxOps";
    public override Version Version => new(1, 0);
    public override Guid InstanceId => _id;
    public override PSHostUserInterface UI => _ui;
    public override CultureInfo CurrentCulture => CultureInfo.CurrentCulture;
    public override CultureInfo CurrentUICulture => CultureInfo.CurrentUICulture;
    public override void SetShouldExit(int exitCode) { }
    public override void EnterNestedPrompt() => throw new NotSupportedException("Вложенный режим не поддерживается.");
    public override void ExitNestedPrompt() { }
    public override void NotifyBeginApplication() { }
    public override void NotifyEndApplication() { }
}

internal sealed class LoUi : PSHostUserInterface
{
    private readonly Channel _ch;
    private readonly LoRawUi _raw;

    public LoUi(Channel ch, LoHost host)
    {
        _ch = ch;
        _raw = new LoRawUi(host);
    }

    public override PSHostRawUserInterface RawUI => _raw;

    // Форматтер PowerShell 7 красит заголовки таблиц ANSI-кодами — консоль вкладки их не рисует.
    private static readonly System.Text.RegularExpressions.Regex Ansi = new(@"\x1b\[[0-9;?]*[A-Za-z]", System.Text.RegularExpressions.RegexOptions.Compiled);

    private void Out(string kind, string text, bool newLine = true, ConsoleColor? color = null) =>
        _ch.Send(new PsMsg { T = "out", Kind = kind, Text = Ansi.Replace(text, ""), NoNewLine = !newLine, Color = color?.ToString() });

    private ConsoleColor? Custom(ConsoleColor fg) => fg == _raw.ForegroundColor ? null : fg;

    public override void Write(string value) => Out("host", value, false);
    public override void Write(ConsoleColor foregroundColor, ConsoleColor backgroundColor, string value) => Out("host", value, false, Custom(foregroundColor));
    public override void WriteLine(string value) => Out("out", value);
    public override void WriteLine(ConsoleColor foregroundColor, ConsoleColor backgroundColor, string value) => Out("host", value, true, Custom(foregroundColor));
    public int Errors;

    public override void WriteErrorLine(string value)
    {
        Errors++;
        Out("err", value);
    }
    public override void WriteWarningLine(string message) => Out("warn", "ПРЕДУПРЕЖДЕНИЕ: " + message);
    public override void WriteVerboseLine(string message) => Out("verbose", "VERBOSE: " + message);
    public override void WriteDebugLine(string message) => Out("debug", "DEBUG: " + message);

    public override void WriteProgress(long sourceId, ProgressRecord record)
    {
        var text = record.RecordType == ProgressRecordType.Completed
            ? ""
            : record.Activity + (string.IsNullOrEmpty(record.StatusDescription) ? "" : ": " + record.StatusDescription) +
              (record.PercentComplete >= 0 ? $" — {record.PercentComplete}%" : "");
        _ch.Send(new PsMsg { T = "progress", Text = text });
    }

    public override string ReadLine()
    {
        var r = _ch.Ask(new PsMsg { Kind = "line", Caption = "Ввод", Fields = new() { new PsField { Name = "v", Label = "Значение" } } });
        return r is { Count: > 0 } ? r[0] : throw new PipelineStoppedException();
    }

    public override SecureString ReadLineAsSecureString()
    {
        var r = _ch.Ask(new PsMsg { Kind = "secure", Caption = "Ввод", Fields = new() { new PsField { Name = "v", Label = "Значение", Secret = true } } });
        return r is { Count: > 0 } ? Secure(r[0]) : throw new PipelineStoppedException();
    }

    public override Dictionary<string, PSObject> Prompt(string caption, string message, Collection<FieldDescription> descriptions)
    {
        var result = new Dictionary<string, PSObject>();
        var plain = new List<FieldDescription>();
        foreach (var d in descriptions)
        {
            // Параметр типа PSCredential — отдельным окном учётных данных.
            if (d.ParameterTypeFullName.Contains("PSCredential", StringComparison.OrdinalIgnoreCase))
                result[d.Name] = new PSObject(PromptForCredential(caption, message, "", ""));
            else plain.Add(d);
        }

        if (plain.Count == 0) return result;
        var fields = plain.Select(d => new PsField
        {
            Name = d.Name,
            Label = string.IsNullOrEmpty(d.Label) ? d.Name : d.Label.Replace("&", ""),
            Secret = d.ParameterTypeFullName.Contains("SecureString", StringComparison.OrdinalIgnoreCase),
            Value = d.DefaultValue?.ToString()
        }).ToList();
        var r = _ch.Ask(new PsMsg { Kind = "fields", Caption = caption, Message = message, Fields = fields });
        if (r is null) throw new PipelineStoppedException();
        for (var i = 0; i < plain.Count; i++)
        {
            var v = i < r.Count ? r[i] : "";
            result[plain[i].Name] = fields[i].Secret ? new PSObject(Secure(v)) : new PSObject(v);
        }

        return result;
    }

    public override int PromptForChoice(string caption, string message, Collection<ChoiceDescription> choices, int defaultChoice)
    {
        var r = _ch.Ask(new PsMsg
        {
            Kind = "choice", Caption = caption, Message = message, Default = defaultChoice,
            Choices = choices.Select(c => c.Label.Replace("&", "")).ToList()
        });
        return r is { Count: > 0 } && int.TryParse(r[0], out var i) ? i : throw new PipelineStoppedException();
    }

    public override PSCredential PromptForCredential(string caption, string message, string userName, string targetName) =>
        PromptForCredential(caption, message, userName, targetName, PSCredentialTypes.Default, PSCredentialUIOptions.Default);

    public override PSCredential PromptForCredential(string caption, string message, string userName, string targetName,
        PSCredentialTypes allowedCredentialTypes, PSCredentialUIOptions options)
    {
        var r = _ch.Ask(new PsMsg
        {
            Kind = "credential",
            Caption = string.IsNullOrEmpty(caption) ? "Учётные данные" : caption,
            Text = targetName, // LinuxOps по нему подставляет запомненный пароль (vcenter:<адрес>)
            Message = message,
            Fields = new()
            {
                new PsField { Name = "user", Label = "Пользователь", Value = userName },
                new PsField { Name = "password", Label = "Пароль", Secret = true }
            }
        });
        // «Отмена» — null, как у консольного хоста: Get-Credential вернёт пусто, скрипт решит сам.
        if (r is not { Count: 2 }) return null!;
        return new PSCredential(r[0], Secure(r[1]));
    }

    private static SecureString Secure(string s)
    {
        var ss = new SecureString();
        foreach (var c in s) ss.AppendChar(c);
        ss.MakeReadOnly();
        return ss;
    }
}

/// <summary>Окно консоли «виртуальное»: ширина — от вкладки, чтобы Format-Table укладывался.</summary>
internal sealed class LoRawUi : PSHostRawUserInterface
{
    private readonly LoHost _host;
    public LoRawUi(LoHost host) => _host = host;

    public override ConsoleColor ForegroundColor { get; set; } = ConsoleColor.Gray;
    public override ConsoleColor BackgroundColor { get; set; } = ConsoleColor.Black;
    public override Coordinates CursorPosition { get; set; }
    public override int CursorSize { get; set; } = 25;
    public override Coordinates WindowPosition { get; set; }
    public override Size BufferSize { get => new(_host.Width, 9999); set { } }
    public override Size WindowSize { get => new(_host.Width, 50); set { } }
    public override Size MaxWindowSize => new(_host.Width, 50);
    public override Size MaxPhysicalWindowSize => new(_host.Width, 50);
    public override bool KeyAvailable => false;
    public override string WindowTitle { get; set; } = "LinuxOps";
    public override KeyInfo ReadKey(ReadKeyOptions options) => throw new NotSupportedException("Чтение клавиш не поддерживается — используйте Read-Host.");
    public override void FlushInputBuffer() { }
    public override BufferCell[,] GetBufferContents(Rectangle rectangle) => throw new NotSupportedException();
    public override void SetBufferContents(Coordinates origin, BufferCell[,] contents) { }
    public override void SetBufferContents(Rectangle rectangle, BufferCell fill) { }
    public override void ScrollBufferContents(Rectangle source, Coordinates destination, Rectangle clip, BufferCell fill) { }
}
