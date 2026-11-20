using System.IO;
using System.Text.Json;

namespace LinuxOps.Services;

public sealed class BaseUser
{
    public string Login { get; set; } = "";
    public bool Sudo { get; set; }
    public string Keys { get; set; } = "";
    public string Password { get; set; } = "";
    public string Comment { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

public static class UserListStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static string FilePath { get; } = Path.Combine(
        Path.GetDirectoryName(DefaultsStore.DefaultsPath) ?? AppContext.BaseDirectory,
        "LinuxOps.users.json");

    public static List<BaseUser> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new List<BaseUser>();
            var dto = JsonSerializer.Deserialize<UserFile>(File.ReadAllText(FilePath), JsonOptions);
            var list = dto?.Users ?? new List<BaseUser>();
            foreach (var user in list)
                user.Password = DefaultsStore.Unprotect(user.Password);
            return list;
        }
        catch
        {
            return new List<BaseUser>();
        }
    }

    public static void Save(IEnumerable<BaseUser> users)
    {
        var copy = users.Select(u => new BaseUser
        {
            Login = u.Login.Trim(),
            Sudo = u.Sudo,
            Keys = (u.Keys ?? "").Replace("\r\n", "\n").Trim(),
            Password = DefaultsStore.Protect(u.Password),
            Comment = u.Comment ?? "",
            Enabled = u.Enabled
        }).ToList();
        var json = JsonSerializer.Serialize(new UserFile { Users = copy }, JsonOptions);
        File.WriteAllText(FilePath, json);
    }

    private sealed class UserFile
    {
        public List<BaseUser> Users { get; set; } = new();
    }
}
