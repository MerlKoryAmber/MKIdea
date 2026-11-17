using System.Diagnostics;
using System.Windows.Controls;

namespace LinuxOps;

/// <summary>
/// Только отладочная сборка: учётка для проверки RDP на лабе, которую владелец сохранил сам
/// (Get-Credential | Export-Clixml, зашифровано DPAPI под его учёткой Windows). Путь — переменная LINUXOPS_RDP_TEST_CRED.
/// В Release этого кода нет: пароль RDP всегда вводится руками.
/// </summary>
internal static class TestCredential
{
    [Conditional("DEBUG")]
    public static void Apply(TextBox user, PasswordBox password)
    {
#if DEBUG
        var path = Environment.GetEnvironmentVariable("LINUXOPS_RDP_TEST_CRED");
        if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path)) return;
        try
        {
            var doc = System.Xml.Linq.XDocument.Load(path);
            string? Value(string tag, string name) => doc.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == tag && (string?)e.Attribute("N") == name)?.Value;
            var login = Value("S", "UserName");
            var hex = Value("SS", "Password");
            if (string.IsNullOrEmpty(login) || string.IsNullOrEmpty(hex)) return;
            var blob = Convert.FromHexString(hex);
            var plain = System.Security.Cryptography.ProtectedData.Unprotect(blob, null,
                System.Security.Cryptography.DataProtectionScope.CurrentUser);
            user.Text = login;
            password.Password = System.Text.Encoding.Unicode.GetString(plain);
            Array.Clear(plain);
        }
        catch
        {
            // файл не тот или чужой учётки — окно останется пустым
        }
#endif
    }
}
