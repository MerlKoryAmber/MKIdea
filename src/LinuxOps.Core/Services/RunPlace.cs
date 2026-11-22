namespace LinuxOps.Services;

/// <summary>
/// Где запущено ядро — для текстов: настольная версия — «этот ПК», веб — сервер LinuxOps Web.
/// Веб задаёт значения при запуске, до первого обращения к <see cref="BuiltinActions"/>.
/// </summary>
public static class RunPlace
{
    /// <summary>«через этот ПК».</summary>
    public static string Here { get; set; } = "этот ПК";
    /// <summary>«с этого ПК».</summary>
    public static string OfHere { get; set; } = "этого ПК";
    /// <summary>«на этом ПК».</summary>
    public static string AtHere { get; set; } = "этом ПК";
    /// <summary>Чья учётка видна в журналах прокси.</summary>
    public static string ProxyOwner { get; set; } = "вашей учётки";

    public static void Server()
    {
        Here = "сервер LinuxOps Web"; OfHere = "сервера LinuxOps Web"; AtHere = "сервере LinuxOps Web"; ProxyOwner = "сервера LinuxOps Web";
    }
}
