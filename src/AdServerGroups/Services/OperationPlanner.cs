using System.Diagnostics;
using AdServerGroups.Models;

namespace AdServerGroups.Services;

public sealed class OperationPlanner
{
    private readonly AppSettings _settings;
    private readonly AdDirectoryService _ad;
    private readonly LocalAdminService _local;

    public OperationPlanner(AppSettings settings, AdDirectoryService ad, LocalAdminService local)
    {
        _settings = settings;
        _ad = ad;
        _local = local;
    }

    public IReadOnlyList<GroupMappingItem> BuildGroupMappings(
        IEnumerable<AdComputerItem> computers,
        bool useSqlGroupTemplate = false)
    {
        var items = new List<GroupMappingItem>();
        foreach (var computer in computers.Where(c => c.IsSelected))
        {
            var groupName = useSqlGroupTemplate
                ? SettingsStore.ResolveSqlGroupName(_settings, computer.Name)
                : SettingsStore.ResolveGroupName(_settings, computer.Name);
            var exists = _ad.GroupExists(groupName);
            items.Add(new GroupMappingItem
            {
                ComputerName = computer.Name,
                GroupName = groupName,
                Status = exists ? "есть" : "создать"
            });
        }

        return items;
    }

    public IReadOnlyList<PlannedAction> BuildPlan(
        IEnumerable<AdComputerItem> computers,
        bool probeLocalAdmins,
        AppWorkState? history = null,
        bool useHistory = true)
    {
        var plan = new List<PlannedAction>();
        var fingerprint = StateStore.OptionsFingerprint(_settings);
        history ??= new AppWorkState();

        foreach (var computer in computers.Where(c => c.IsSelected))
        {
            var groupName = SettingsStore.ResolveGroupName(_settings, computer.Name);
            var record = useHistory ? StateStore.Find(history, computer.Name, groupName) : null;
            var exists = _ad.GroupExists(groupName);

            if (!exists)
            {
                if (_settings.CreateMissingGroups)
                {
                    if (useHistory && record?.GroupCreatedInAd == true)
                    {
                        plan.Add(new PlannedAction
                        {
                            Kind = PlanActionKind.CreateGroup,
                            ComputerName = computer.Name,
                            GroupName = groupName,
                            Detail = $"[SKIP] CREATE {groupName} — в истории уже создавали (в AD сейчас нет → CONFLICT)"
                        });
                    }
                    else
                    {
                        plan.Add(new PlannedAction
                        {
                            Kind = PlanActionKind.CreateGroup,
                            ComputerName = computer.Name,
                            GroupName = groupName
                        });
                    }
                }
            }
            else if (useHistory && record?.GroupCreatedInAd == true)
            {
                plan.Add(new PlannedAction
                {
                    Kind = PlanActionKind.SkipExistingGroup,
                    ComputerName = computer.Name,
                    GroupName = groupName,
                    Detail = $"[SKIP] {groupName} — есть в AD и в истории"
                });
            }
            else
            {
                plan.Add(new PlannedAction
                {
                    Kind = PlanActionKind.SkipExistingGroup,
                    ComputerName = computer.Name,
                    GroupName = groupName
                });
            }

            if (!probeLocalAdmins)
                continue;

            var remoteHost = computer.RemoteHost;
            IReadOnlyList<string> members;
            try
            {
                members = _local.GetAdministrators(remoteHost);
            }
            catch (Exception ex)
            {
                plan.Add(new PlannedAction
                {
                    Kind = PlanActionKind.AddGroupToLocalAdmins,
                    ComputerName = computer.Name,
                    RemoteHost = remoteHost,
                    GroupName = groupName,
                    Detail = $"[LOCAL] SKIP  {computer.Name} ({remoteHost}): {RemoteErrorHints.Annotate(ex)}"
                });
                continue;
            }

            var hashMatches = !useHistory || StateStore.AdminsHashMatches(record, members);
            var trustHistoryLocal = useHistory && record is not null && hashMatches;

            if (useHistory && record is not null && StateStore.HasAdminsSnapshot(record) && !hashMatches)
            {
                var (added, removed) = StateStore.DiffAdmins(record.AdminsSnapshot, members);
                plan.Add(new PlannedAction
                {
                    Kind = PlanActionKind.SkipExistingGroup,
                    ComputerName = computer.Name,
                    RemoteHost = remoteHost,
                    GroupName = groupName,
                    Detail = FormatAdminsConflict(computer.Name, added, removed)
                });
            }

            if (_settings.MigrateDirectMembersToGroup)
            {
                var migrated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var alreadyMigrated = trustHistoryLocal
                    ? new HashSet<string>(record!.MigratedPrincipals, StringComparer.OrdinalIgnoreCase)
                    : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var member in members)
                {
                    if (_local.IsProtectedMember(member))
                        continue;
                    if (IsSameGroup(member, groupName))
                        continue;

                    IReadOnlyList<MigrationLeaf> leaves;
                    try
                    {
                        leaves = _ad.ExpandForMigration(member, _local.IsProtectedMember);
                    }
                    catch (Exception ex)
                    {
                        plan.Add(new PlannedAction
                        {
                            Kind = PlanActionKind.AddMemberToGroup,
                            ComputerName = computer.Name,
                            RemoteHost = remoteHost,
                            GroupName = groupName,
                            Target = member,
                            Detail = $"[AD] SKIP unpack {member}: {RemoteErrorHints.Annotate(ex)}"
                        });
                        continue;
                    }

                    foreach (var leaf in leaves)
                    {
                        if (IsSameGroup(leaf.Member, groupName))
                            continue;
                        if (_local.IsProtectedMember(leaf.Member))
                            continue;
                        if (!migrated.Add(leaf.Member))
                            continue;

                        if (alreadyMigrated.Contains(leaf.Member))
                        {
                            plan.Add(new PlannedAction
                            {
                                Kind = PlanActionKind.AddMemberToGroup,
                                ComputerName = computer.Name,
                                RemoteHost = remoteHost,
                                GroupName = groupName,
                                Target = leaf.Member,
                                Detail = $"[SKIP] ADD {leaf.Member} → {groupName} — уже в истории"
                            });
                            continue;
                        }

                        var from = string.IsNullOrWhiteSpace(leaf.UnpackedFrom)
                            ? null
                            : $"  (из {leaf.UnpackedFrom})";

                        plan.Add(new PlannedAction
                        {
                            Kind = PlanActionKind.AddMemberToGroup,
                            ComputerName = computer.Name,
                            RemoteHost = remoteHost,
                            GroupName = groupName,
                            Target = leaf.Member,
                            Detail = $"[AD] ADD     {leaf.Member}  → {groupName}{from}"
                        });
                    }
                }
            }

            if (_settings.AddGroupToLocalAdministrators)
            {
                if (members.Any(m => IsSameGroup(m, groupName)))
                {
                    plan.Add(new PlannedAction
                    {
                        Kind = PlanActionKind.AddGroupToLocalAdmins,
                        ComputerName = computer.Name,
                        RemoteHost = remoteHost,
                        GroupName = groupName,
                        Detail = $"[SKIP] LOCAL ADD {groupName} — уже в Administrators"
                    });
                }
                else if (trustHistoryLocal && record?.GroupAddedToLocalAdmins == true)
                {
                    plan.Add(new PlannedAction
                    {
                        Kind = PlanActionKind.AddGroupToLocalAdmins,
                        ComputerName = computer.Name,
                        RemoteHost = remoteHost,
                        GroupName = groupName,
                        Detail = $"[SKIP] LOCAL ADD {groupName} — в истории уже добавляли (сейчас нет → CONFLICT)"
                    });
                }
                else
                {
                    plan.Add(new PlannedAction
                    {
                        Kind = PlanActionKind.AddGroupToLocalAdmins,
                        ComputerName = computer.Name,
                        RemoteHost = remoteHost,
                        GroupName = groupName
                    });
                }
            }

            if (_settings.RemoveNonTargetMembers)
            {
                // Член снова в Administrators → удаляем снова (история RemovedFromLocal не блокирует).
                foreach (var member in members)
                {
                    if (_local.IsProtectedMember(member))
                        continue;
                    if (IsSameGroup(member, groupName))
                        continue;

                    var wasRemovedBefore = trustHistoryLocal && record is not null &&
                        (record.RemovedFromLocal.Any(p =>
                             string.Equals(p, member, StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(StripDomain(p), StripDomain(member), StringComparison.OrdinalIgnoreCase)));

                    plan.Add(new PlannedAction
                    {
                        Kind = PlanActionKind.RemoveLocalMember,
                        ComputerName = computer.Name,
                        RemoteHost = remoteHost,
                        GroupName = groupName,
                        Target = member,
                        Detail = wasRemovedBefore
                            ? $"[LOCAL] REMOVE {member}  from {computer.Name}\\Administrators  (снова появился после прошлого apply)"
                            : string.Empty
                    });
                }
            }

            if (useHistory && record is not null &&
                !string.IsNullOrWhiteSpace(record.OptionsFingerprint) &&
                !string.Equals(record.OptionsFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
            {
                plan.Add(new PlannedAction
                {
                    Kind = PlanActionKind.SkipExistingGroup,
                    ComputerName = computer.Name,
                    GroupName = groupName,
                    Detail = $"[INFO] История для {computer.Name}: опции были [{record.OptionsFingerprint}], сейчас [{fingerprint}]"
                });
            }
        }

        return plan;
    }

    public IReadOnlyList<string> Apply(
        IEnumerable<PlannedAction> plan,
        AppWorkState state,
        string appliedBy)
    {
        var log = new List<string>();
        var fingerprint = StateStore.OptionsFingerprint(_settings);
        var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var snapshotTargets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var justCreatedGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var actions = plan.ToList();

        foreach (var action in actions)
        {
            if (action.Kind is PlanActionKind.AddGroupToLocalAdmins or PlanActionKind.RemoveLocalMember ||
                (!string.IsNullOrWhiteSpace(action.Detail) &&
                 action.Detail.StartsWith("[CONFLICT]", StringComparison.Ordinal)))
            {
                RememberSnapshotTarget(snapshotTargets, action);
            }
        }

        foreach (var action in actions)
        {
            try
            {
                switch (action.Kind)
                {
                    case PlanActionKind.CreateGroup:
                        if (IsSkipDetail(action.Detail))
                        {
                            log.Add(action.ToLogLine());
                            break;
                        }

                        _ad.EnsureGroup(
                            action.GroupName,
                            SettingsStore.ResolveGroupDescription(_settings, action.ComputerName));
                        justCreatedGroups.Add(action.GroupName);
                        WaitForAdGroupVisible(action.GroupName, TimeSpan.FromSeconds(12));
                        var created = StateStore.GetOrCreate(state, action.ComputerName, action.GroupName);
                        created.GroupCreatedInAd = true;
                        Touch(created, fingerprint, appliedBy);
                        touched.Add(Key(action.ComputerName, action.GroupName));
                        log.Add(action.ToLogLine() + "  OK");
                        break;

                    case PlanActionKind.SkipExistingGroup:
                        log.Add(action.ToLogLine());
                        if (!string.IsNullOrWhiteSpace(action.GroupName) &&
                            !action.Detail.StartsWith("[INFO]", StringComparison.Ordinal) &&
                            !action.Detail.StartsWith("[CONFLICT]", StringComparison.Ordinal))
                        {
                            var skipRec = StateStore.GetOrCreate(state, action.ComputerName, action.GroupName);
                            skipRec.GroupCreatedInAd = true;
                            Touch(skipRec, fingerprint, appliedBy);
                            touched.Add(Key(action.ComputerName, action.GroupName));
                        }
                        break;

                    case PlanActionKind.AddMemberToGroup:
                        if (IsSkipDetail(action.Detail))
                        {
                            log.Add(action.ToLogLine());
                            break;
                        }

                        _ad.AddMembersToGroup(action.GroupName, new[] { action.Target });
                        var mig = StateStore.GetOrCreate(state, action.ComputerName, action.GroupName);
                        if (!mig.MigratedPrincipals.Any(p =>
                                string.Equals(p, action.Target, StringComparison.OrdinalIgnoreCase)))
                        {
                            mig.MigratedPrincipals.Add(action.Target);
                        }

                        Touch(mig, fingerprint, appliedBy);
                        touched.Add(Key(action.ComputerName, action.GroupName));
                        log.Add(action.ToLogLine() + "  OK");
                        break;

                    case PlanActionKind.AddGroupToLocalAdmins:
                        if (IsSkipDetail(action.Detail) ||
                            (!string.IsNullOrWhiteSpace(action.Detail) &&
                             action.Detail.StartsWith("[LOCAL] SKIP", StringComparison.Ordinal)))
                        {
                            log.Add(action.ToLogLine());
                            if (action.Detail.Contains("уже в Administrators", StringComparison.OrdinalIgnoreCase))
                            {
                                var localOk = StateStore.GetOrCreate(state, action.ComputerName, action.GroupName);
                                localOk.GroupAddedToLocalAdmins = true;
                                Touch(localOk, fingerprint, appliedBy);
                                touched.Add(Key(action.ComputerName, action.GroupName));
                            }
                            break;
                        }

                        if (justCreatedGroups.Contains(action.GroupName))
                            WaitForAdGroupVisible(action.GroupName, TimeSpan.FromSeconds(8));

                        AddDomainGroupWithRetry(action.EffectiveRemoteHost, action.GroupName, retries: 3);
                        var localRec = StateStore.GetOrCreate(state, action.ComputerName, action.GroupName);
                        localRec.GroupAddedToLocalAdmins = true;
                        Touch(localRec, fingerprint, appliedBy);
                        touched.Add(Key(action.ComputerName, action.GroupName));
                        log.Add(action.ToLogLine() + "  OK");
                        break;

                    case PlanActionKind.RemoveLocalMember:
                        if (IsSkipDetail(action.Detail))
                        {
                            log.Add(action.ToLogLine());
                            break;
                        }

                        _local.RemoveMember(action.EffectiveRemoteHost, action.Target);
                        var rem = StateStore.GetOrCreate(state, action.ComputerName, action.GroupName);
                        if (!rem.RemovedFromLocal.Any(p =>
                                string.Equals(p, action.Target, StringComparison.OrdinalIgnoreCase)))
                        {
                            rem.RemovedFromLocal.Add(action.Target);
                        }

                        Touch(rem, fingerprint, appliedBy);
                        touched.Add(Key(action.ComputerName, action.GroupName));
                        log.Add(action.ToLogLine() + "  OK");
                        break;
                }
            }
            catch (Exception ex)
            {
                log.Add($"{action.ToLogLine()}  FAIL: {RemoteErrorHints.Annotate(ex)}");
            }
        }

        foreach (var (computer, groupName) in snapshotTargets)
        {
            try
            {
                var host = actions
                    .Where(a => string.Equals(a.ComputerName, computer, StringComparison.OrdinalIgnoreCase))
                    .Select(a => a.EffectiveRemoteHost)
                    .FirstOrDefault(h => !string.IsNullOrWhiteSpace(h))
                    ?? computer;
                var members = _local.GetAdministrators(host);
                var rec = StateStore.GetOrCreate(state, computer, groupName);
                StateStore.SetAdminsSnapshot(rec, members);
                touched.Add(Key(computer, groupName));
            }
            catch (Exception ex)
            {
                log.Add($"[LOCAL] SNAPSHOT {computer}: {RemoteErrorHints.Annotate(ex)}");
            }
        }

        if (touched.Count > 0)
            StateStore.Save(state);

        return log;
    }

    public static string FormatAdminsConflict(
        string computerName,
        IReadOnlyList<string> added,
        IReadOnlyList<string> removed)
    {
        var parts = new List<string>();
        if (added.Count > 0)
            parts.Add("+" + string.Join(", +", added));
        if (removed.Count > 0)
            parts.Add("-" + string.Join(", -", removed));
        var body = parts.Count == 0 ? "(состав другой, имена совпали после нормализации)" : string.Join("  ", parts);
        return $"[CONFLICT] {computerName}: Administrators изменились с прошлого apply: {body}";
    }

    private static void RememberSnapshotTarget(Dictionary<string, string> map, PlannedAction action)
    {
        if (string.IsNullOrWhiteSpace(action.ComputerName) || string.IsNullOrWhiteSpace(action.GroupName))
            return;
        map[action.ComputerName] = action.GroupName;
    }

    private static void Touch(ServerWorkRecord record, string fingerprint, string appliedBy)
    {
        record.OptionsFingerprint = fingerprint;
        record.AppliedUtc = DateTimeOffset.UtcNow;
        record.AppliedBy = appliedBy;
    }

    private static string Key(string computer, string group) => computer + "|" + group;

    private static bool IsSkipDetail(string? detail) =>
        !string.IsNullOrWhiteSpace(detail) &&
        (detail.StartsWith("[SKIP]", StringComparison.Ordinal) ||
         detail.StartsWith("[AD] SKIP", StringComparison.Ordinal) ||
         detail.StartsWith("[INFO]", StringComparison.Ordinal) ||
         detail.StartsWith("[CONFLICT]", StringComparison.Ordinal));

    private static bool IsSameGroup(string memberName, string groupName)
    {
        var shortName = StripDomain(memberName);
        return string.Equals(shortName, groupName, StringComparison.OrdinalIgnoreCase);
    }

    private static string StripDomain(string name)
    {
        var s = name.Trim();
        var slash = s.LastIndexOf('\\');
        if (slash >= 0 && slash < s.Length - 1)
            return s[(slash + 1)..];
        return s;
    }

    /// <summary>
    /// После CREATE группа может ещё не резолвиться на WinNT/SAM целевого сервера —
    /// ждём появления в AD + короткая пауза на репликацию.
    /// </summary>
    private void WaitForAdGroupVisible(string groupName, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            try
            {
                if (_ad.GroupExists(groupName))
                {
                    Thread.Sleep(750);
                    return;
                }
            }
            catch
            {
                // повторим
            }

            Thread.Sleep(400);
        }
    }

    private void AddDomainGroupWithRetry(string remoteHost, string groupName, int retries)
    {
        Exception? last = null;
        for (var attempt = 1; attempt <= retries; attempt++)
        {
            try
            {
                _local.AddDomainGroup(remoteHost, groupName);
                return;
            }
            catch (Exception ex)
            {
                last = ex;
                if (attempt >= retries)
                    break;
                Thread.Sleep(2000 * attempt);
                try { WaitForAdGroupVisible(groupName, TimeSpan.FromSeconds(5)); }
                catch { /* ignore */ }
            }
        }

        throw last ?? new InvalidOperationException($"Не удалось добавить {groupName} в Administrators на {remoteHost}.");
    }
}
