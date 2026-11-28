using LinuxOps.Models;
using LinuxOps.Services;
using LinuxOps.Web.Services.Auth;

namespace LinuxOps.Web.Services;

/// <summary>Свои Bash-действия каталога: создание, правка, дублирование, удаление (только «Админ» = Can(Vcenter)).</summary>
public sealed partial class WebSession
{
    /// <summary>Создавать / менять / удалять свои действия — та же проверка, что для vCenter (встроенная «Админ»).</summary>
    public bool CanEditActions => Can(Perm.Vcenter);

    public string EditActionsHint => CanEditActions
        ? "Новое своё действие (Bash по SSH)"
        : "Нужна встроенная роль «Админ» (право vCenter)";

    /// <summary>Сохранить своё действие. Всегда Destructive=true, ReadOnly=false. prevScriptLen — при правке (аудит было/стало).</summary>
    public bool SaveCustomAction(ActionDefinition def, int? prevScriptLen = null)
    {
        if (!Allowed(Perm.Vcenter, "действие: сохранить")) return false;
        if (def.BuiltIn)
        {
            Warn("Встроенные действия нельзя перезаписывать.");
            return false;
        }

        HardenCustom(def);
        var creating = prevScriptLen is null;
        if (creating)
        {
            if (IsBuiltinId(def.Id))
            {
                Warn($"Id «{def.Id}» занят встроенным действием.");
                return false;
            }
            def.Id = ActionCatalog.UniqueId(def.Id);
        }
        else if (IsBuiltinId(def.Id))
        {
            Warn("Встроенные действия нельзя перезаписывать.");
            return false;
        }

        try
        {
            ActionCatalog.SaveCustom(def);
        }
        catch (Exception ex)
        {
            Warn(ex.Message);
            return false;
        }

        var scriptLen = (def.Script ?? "").Length;
        if (creating)
            _audit.Write(Who, "действие: создано", $"{def.Name} ({def.Id}), скрипт {scriptLen} симв.");
        else
            _audit.Write(Who, "действие: изменено", $"{def.Name} ({def.Id}), скрипт {prevScriptLen} → {scriptLen} симв.");

        LoadCatalog();
        Notify();
        return true;
    }

    public bool DeleteCustomAction(ActionDefinition def)
    {
        if (!Allowed(Perm.Vcenter, "действие: удалить")) return false;
        if (def.BuiltIn)
        {
            Warn("Встроенные нельзя удалить. Склонируйте и правьте копию.");
            return false;
        }

        var scriptLen = (def.Script ?? "").Length;
        ActionCatalog.DeleteCustom(def.Id);
        _audit.Write(Who, "действие: удалено", $"{def.Name} ({def.Id}), скрипт {scriptLen} симв.");
        LoadCatalog();
        Notify();
        return true;
    }

    /// <summary>Свои действия из JSON всегда опасные и не ReadOnly — даже если файл положили руками с Destructive:false.</summary>
    private static void HardenCustom(ActionDefinition def)
    {
        def.BuiltIn = false;
        def.Kind = "bash";
        def.Destructive = true;
        def.ReadOnly = false;
    }

    private static bool IsBuiltinId(string id) =>
        BuiltinActions.All.Any(a => a.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
        || VsphereActions.All.Any(a => a.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
}
