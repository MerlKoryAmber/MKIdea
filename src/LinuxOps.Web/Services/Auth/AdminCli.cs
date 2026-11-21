namespace LinuxOps.Web.Services.Auth;

/// <summary>
/// Управление локальным админом из консоли сервера (меню linuxops-web → «Local admin»):
///   LinuxOps.Web admin status | password [--generate] | totp-reset | enable | disable
/// Пароль — со stdin (не в argv). Запускать от пользователя службы (runuser -u linuxops-web), чтобы файлы базы остались его.
/// Каждое действие — в аудит (база, audit.log, syslog).
/// </summary>
public static class AdminCli
{
    public static int Run(string[] args)
    {
        var store = new WebStore(new SecretBox());
        var audit = new AuditLog(store);
        var la = store.LoadLocalAdmin();
        var who = "cli (" + Environment.UserName + ")";
        var cmd = args.Length > 0 ? args[0] : "status";
        switch (cmd)
        {
            case "status":
                Console.WriteLine($"login:    {la.Login}");
                Console.WriteLine($"enabled:  {(la.Enabled ? "yes" : "no")}");
                Console.WriteLine($"password: {(la.HasPassword ? "set" : "not set")}");
                Console.WriteLine($"totp:     {(la.NoTotp ? "OFF (lab mode)" : la.TotpConfirmed ? "bound" : "not bound (set up at next login)")}");
                var s = store.LoadAuth();
                Console.WriteLine($"ad login: {(s.AdEnabled ? $"on ({s.AdServers}:{s.AdPort}, {s.AdSecurity})" : "off")}");
                return 0;

            case "password":
            {
                string pw;
                var generated = args.Contains("--generate");
                if (generated) pw = PasswordHash.Generate();
                else
                {
                    pw = Console.ReadLine() ?? "";
                    if (pw.Length < 12) { Console.Error.WriteLine("ERROR: password must be at least 12 characters"); return 2; }
                }
                la.PasswordHash = PasswordHash.Hash(pw);
                la.Enabled = true;
                la.NoTotp = false;
                store.SaveLocalAdmin(la);
                audit.Write(who, "локальный админ: пароль задан", "вход локального админа включён");
                if (generated) Console.WriteLine(pw);
                else Console.WriteLine("password set, local admin enabled");
                return 0;
            }

            case "totp-reset":
                la.TotpSecretEnc = "";
                la.TotpConfirmed = false;
                la.TotpLastStep = 0;
                store.SaveLocalAdmin(la);
                audit.Write(who, "локальный админ: TOTP сброшен", "новая привязка при следующем входе");
                Console.WriteLine("TOTP reset: a new one is bound at the next login");
                return 0;

            case "enable":
                if (!la.HasPassword) { Console.Error.WriteLine("ERROR: set a password first"); return 2; }
                la.Enabled = true;
                store.SaveLocalAdmin(la);
                audit.Write(who, "локальный админ: вход включён");
                Console.WriteLine("local admin login enabled");
                return 0;

            case "lab":
            {
                var mode = args.Length > 1 ? args[1] : "";
                if (mode is not ("on" or "off")) { Console.Error.WriteLine("usage: admin lab on|off"); return 1; }
                if (mode == "on")
                {
                    la.PasswordHash = PasswordHash.Hash("admin");
                    la.NoTotp = true;
                    la.Enabled = true;
                    store.SaveLocalAdmin(la);
                    audit.Write(who, "локальный админ: лабораторный режим включён", "admin / admin, без TOTP");
                    Console.WriteLine("LAB MODE ON: admin / admin, no TOTP. Never on production");
                }
                else
                {
                    la.NoTotp = false;
                    la.Enabled = false;
                    store.SaveLocalAdmin(la);
                    audit.Write(who, "локальный админ: лабораторный режим выключен", "вход локального админа выключен до нового пароля");
                    Console.WriteLine("lab mode off, local admin login disabled (set a new password to enable)");
                }
                return 0;
            }

            case "disable":
                la.Enabled = false;
                store.SaveLocalAdmin(la);
                audit.Write(who, "локальный админ: вход выключен");
                Console.WriteLine("local admin login disabled (open sessions end at the next request)");
                return 0;

            default:
                Console.Error.WriteLine("usage: LinuxOps.Web admin status | password [--generate] | totp-reset | enable | disable | lab on|off");
                return 1;
        }
    }
}
