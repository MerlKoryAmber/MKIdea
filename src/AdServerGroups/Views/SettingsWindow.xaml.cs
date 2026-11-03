using System.Windows;
using System.Windows.Controls;
using AdServerGroups.Models;
using AdServerGroups.Services;

namespace AdServerGroups.Views;

public partial class SettingsWindow : Window
{
    public AppSettings Settings { get; private set; }

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        Settings = Clone(settings);
        LoadToUi(Settings);
    }

    private void LoadToUi(AppSettings s)
    {
        DomainDnsBox.Text = s.DomainDns;
        LdapHostBox.Text = s.LdapHost;
        UsernameBox.Text = s.Username;
        DefaultContainerBox.Text = s.DefaultContainerDn;
        GroupsOuBox.Text = s.GroupsOuDn;
        GroupTemplateBox.Text = s.GroupNameTemplate;
        GroupDescriptionBox.Text = s.GroupDescriptionTemplate;
        SqlGroupTemplateBox.Text = s.SqlGroupNameTemplate;
        OsFilterBox.Text = s.OperatingSystemContains;
        IncludeNestedBox.IsChecked = s.IncludeNestedContainers;
        CreateGroupsBox.IsChecked = s.CreateMissingGroups;
        AddLocalBox.IsChecked = s.AddGroupToLocalAdministrators;
        MigrateBox.IsChecked = s.MigrateDirectMembersToGroup;
        ExpandBox.IsChecked = s.ExpandNestedGroupsOnMigrate;
        RemoveBox.IsChecked = s.RemoveNonTargetMembers;
        KeepBox.Text = s.AlwaysKeepMembers;
        UpdateFriendlyHints();
    }

    private void DefaultContainerBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateFriendlyHints();
    private void GroupsOuBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateFriendlyHints();

    private void UpdateFriendlyHints()
    {
        if (DefaultContainerFriendly is not null)
            DefaultContainerFriendly.Text = DnDisplay.ToFriendly(DefaultContainerBox.Text);
        if (GroupsOuFriendly is not null)
            GroupsOuFriendly.Text = DnDisplay.ToFriendly(GroupsOuBox.Text);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var template = GroupTemplateBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(template) ||
            template.IndexOf("{ComputerName}", StringComparison.OrdinalIgnoreCase) < 0)
        {
            ErrorText.Text = "Шаблон Local Admins должен содержать {ComputerName}.";
            return;
        }

        var sqlTemplate = SqlGroupTemplateBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(sqlTemplate) ||
            sqlTemplate.IndexOf("{ComputerName}", StringComparison.OrdinalIgnoreCase) < 0)
        {
            ErrorText.Text = "Шаблон SQL sysadmin должен содержать {ComputerName}.";
            return;
        }

        ErrorText.Text = string.Empty;

        Settings.DomainDns = DomainDnsBox.Text.Trim();
        Settings.LdapHost = LdapHostBox.Text.Trim();
        Settings.Username = UsernameBox.Text.Trim();
        Settings.DefaultContainerDn = DefaultContainerBox.Text.Trim();
        Settings.GroupsOuDn = GroupsOuBox.Text.Trim();
        Settings.GroupNameTemplate = template;
        Settings.GroupDescriptionTemplate = GroupDescriptionBox.Text.Trim();
        Settings.SqlGroupNameTemplate = sqlTemplate;
        Settings.OperatingSystemContains = OsFilterBox.Text.Trim();
        Settings.IncludeNestedContainers = IncludeNestedBox.IsChecked == true;
        Settings.CreateMissingGroups = CreateGroupsBox.IsChecked == true;
        Settings.AddGroupToLocalAdministrators = AddLocalBox.IsChecked == true;
        Settings.MigrateDirectMembersToGroup = MigrateBox.IsChecked == true;
        Settings.ExpandNestedGroupsOnMigrate = ExpandBox.IsChecked == true;
        Settings.RemoveNonTargetMembers = RemoveBox.IsChecked == true;
        Settings.AlwaysKeepMembers = KeepBox.Text.Trim();

        SettingsStore.Save(Settings);
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private static AppSettings Clone(AppSettings s) => new()
    {
        DomainDns = s.DomainDns,
        LdapHost = s.LdapHost,
        Username = s.Username,
        DefaultContainerDn = s.DefaultContainerDn,
        GroupsOuDn = s.GroupsOuDn,
        GroupNameTemplate = s.GroupNameTemplate,
        GroupDescriptionTemplate = s.GroupDescriptionTemplate,
        SqlGroupNameTemplate = s.SqlGroupNameTemplate,
        SqlGroupDescriptionTemplate = s.SqlGroupDescriptionTemplate,
        DomainNetBios = s.DomainNetBios,
        SqlMigrateDomainSysadmins = s.SqlMigrateDomainSysadmins,
        OperatingSystemContains = s.OperatingSystemContains,
        IncludeNestedContainers = s.IncludeNestedContainers,
        CreateMissingGroups = s.CreateMissingGroups,
        AddGroupToLocalAdministrators = s.AddGroupToLocalAdministrators,
        MigrateDirectMembersToGroup = s.MigrateDirectMembersToGroup,
        ExpandNestedGroupsOnMigrate = s.ExpandNestedGroupsOnMigrate,
        RemoveNonTargetMembers = s.RemoveNonTargetMembers,
        AlwaysKeepMembers = s.AlwaysKeepMembers,
        WindowWidth = s.WindowWidth,
        WindowHeight = s.WindowHeight,
        WindowLeft = s.WindowLeft,
        WindowTop = s.WindowTop,
        WindowState = s.WindowState,
        LogPanelHeight = s.LogPanelHeight
    };
}
