namespace AdServerGroups.Models;

public sealed class AppSettings
{
    // Домен/LDAP/OU/учётка — пустые в коде; берутся из AdServerGroups.defaults.json
    // или из зашифрованного AdServerGroups.settings рядом с exe.
    public string DomainDns { get; set; } = string.Empty;
    public string LdapHost { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string DefaultContainerDn { get; set; } = string.Empty;
    public string GroupsOuDn { get; set; } = string.Empty;

    /// <summary>
    /// Шаблон имени группы по умолчанию. Плейсхолдер: {ComputerName}.
    /// На вкладке «Группы» можно переопределить для текущей задачи.
    /// </summary>
    public string GroupNameTemplate { get; set; } = "GRP-LA-{ComputerName}";

    public string GroupDescriptionTemplate { get; set; } = "Local Administrators for {ComputerName}";

    /// <summary>Шаблон AD-группы для SQL sysadmin. Плейсхолдер: {ComputerName}.</summary>
    public string SqlGroupNameTemplate { get; set; } = "GRP-SQL-{ComputerName}";

    /// <summary>Описание AD-группы для SQL. Плейсхолдер: {ComputerName}.</summary>
    public string SqlGroupDescriptionTemplate { get; set; } = "SQL sysadmin for {ComputerName}";

    /// <summary>
    /// NetBIOS-имя домена для SQL CREATE LOGIN (HCI\group). Пусто = первый лейбл DomainDns.
    /// SQL обычно не принимает DNS-форму (hci.interros.ru\group).
    /// </summary>
    public string DomainNetBios { get; set; } = string.Empty;

    /// <summary>
    /// Перенести доменных Windows-пользователей/группы из роли SQL sysadmin
    /// в целевую AD-группу. В SQL ничего не удаляет. Объекты компьютеров (*$) пропускаются.
    /// </summary>
    public bool SqlMigrateDomainSysadmins { get; set; } = true;
    public string OperatingSystemContains { get; set; } = "Windows Server";
    public bool IncludeNestedContainers { get; set; } = true;
    public bool CreateMissingGroups { get; set; } = true;
    public bool AddGroupToLocalAdministrators { get; set; } = true;
    public bool MigrateDirectMembersToGroup { get; set; } = true;
    /// <summary>
    /// При переносе: группы из Administrators распаковывать до пользователей/вложенных,
    /// защищённые группы не трогать и не разворачивать.
    /// </summary>
    public bool ExpandNestedGroupsOnMigrate { get; set; } = true;
    public bool RemoveNonTargetMembers { get; set; } = false;
    /// <summary>Не трогать при migrate/remove. Exchange-группы — обязательно (Trusted Subsystem, Organization Management).</summary>
    public string AlwaysKeepMembers { get; set; } =
        "Administrator,Administrators,Domain Admins,Exchange Trusted Subsystem,Organization Management";

    // Геометрия главного окна (сохраняется при закрытии)
    public double WindowWidth { get; set; } = 1180;
    public double WindowHeight { get; set; } = 760;
    public double WindowLeft { get; set; } = double.NaN;
    public double WindowTop { get; set; } = double.NaN;
    public string WindowState { get; set; } = "Normal";

    /// <summary>Высота нижней панели лога (между рабочей областью и логом — GridSplitter).</summary>
    public double LogPanelHeight { get; set; } = 140;
}

public sealed class SessionCredentials
{
    /// <summary>Текущий Windows-токен (SSO), без логина/пароля в UI.</summary>
    public bool UseIntegratedWindowsAuth { get; set; } = true;

    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;

    public bool HasExplicitCredentials =>
        !UseIntegratedWindowsAuth
        && !string.IsNullOrWhiteSpace(Username)
        && !string.IsNullOrEmpty(Password);

    public void UseIntegrated()
    {
        UseIntegratedWindowsAuth = true;
        Username = string.Empty;
        Password = string.Empty;
    }

    public void UseExplicit(string username, string password)
    {
        UseIntegratedWindowsAuth = false;
        Username = username;
        Password = password;
    }
}
