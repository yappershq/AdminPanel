using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using AdminPanel.Configuration;
using AdminPanel.Database;
using AdminPanel.Shared;
using Microsoft.Extensions.Logging;
using Sharp.Modules.AdminCommands.Shared;
using Sharp.Modules.AdminManager.Shared;
using Sharp.Modules.MenuManager.Shared;
using Sharp.Shared;
using Sharp.Shared.Enums;
using Sharp.Shared.Managers;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;
using Sharp.Shared.Units;

namespace AdminPanel.Modules;

/// <summary>
/// Registers the !admin command and builds the full menu flow:
///   Player picker → Action menu → [Duration →] Reason → Execute
/// All admin permission checks use the AdminManager IAdmin.HasPermission pattern.
/// </summary>
internal sealed class AdminCommandModule
{
    // Canonical perm form is module:flag (separator ':'; a leading '@' means a ROLE ref, not a perm,
    // so '@adminpanel/open' never matched a root '*'). Action verbs reuse AdminCommands' admin:<verb>.
    private const string ModuleId        = "AdminPanel";
    private const string PermOpen        = "adminpanel:open";
    private const string PermBan         = "admin:ban";
    private const string PermKick        = "admin:kick";
    private const string PermMute        = "admin:mute";
    private const string PermGag         = "admin:gag";
    private const string PermSilence     = "admin:silence";
    private const string PermSlay        = "admin:slay";
    private const string PermSlap        = "admin:slap";
    private const string PermMap         = "admin:map";

    // Additional native built-in admin operations (mirror the AdminCommands verb set).
    private const string PermGod         = "admin:god";
    private const string PermHp          = "admin:hp";
    private const string PermRespawn     = "admin:respawn";
    private const string PermNoclip      = "admin:noclip";
    private const string PermFreeze      = "admin:freeze";
    private const string PermSpeed       = "admin:speed";
    private const string PermGravity     = "admin:gravity";
    private const string PermTeam        = "admin:team";
    private const string PermRename      = "admin:rename";
    private const string PermMoney       = "admin:money";
    private const string PermBring       = "admin:bring";
    private const string PermGoto        = "admin:goto";
    private const string PermStrip       = "admin:strip";
    private const string PermGive        = "admin:give";

    // ── Category grouping ──
    // Built-in action categories and the order their rows render in. Any category that
    // appears in the merged data but is missing here sorts alphabetically after these.
    private const string CatPunish   = "Punish";
    private const string CatPlayer   = "Player";
    private const string CatTeleport = "Teleport";
    private const string CatServer   = "Server";
    private const string CatFun      = "Fun";

    /// <summary>Default bucket for externally-registered actions with no Category set.</summary>
    private const string DefaultExternalCategory = "Plugins";

    private static readonly string[] PlayerCategoryOrder =
        [CatPunish, CatPlayer, CatTeleport, CatServer, CatFun, DefaultExternalCategory];

    private static readonly string[] GlobalCategoryOrder =
        [CatServer, CatFun, DefaultExternalCategory];

    private readonly InterfaceBridge              _bridge;
    private readonly AdminPanelConfig             _config;
    private readonly ReasonSyncModule             _reasons;
    private readonly AdminActionRegistry          _actions;
    private readonly AdminInputModule             _input;
    private readonly ILogger<AdminCommandModule>  _logger;

    public AdminCommandModule(
        InterfaceBridge bridge,
        AdminPanelConfig config,
        ReasonSyncModule reasons,
        AdminActionRegistry actions,
        AdminInputModule input,
        ILogger<AdminCommandModule> logger)
    {
        _bridge  = bridge;
        _config  = config;
        _reasons = reasons;
        _actions = actions;
        _input   = input;
        _logger  = logger;
    }

    public void Start()
    {
        if (_bridge.MenuManager is null)
        {
            _logger.LogWarning("[AdminPanel] MenuManager unavailable — !{Cmd} disabled", _config.Command);
            return;
        }

        if (_bridge.AdminManager is not { } am)
        {
            _logger.LogError("[AdminPanel] AdminManager unavailable — !{Cmd} NOT registered (requires AdminManager for permission gating).", _config.Command);
            return;
        }

        am.MountAdminManifest(ModuleId, () => new AdminTableManifest(
            new Dictionary<string, HashSet<string>>
            {
                ["adminpanel"] =
                [
                    PermOpen, PermBan, PermKick, PermMute, PermGag, PermSilence,
                    PermSlay, PermSlap, PermMap,
                    PermGod, PermHp, PermRespawn, PermNoclip, PermFreeze, PermSpeed,
                    PermGravity, PermTeam, PermRename, PermMoney, PermBring, PermGoto,
                    PermStrip, PermGive,
                ]
            },
            [],
            []));

        am.GetCommandRegistry(ModuleId)
          .RegisterAdminCommand(_config.Command, OnAdminCommand, ImmutableArray.Create(PermOpen));

        _logger.LogInformation("[AdminPanel] !{Cmd} registered (perm {Perm})", _config.Command, PermOpen);
    }

    public void Stop()
    {
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Command entry
    // ──────────────────────────────────────────────────────────────────────────

    private void OnAdminCommand(IGameClient? invoker, StringCommand _cmd)
    {
        if (invoker is null || invoker.IsFakeClient)
            return;

        ShowRootMenu(invoker);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 0. Root menu (player management + registered global actions)
    // ──────────────────────────────────────────────────────────────────────────

    private void ShowRootMenu(IGameClient admin)
    {
        if (_bridge.MenuManager is not { } mm) return;

        var builder = Menu.Create().Title("Admin Panel");

        // Built-in: player management flow stays as the first top-level entry.
        var capturedAdmin = admin;
        builder.Item("Manage Players", ctrl => ctrl.Next(_ => BuildPlayerPicker(capturedAdmin)));

        // Registered global actions, grouped into category submenus (perm-gated).
        var adminObj = _bridge.AdminManager?.GetAdmin(admin.SteamId);
        bool HasPerm(string? perm) =>
            perm is null || _bridge.AdminManager is null || (adminObj?.HasPermission(perm) ?? false);

        var buckets = new Dictionary<string, List<MenuEntry>>(StringComparer.Ordinal);
        foreach (var action in _actions.GetGlobalActions())
        {
            bool allowed   = HasPerm(action.Permission);
            var  category  = action.Category is { Length: > 0 } c ? c : DefaultExternalCategory;
            var  captured  = action;

            AddEntry(buckets, category, action.SortOrder, captured.Label, allowed, ctrl =>
            {
                ctrl.Exit();
                InvokeGlobalAction(capturedAdmin, captured);
            });
        }

        // One row per non-empty category (hidden when the admin lacks perms for every item).
        foreach (var category in OrderCategories(buckets.Keys, GlobalCategoryOrder))
        {
            var entries = buckets[category];
            if (!entries.Exists(e => e.Allowed))
                continue;

            var capturedCategory = category;
            var capturedEntries  = entries;
            builder.Item(category, ctrl =>
                ctrl.Next(_ => BuildGlobalCategoryMenu(capturedCategory, capturedEntries)));
        }

        builder.ExitItem("Exit");
        mm.DisplayMenu(admin, builder.Build());
    }

    /// <summary>Leaf menu listing one category's global actions (sorted, perm-gated).</summary>
    private Menu BuildGlobalCategoryMenu(string category, List<MenuEntry> entries)
    {
        var builder = Menu.Create().Title(category);

        foreach (var entry in entries.OrderBy(e => e.SortOrder)
                                     .ThenBy(e => e.Label, StringComparer.OrdinalIgnoreCase))
        {
            AddActionItem(builder, entry.Label, string.Empty, entry.Allowed, entry.Action);
        }

        builder.BackItem("« Back");
        builder.ExitItem("Exit");
        return builder.Build();
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 1. Player picker
    // ──────────────────────────────────────────────────────────────────────────

    private Menu BuildPlayerPicker(IGameClient admin)
    {
        var builder = Menu.Create().Title("Admin Panel — Select Player");

        var players = _bridge.ClientManager.GetGameClients();
        bool any = false;

        foreach (var client in players)
        {
            if (client.IsFakeClient || client.IsHltv)
                continue;

            var captured = client;
            var name     = captured.Name ?? captured.SteamId.ToString();
            builder.Item(name, ctrl =>
            {
                ctrl.Next(c => BuildActionMenu(c, captured, name));
            });
            any = true;
        }

        if (!any)
            builder.Item("(no players online)", _ => { });

        builder.BackItem("« Back");
        builder.ExitItem("Exit");
        return builder.Build();
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 2. Action menu
    // ──────────────────────────────────────────────────────────────────────────

    private Menu BuildActionMenu(IGameClient admin, IGameClient target, string targetName)
    {
        var adminObj = _bridge.AdminManager?.GetAdmin(admin.SteamId);

        bool HasPerm(string perm) =>
            _bridge.AdminManager is null || (adminObj?.HasPermission(perm) ?? false);

        // Build the per-category buckets, merging built-ins with externally-registered actions.
        var buckets = new Dictionary<string, List<MenuEntry>>(StringComparer.Ordinal);

        void Add(string category, string label, string perm, int sortOrder, Action<IMenuController> action)
            => AddEntry(buckets, category, sortOrder, label, HasPerm(perm), action);

        // ── Punish ──
        Add(CatPunish, "Ban",     PermBan,     0, ctrl =>
            ctrl.Next(c => BuildDurationMenu(c, target, targetName, "ban", "Ban")));
        Add(CatPunish, "Kick",    PermKick,    1, ctrl =>
            ctrl.Next(c => BuildReasonMenu(c, target, targetName, "kick", "Kick", null)));
        Add(CatPunish, "Mute",    PermMute,    2, ctrl =>
            ctrl.Next(c => BuildDurationMenu(c, target, targetName, "mute", "Mute")));
        Add(CatPunish, "Gag",     PermGag,     3, ctrl =>
            ctrl.Next(c => BuildDurationMenu(c, target, targetName, "gag", "Gag")));
        Add(CatPunish, "Silence", PermSilence, 4, ctrl =>
            ctrl.Next(c => BuildDurationMenu(c, target, targetName, "silence", "Silence")));

        // ── Player ──
        Add(CatPlayer, "Slay",          PermSlay,    0, ctrl => { ctrl.Exit(); ExecuteSlay(admin, target, targetName); });
        Add(CatPlayer, "Slap",          PermSlap,    1, ctrl => { ctrl.Exit(); ExecuteSlap(admin, target, targetName); });
        Add(CatPlayer, "God Mode",      PermGod,     2, ctrl => { ctrl.Exit(); ExecuteGod(admin, target, targetName); });
        Add(CatPlayer, "Set HP",        PermHp,      3, ctrl => { ctrl.Exit(); RequestHealth(admin, target, targetName); });
        Add(CatPlayer, "Respawn",       PermRespawn, 4, ctrl => { ctrl.Exit(); ExecuteRespawn(admin, target, targetName); });
        Add(CatPlayer, "Noclip",        PermNoclip,  5, ctrl => { ctrl.Exit(); ExecuteNoclip(admin, target, targetName); });
        Add(CatPlayer, "Freeze",        PermFreeze,  6, ctrl => { ctrl.Exit(); ExecuteFreeze(admin, target, targetName, true); });
        Add(CatPlayer, "Unfreeze",      PermFreeze,  7, ctrl => { ctrl.Exit(); ExecuteFreeze(admin, target, targetName, false); });
        Add(CatPlayer, "Speed",         PermSpeed,   8, ctrl => { ctrl.Exit(); RequestSpeed(admin, target, targetName); });
        Add(CatPlayer, "Gravity",       PermGravity, 9, ctrl => { ctrl.Exit(); RequestGravity(admin, target, targetName); });
        Add(CatPlayer, "Team",          PermTeam,   10, ctrl => ctrl.Next(c => BuildTeamMenu(c, target, targetName)));
        Add(CatPlayer, "Rename",        PermRename, 11, ctrl => { ctrl.Exit(); RequestRename(admin, target, targetName); });
        Add(CatPlayer, "Set Money",     PermMoney,  12, ctrl => { ctrl.Exit(); RequestMoney(admin, target, targetName); });
        Add(CatPlayer, "Strip Weapons", PermStrip,  13, ctrl => { ctrl.Exit(); ExecuteStrip(admin, target, targetName); });
        Add(CatPlayer, "Give Item",     PermGive,   14, ctrl => { ctrl.Exit(); RequestGive(admin, target, targetName); });

        // ── Teleport ──
        Add(CatTeleport, "Bring", PermBring, 0, ctrl => { ctrl.Exit(); ExecuteBring(admin, target, targetName); });
        Add(CatTeleport, "Goto",  PermGoto,  1, ctrl => { ctrl.Exit(); ExecuteGoto(admin, target, targetName); });

        // ── Server ──
        Add(CatServer, "Map Change", PermMap, 0, ctrl => ctrl.Next(c => BuildMapMenu(c)));

        // Merge externally-registered player-actions (perm-gated, category-bucketed).
        // Null permission → visible to anyone who can open the panel.
        var adminSlot = (int) admin.Slot.AsPrimitive();
        foreach (var action in _actions.GetPlayerActions(adminSlot))
        {
            bool allowed  = action.Permission is null || HasPerm(action.Permission);
            var  label    = action.ResolveLabel(adminSlot);
            var  category = action.Category is { Length: > 0 } c ? c : DefaultExternalCategory;

            var capturedAction = action;
            var capturedAdmin  = admin;
            var capturedTarget = target;
            AddEntry(buckets, category, action.SortOrder, label, allowed, ctrl =>
            {
                ctrl.Exit();
                InvokePlayerAction(capturedAdmin, capturedTarget, capturedAction);
            });
        }

        // Top-level category list — one row per non-empty (admin-permitted) category.
        var builder = Menu.Create().Title($"Actions — {targetName}");

        foreach (var category in OrderCategories(buckets.Keys, PlayerCategoryOrder))
        {
            var entries = buckets[category];
            if (!entries.Exists(e => e.Allowed))
                continue;

            var capturedCategory = category;
            var capturedEntries  = entries;
            var capturedName     = targetName;
            builder.Item(category, ctrl =>
                ctrl.Next(_ => BuildCategoryActionMenu(capturedCategory, capturedName, capturedEntries)));
        }

        builder.BackItem("« Back");
        builder.ExitItem("Exit");

        return builder.Build();
    }

    /// <summary>Leaf menu listing one category's player actions (sorted, perm-gated).</summary>
    private Menu BuildCategoryActionMenu(string category, string targetName, List<MenuEntry> entries)
    {
        var builder = Menu.Create().Title($"{category} — {targetName}");

        foreach (var entry in entries.OrderBy(e => e.SortOrder)
                                     .ThenBy(e => e.Label, StringComparer.OrdinalIgnoreCase))
        {
            AddActionItem(builder, entry.Label, string.Empty, entry.Allowed, entry.Action);
        }

        builder.BackItem("« Back");
        builder.ExitItem("Exit");
        return builder.Build();
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Category grouping helpers
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>A single action entry inside a category bucket.</summary>
    private sealed record MenuEntry(int SortOrder, string Label, bool Allowed, Action<IMenuController> Action);

    /// <summary>Append an entry to its category bucket, creating the bucket on first use.</summary>
    private static void AddEntry(
        Dictionary<string, List<MenuEntry>> buckets,
        string category,
        int sortOrder,
        string label,
        bool allowed,
        Action<IMenuController> action)
    {
        if (!buckets.TryGetValue(category, out var list))
            buckets[category] = list = new List<MenuEntry>();

        list.Add(new MenuEntry(sortOrder, label, allowed, action));
    }

    /// <summary>
    /// Order categories: known ones first in the supplied order, then any extras alphabetically.
    /// </summary>
    private static IEnumerable<string> OrderCategories(IEnumerable<string> present, string[] order)
    {
        var set = new HashSet<string>(present, StringComparer.Ordinal);

        foreach (var c in order)
            if (set.Remove(c))
                yield return c;

        foreach (var c in set.OrderBy(c => c, StringComparer.OrdinalIgnoreCase))
            yield return c;
    }

    private static void AddActionItem(
        Menu.Builder builder,
        string label,
        string _perm,
        bool allowed,
        Action<IMenuController> action)
    {
        if (allowed)
            builder.Item(label, action);
        else
            builder.DisabledItem($"{label} (no permission)");
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 3. Duration menu
    // ──────────────────────────────────────────────────────────────────────────

    private Menu BuildDurationMenu(
        IGameClient admin,
        IGameClient target,
        string targetName,
        string actionType,
        string actionLabel)
    {
        TimeSpan? dur1h   = TimeSpan.FromHours(1);
        TimeSpan? dur1d   = TimeSpan.FromDays(1);
        TimeSpan? dur7d   = TimeSpan.FromDays(7);
        TimeSpan? dur30d  = TimeSpan.FromDays(30);
        TimeSpan? durPerm = null;

        return Menu.Create()
            .Title($"{actionLabel} — {targetName} — Duration")
            .Item("1 Hour",    ctrl => ctrl.Next(c => BuildReasonMenu(c, target, targetName, actionType, actionLabel, dur1h)))
            .Item("1 Day",     ctrl => ctrl.Next(c => BuildReasonMenu(c, target, targetName, actionType, actionLabel, dur1d)))
            .Item("7 Days",    ctrl => ctrl.Next(c => BuildReasonMenu(c, target, targetName, actionType, actionLabel, dur7d)))
            .Item("30 Days",   ctrl => ctrl.Next(c => BuildReasonMenu(c, target, targetName, actionType, actionLabel, dur30d)))
            .Item("Permanent", ctrl => ctrl.Next(c => BuildReasonMenu(c, target, targetName, actionType, actionLabel, durPerm)))
            .Item("Custom…",   ctrl =>
            {
                // Exit the menu and capture a free-text minute count via chat. 0 = permanent.
                ctrl.Exit();
                RequestCustomDuration(admin, target, targetName, actionType, actionLabel);
            })
            .BackItem("« Back")
            .ExitItem("Exit")
            .Build();
    }

    /// <summary>
    /// PROOF (input primitive): prompts the admin for a duration in minutes via chat
    /// capture, then re-enters the reason flow with that duration. 0 means permanent.
    /// </summary>
    private void RequestCustomDuration(
        IGameClient admin,
        IGameClient target,
        string      targetName,
        string      actionType,
        string      actionLabel)
    {
        var adminSlot  = (int) admin.Slot.AsPrimitive();
        var targetSlot = (int) target.Slot.AsPrimitive();

        _input.RequestInput(
            adminSlot,
            $"Type the {actionLabel.ToLowerInvariant()} duration in minutes (0 = permanent):",
            AdminInputKind.Integer,
            (slot, value) =>
            {
                var minutes  = (long) value;
                TimeSpan? dur = minutes <= 0 ? null : TimeSpan.FromMinutes(minutes);

                // Re-resolve live clients on the game thread before re-opening the menu.
                var liveAdmin  = _bridge.ClientManager.GetGameClient((PlayerSlot) (byte) slot);
                var liveTarget = _bridge.ClientManager.GetGameClient((PlayerSlot) (byte) targetSlot);

                if (liveAdmin is not { IsInGame: true } || _bridge.MenuManager is not { } mm)
                    return;

                if (liveTarget is not { IsInGame: true })
                {
                    liveAdmin.Print(HudPrintChannel.Chat, " [AdminPanel] Target is no longer connected.");
                    return;
                }

                var menu = BuildReasonMenu(liveAdmin, liveTarget, targetName, actionType, actionLabel, dur);
                mm.DisplayMenu(liveAdmin, menu);
            },
            min: 0,
            max: 525600, // one year in minutes — sanity cap
            timeoutSeconds: 30);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 4. Reason menu (preset list from DB)
    // ──────────────────────────────────────────────────────────────────────────

    private Menu BuildReasonMenu(
        IGameClient admin,
        IGameClient target,
        string targetName,
        string actionType,
        string actionLabel,
        TimeSpan? duration)
    {
        var presets = _reasons.GetPresets(actionType);
        var durationStr = FormatDuration(duration);
        var title = duration.HasValue
            ? $"{actionLabel} — {targetName} — {durationStr} — Reason"
            : $"{actionLabel} — {targetName} — Reason";

        var builder = Menu.Create().Title(title);

        if (presets.Count == 0)
        {
            builder.DisabledItem("(no reasons configured)");
        }
        else
        {
            foreach (var preset in presets)
            {
                var capturedReason = preset.ReasonText;
                builder.Item(preset.Label, ctrl =>
                {
                    ctrl.Exit();
                    ExecuteAction(admin, target, targetName, actionType, duration, capturedReason);
                });
            }
        }

        // PROOF (input primitive): capture a free-text reason via chat, then execute.
        builder.Item("Custom reason…", ctrl =>
        {
            ctrl.Exit();
            RequestCustomReason(admin, target, targetName, actionType, duration);
        });

        builder.BackItem("« Back");
        builder.ExitItem("Exit");
        return builder.Build();
    }

    /// <summary>
    /// PROOF (input primitive): prompts the admin for a free-text reason via chat capture,
    /// then executes the action with it.
    /// </summary>
    private void RequestCustomReason(
        IGameClient admin,
        IGameClient target,
        string      targetName,
        string      actionType,
        TimeSpan?   duration)
    {
        var adminSlot  = (int) admin.Slot.AsPrimitive();
        var targetSlot = (int) target.Slot.AsPrimitive();

        _input.RequestInput(
            adminSlot,
            "Type the reason:",
            AdminInputKind.String,
            (slot, value) =>
            {
                var reason = (string) value;

                var liveAdmin  = _bridge.ClientManager.GetGameClient((PlayerSlot) (byte) slot);
                var liveTarget = _bridge.ClientManager.GetGameClient((PlayerSlot) (byte) targetSlot);

                if (liveAdmin is not { IsInGame: true })
                    return;

                if (actionType != "kick" && liveTarget is not { IsInGame: true })
                {
                    liveAdmin.Print(HudPrintChannel.Chat, " [AdminPanel] Target is no longer connected.");
                    return;
                }

                if (liveTarget is null)
                {
                    liveAdmin.Print(HudPrintChannel.Chat, " [AdminPanel] Target is no longer connected.");
                    return;
                }

                ExecuteAction(liveAdmin, liveTarget, targetName, actionType, duration, reason);
            },
            timeoutSeconds: 30);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 5. Map change menu
    // ──────────────────────────────────────────────────────────────────────────

    private Menu BuildMapMenu(IGameClient admin)
    {
        // Build a list of available maps. ModSharp doesn't expose a map list API,
        // so we present common workshop/official maps and let admins type via console.
        var builder = Menu.Create().Title("Map Change — Select Map");

        // Provide commonly used competitive maps; the website can manage this via DB later.
        string[] maps =
        [
            "de_dust2",
            "de_mirage",
            "de_inferno",
            "de_nuke",
            "de_overpass",
            "de_ancient",
            "de_anubis",
            "de_vertigo",
            "cs_office",
            "cs_italy",
        ];

        foreach (var map in maps)
        {
            var capturedMap = map;
            builder.Item(map, ctrl =>
            {
                ctrl.Exit();
                ExecuteMapChange(admin, capturedMap);
            });
        }

        builder.BackItem("« Back");
        builder.ExitItem("Exit");
        return builder.Build();
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 6. Team submenu
    // ──────────────────────────────────────────────────────────────────────────

    private Menu BuildTeamMenu(IGameClient admin, IGameClient target, string targetName)
    {
        return Menu.Create()
            .Title($"Team — {targetName}")
            .Item("Counter-Terrorist", ctrl =>
            {
                ctrl.Exit();
                ExecuteTeam(admin, target, targetName, CStrikeTeam.CT);
            })
            .Item("Terrorist", ctrl =>
            {
                ctrl.Exit();
                ExecuteTeam(admin, target, targetName, CStrikeTeam.TE);
            })
            .Item("Spectator", ctrl =>
            {
                ctrl.Exit();
                ExecuteTeam(admin, target, targetName, CStrikeTeam.Spectator);
            })
            .BackItem("« Back")
            .ExitItem("Exit")
            .Build();
    }

    // ──────────────────────────────────────────────────────────────────────────
    // External action invocation (Shared API)
    // ──────────────────────────────────────────────────────────────────────────

    private void InvokePlayerAction(IGameClient admin, IGameClient target, AdminPanelPlayerAction action)
    {
        // Capture slots now; resolve back to live clients on the game thread at fire time
        // so a disconnect between menu select and execution can't hand the callback a stale
        // pointer.
        var adminSlot  = (int) admin.Slot.AsPrimitive();
        var targetSlot = (int) target.Slot.AsPrimitive();

        _bridge.ModSharp.InvokeFrameAction(() =>
        {
            var liveAdmin  = _bridge.ClientManager.GetGameClient((PlayerSlot) (byte) adminSlot);
            var liveTarget = _bridge.ClientManager.GetGameClient((PlayerSlot) (byte) targetSlot);

            if (liveAdmin is not { IsInGame: true })
                return;
            if (liveTarget is not { IsInGame: true })
            {
                liveAdmin.Print(HudPrintChannel.Chat, " [AdminPanel] Target is no longer connected.");
                return;
            }

            try
            {
                action.OnSelected(adminSlot, targetSlot);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "[AdminPanel] External player-action '{Id}' threw", action.Id);
            }
        });
    }

    private void InvokeGlobalAction(IGameClient admin, AdminPanelGlobalAction action)
    {
        var adminSlot = (int) admin.Slot.AsPrimitive();

        _bridge.ModSharp.InvokeFrameAction(() =>
        {
            var liveAdmin = _bridge.ClientManager.GetGameClient((PlayerSlot) (byte) adminSlot);
            if (liveAdmin is not { IsInGame: true })
                return;

            try
            {
                action.OnSelected(adminSlot);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "[AdminPanel] External global-action '{Id}' threw", action.Id);
            }
        });
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Execution methods
    // ──────────────────────────────────────────────────────────────────────────

    private void ExecuteAction(
        IGameClient admin,
        IGameClient target,
        string targetName,
        string actionType,
        TimeSpan? duration,
        string reason)
    {
        if (_bridge.AdminService is not { } svc)
        {
            admin.Print(HudPrintChannel.Chat, " [AdminPanel] AdminCommands unavailable.");
            return;
        }

        var opType = actionType switch
        {
            "ban"  => AdminOperationType.Ban,
            "mute" => AdminOperationType.Mute,
            "gag"  => AdminOperationType.Gag,
            "kick" => AdminOperationType.Ban, // handled separately below
            _      => AdminOperationType.Ban,
        };

        if (actionType == "kick")
        {
            ExecuteKick(admin, target, targetName, reason);
            return;
        }

        if (target is not { IsInGame: true })
        {
            admin.Print(HudPrintChannel.Chat, " [AdminPanel] Target is no longer connected.");
            return;
        }

        if (actionType == "silence")
            svc.Silence.Silence(admin, target, duration, reason);
        else
            svc.Apply(admin, target, opType, duration, reason);

        var durStr = FormatDuration(duration);
        admin.Print(HudPrintChannel.Chat,
            $" [AdminPanel] {Capitalize(actionType)}d {targetName} ({durStr}): {reason}");
        _logger.LogInformation("[AdminPanel] {Admin} -> {Action} {Target} ({Steam}) dur={Dur} reason={Reason}",
            (ulong) admin.SteamId, actionType, targetName, (ulong) target.SteamId, durStr, reason);
    }

    private void ExecuteKick(IGameClient admin, IGameClient target, string targetName, string reason)
    {
        if (target is not { IsInGame: true })
        {
            admin.Print(HudPrintChannel.Chat, " [AdminPanel] Target is no longer connected.");
            return;
        }

        _bridge.ModSharp.InvokeFrameAction(() =>
        {
            _bridge.ClientManager.KickClient(target, reason, NetworkDisconnectionReason.Kicked);
        });

        admin.Print(HudPrintChannel.Chat, $" [AdminPanel] Kicked {targetName}: {reason}");
        _logger.LogInformation("[AdminPanel] {Admin} -> Kick {Target} ({Steam}): {Reason}",
            (ulong) admin.SteamId, targetName, (ulong) target.SteamId, reason);
    }

    private void ExecuteSlay(IGameClient admin, IGameClient target, string targetName)
    {
        var pawn = target.GetPlayerController()?.GetPlayerPawn();
        if (pawn is null)
        {
            admin.Print(HudPrintChannel.Chat, " [AdminPanel] Target has no active pawn.");
            return;
        }

        // Defeat godmode before slaying, matching AdminCommands' own slay handler.
        pawn.AllowTakesDamage = true;
        pawn.Slay();

        admin.Print(HudPrintChannel.Chat, $" [AdminPanel] Slayed {targetName}.");
        _logger.LogInformation("[AdminPanel] {Admin} -> Slay {Target} ({Steam})",
            (ulong) admin.SteamId, targetName, (ulong) target.SteamId);
    }

    private void ExecuteSlap(IGameClient admin, IGameClient target, string targetName)
    {
        var pawn = target.GetPlayerController()?.GetPlayerPawn();
        if (pawn is null)
        {
            admin.Print(HudPrintChannel.Chat, " [AdminPanel] Target has no active pawn.");
            return;
        }

        // NEVER use raw Health -= X; use DispatchTraceAttack per ecosystem rules.
        var dmg = new TakeDamageInfo();
        dmg.Damage     = _config.SlapDamage;
        dmg.DamageType = DamageFlagBits.Generic;
        pawn.DispatchTraceAttack(in dmg);

        // Launch impulse (cosmetic), matching AdminCommands' slap behaviour.
        pawn.ApplyAbsVelocityImpulse(new Vector(0f, 0f, 250f));

        admin.Print(HudPrintChannel.Chat,
            $" [AdminPanel] Slapped {targetName} for {_config.SlapDamage} damage.");
        _logger.LogInformation("[AdminPanel] {Admin} -> Slap {Target} ({Steam}) dmg={Dmg}",
            (ulong) admin.SteamId, targetName, (ulong) target.SteamId, _config.SlapDamage);
    }

    private void ExecuteMapChange(IGameClient admin, string map)
    {
        admin.Print(HudPrintChannel.Chat, $" [AdminPanel] Changing map to {map}.");
        _logger.LogInformation("[AdminPanel] {Admin} -> Map change to {Map}", (ulong) admin.SteamId, map);

        _bridge.ModSharp.InvokeFrameAction(() =>
        {
            _bridge.ModSharp.ChangeLevel(map);
        });
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Native built-in operations (pawn/controller ops — never raw Health -=)
    // ──────────────────────────────────────────────────────────────────────────

    private void ExecuteGod(IGameClient admin, IGameClient target, string targetName)
    {
        var pawn = target.GetPlayerController()?.GetPlayerPawn();
        if (pawn is null)
        {
            admin.Print(HudPrintChannel.Chat, " [AdminPanel] Target has no active pawn.");
            return;
        }

        // Toggle: AllowTakesDamage == true means god is OFF; flip it.
        var godOn = !pawn.AllowTakesDamage;
        pawn.AllowTakesDamage = !godOn;

        admin.Print(HudPrintChannel.Chat,
            $" [AdminPanel] God mode {(godOn ? "enabled" : "disabled")} for {targetName}.");
        _logger.LogInformation("[AdminPanel] {Admin} -> God({On}) {Target} ({Steam})",
            (ulong) admin.SteamId, godOn, targetName, (ulong) target.SteamId);
    }

    private void RequestHealth(IGameClient admin, IGameClient target, string targetName)
    {
        var adminSlot  = (int) admin.Slot.AsPrimitive();
        var targetSlot = (int) target.Slot.AsPrimitive();

        _input.RequestInput(
            adminSlot,
            "Type the health value to set (>0):",
            AdminInputKind.Integer,
            (slot, value) =>
            {
                var hp = (int) Math.Clamp((long) value, 1, 1_000_000);
                if (!TryResolve(slot, targetSlot, out var liveAdmin, out var liveTarget))
                    return;

                var pawn = liveTarget.GetPlayerController()?.GetPlayerPawn();
                if (pawn is null)
                {
                    liveAdmin.Print(HudPrintChannel.Chat, " [AdminPanel] Target has no active pawn.");
                    return;
                }

                if (hp > pawn.MaxHealth)
                    pawn.MaxHealth = hp;
                pawn.Health = hp;

                liveAdmin.Print(HudPrintChannel.Chat, $" [AdminPanel] Set {targetName}'s health to {hp}.");
                _logger.LogInformation("[AdminPanel] {Admin} -> SetHP {Target} ({Steam}) hp={Hp}",
                    (ulong) liveAdmin.SteamId, targetName, (ulong) liveTarget.SteamId, hp);
            },
            min: 1,
            max: 1_000_000,
            timeoutSeconds: 30);
    }

    private void ExecuteRespawn(IGameClient admin, IGameClient target, string targetName)
    {
        var controller = target.GetPlayerController();
        if (controller is null)
        {
            admin.Print(HudPrintChannel.Chat, " [AdminPanel] Target has no controller.");
            return;
        }

        controller.Respawn();

        admin.Print(HudPrintChannel.Chat, $" [AdminPanel] Respawned {targetName}.");
        _logger.LogInformation("[AdminPanel] {Admin} -> Respawn {Target} ({Steam})",
            (ulong) admin.SteamId, targetName, (ulong) target.SteamId);
    }

    private void ExecuteNoclip(IGameClient admin, IGameClient target, string targetName)
    {
        var pawn = target.GetPlayerController()?.GetPlayerPawn();
        if (pawn is null)
        {
            admin.Print(HudPrintChannel.Chat, " [AdminPanel] Target has no active pawn.");
            return;
        }

        var noclipOn = pawn.MoveType != MoveType.NoClip;
        pawn.SetMoveType(noclipOn ? MoveType.NoClip : MoveType.Walk);

        admin.Print(HudPrintChannel.Chat,
            $" [AdminPanel] Noclip {(noclipOn ? "enabled" : "disabled")} for {targetName}.");
        _logger.LogInformation("[AdminPanel] {Admin} -> Noclip({On}) {Target} ({Steam})",
            (ulong) admin.SteamId, noclipOn, targetName, (ulong) target.SteamId);
    }

    private void ExecuteFreeze(IGameClient admin, IGameClient target, string targetName, bool freeze)
    {
        var pawn = target.GetPlayerController()?.GetPlayerPawn();
        if (pawn is null)
        {
            admin.Print(HudPrintChannel.Chat, " [AdminPanel] Target has no active pawn.");
            return;
        }

        pawn.SetMoveType(freeze ? MoveType.None : MoveType.Walk);

        admin.Print(HudPrintChannel.Chat,
            $" [AdminPanel] {(freeze ? "Froze" : "Unfroze")} {targetName}.");
        _logger.LogInformation("[AdminPanel] {Admin} -> Freeze({Freeze}) {Target} ({Steam})",
            (ulong) admin.SteamId, freeze, targetName, (ulong) target.SteamId);
    }

    private void RequestSpeed(IGameClient admin, IGameClient target, string targetName)
    {
        var adminSlot  = (int) admin.Slot.AsPrimitive();
        var targetSlot = (int) target.Slot.AsPrimitive();

        _input.RequestInput(
            adminSlot,
            "Type the speed as a percentage (100 = normal, e.g. 200 = 2x):",
            AdminInputKind.Integer,
            (slot, value) =>
            {
                var pct   = Math.Clamp((long) value, 1, 1000);
                var speed = pct / 100f;
                if (!TryResolve(slot, targetSlot, out var liveAdmin, out var liveTarget))
                    return;

                var controller = liveTarget.GetPlayerController();
                if (controller is null)
                {
                    liveAdmin.Print(HudPrintChannel.Chat, " [AdminPanel] Target has no controller.");
                    return;
                }

                controller.LaggedMovement = speed;

                liveAdmin.Print(HudPrintChannel.Chat, $" [AdminPanel] Set {targetName}'s speed to {pct}%.");
                _logger.LogInformation("[AdminPanel] {Admin} -> Speed {Target} ({Steam}) speed={Speed}",
                    (ulong) liveAdmin.SteamId, targetName, (ulong) liveTarget.SteamId, speed);
            },
            min: 1,
            max: 1000,
            timeoutSeconds: 30);
    }

    private void RequestGravity(IGameClient admin, IGameClient target, string targetName)
    {
        var adminSlot  = (int) admin.Slot.AsPrimitive();
        var targetSlot = (int) target.Slot.AsPrimitive();

        _input.RequestInput(
            adminSlot,
            "Type the gravity as a percentage (100 = normal, e.g. 50 = half):",
            AdminInputKind.Integer,
            (slot, value) =>
            {
                var pct   = Math.Clamp((long) value, 1, 1000);
                var scale = pct / 100f;
                if (!TryResolve(slot, targetSlot, out var liveAdmin, out var liveTarget))
                    return;

                var pawn = liveTarget.GetPlayerController()?.GetPlayerPawn();
                if (pawn is null)
                {
                    liveAdmin.Print(HudPrintChannel.Chat, " [AdminPanel] Target has no active pawn.");
                    return;
                }

                pawn.SetGravityScale(scale);

                liveAdmin.Print(HudPrintChannel.Chat, $" [AdminPanel] Set {targetName}'s gravity to {pct}%.");
                _logger.LogInformation("[AdminPanel] {Admin} -> Gravity {Target} ({Steam}) scale={Scale}",
                    (ulong) liveAdmin.SteamId, targetName, (ulong) liveTarget.SteamId, scale);
            },
            min: 1,
            max: 1000,
            timeoutSeconds: 30);
    }

    private void ExecuteTeam(IGameClient admin, IGameClient target, string targetName, CStrikeTeam team)
    {
        var controller = target.GetPlayerController();
        if (controller is null)
        {
            admin.Print(HudPrintChannel.Chat, " [AdminPanel] Target has no controller.");
            return;
        }

        // Spectator/unassigned use ChangeTeam; live teams use SwitchTeam (no slay).
        if (team <= CStrikeTeam.Spectator)
            controller.ChangeTeam(team);
        else
            controller.SwitchTeam(team);

        admin.Print(HudPrintChannel.Chat, $" [AdminPanel] Moved {targetName} to {team}.");
        _logger.LogInformation("[AdminPanel] {Admin} -> Team {Target} ({Steam}) team={Team}",
            (ulong) admin.SteamId, targetName, (ulong) target.SteamId, team);
    }

    private void RequestRename(IGameClient admin, IGameClient target, string targetName)
    {
        var adminSlot  = (int) admin.Slot.AsPrimitive();
        var targetSlot = (int) target.Slot.AsPrimitive();

        _input.RequestInput(
            adminSlot,
            "Type the new name:",
            AdminInputKind.String,
            (slot, value) =>
            {
                var newName = (string) value;
                if (!TryResolve(slot, targetSlot, out var liveAdmin, out var liveTarget))
                    return;

                if (liveTarget.GetPlayerController() is { } controller)
                    controller.PlayerName = newName;
                liveTarget.SetName(newName);

                liveAdmin.Print(HudPrintChannel.Chat, $" [AdminPanel] Renamed {targetName} to {newName}.");
                _logger.LogInformation("[AdminPanel] {Admin} -> Rename {Target} ({Steam}) name={Name}",
                    (ulong) liveAdmin.SteamId, targetName, (ulong) liveTarget.SteamId, newName);
            },
            timeoutSeconds: 30);
    }

    private void RequestMoney(IGameClient admin, IGameClient target, string targetName)
    {
        var adminSlot  = (int) admin.Slot.AsPrimitive();
        var targetSlot = (int) target.Slot.AsPrimitive();

        _input.RequestInput(
            adminSlot,
            "Type the money amount to set (0–60000):",
            AdminInputKind.Integer,
            (slot, value) =>
            {
                var amount = (int) Math.Clamp((long) value, 0, 60000);
                if (!TryResolve(slot, targetSlot, out var liveAdmin, out var liveTarget))
                    return;

                if (liveTarget.GetPlayerController()?.GetInGameMoneyService() is not { } money)
                {
                    liveAdmin.Print(HudPrintChannel.Chat, " [AdminPanel] Target has no money service.");
                    return;
                }

                money.Account = amount;

                liveAdmin.Print(HudPrintChannel.Chat, $" [AdminPanel] Set {targetName}'s money to {amount}.");
                _logger.LogInformation("[AdminPanel] {Admin} -> Money {Target} ({Steam}) amount={Amount}",
                    (ulong) liveAdmin.SteamId, targetName, (ulong) liveTarget.SteamId, amount);
            },
            min: 0,
            max: 60000,
            timeoutSeconds: 30);
    }

    private void ExecuteBring(IGameClient admin, IGameClient target, string targetName)
    {
        var adminPawn  = admin.GetPlayerController()?.GetPlayerPawn();
        var targetPawn = target.GetPlayerController()?.GetPlayerPawn();
        if (adminPawn is null || targetPawn is null)
        {
            admin.Print(HudPrintChannel.Chat, " [AdminPanel] Both admin and target must have an active pawn.");
            return;
        }

        targetPawn.Teleport(adminPawn.GetAbsOrigin(), null, new Vector(0f, 0f, 0f));

        admin.Print(HudPrintChannel.Chat, $" [AdminPanel] Brought {targetName} to you.");
        _logger.LogInformation("[AdminPanel] {Admin} -> Bring {Target} ({Steam})",
            (ulong) admin.SteamId, targetName, (ulong) target.SteamId);
    }

    private void ExecuteGoto(IGameClient admin, IGameClient target, string targetName)
    {
        var adminPawn  = admin.GetPlayerController()?.GetPlayerPawn();
        var targetPawn = target.GetPlayerController()?.GetPlayerPawn();
        if (adminPawn is null || targetPawn is null)
        {
            admin.Print(HudPrintChannel.Chat, " [AdminPanel] Both admin and target must have an active pawn.");
            return;
        }

        adminPawn.Teleport(targetPawn.GetAbsOrigin(), null, new Vector(0f, 0f, 0f));

        admin.Print(HudPrintChannel.Chat, $" [AdminPanel] Teleported to {targetName}.");
        _logger.LogInformation("[AdminPanel] {Admin} -> Goto {Target} ({Steam})",
            (ulong) admin.SteamId, targetName, (ulong) target.SteamId);
    }

    private void ExecuteStrip(IGameClient admin, IGameClient target, string targetName)
    {
        var pawn = target.GetPlayerController()?.GetPlayerPawn();
        if (pawn is null)
        {
            admin.Print(HudPrintChannel.Chat, " [AdminPanel] Target has no active pawn.");
            return;
        }

        if (!pawn.IsAlive)
        {
            admin.Print(HudPrintChannel.Chat, " [AdminPanel] Target is not alive.");
            return;
        }

        pawn.RemoveAllItems(true);

        admin.Print(HudPrintChannel.Chat, $" [AdminPanel] Stripped {targetName}'s weapons.");
        _logger.LogInformation("[AdminPanel] {Admin} -> Strip {Target} ({Steam})",
            (ulong) admin.SteamId, targetName, (ulong) target.SteamId);
    }

    private void RequestGive(IGameClient admin, IGameClient target, string targetName)
    {
        var adminSlot  = (int) admin.Slot.AsPrimitive();
        var targetSlot = (int) target.Slot.AsPrimitive();

        _input.RequestInput(
            adminSlot,
            "Type the item to give (e.g. weapon_ak47):",
            AdminInputKind.String,
            (slot, value) =>
            {
                var itemName = (string) value;
                if (!TryResolve(slot, targetSlot, out var liveAdmin, out var liveTarget))
                    return;

                var pawn = liveTarget.GetPlayerController()?.GetPlayerPawn();
                if (pawn is null)
                {
                    liveAdmin.Print(HudPrintChannel.Chat, " [AdminPanel] Target has no active pawn.");
                    return;
                }

                if (pawn.GiveNamedItem(itemName) is null)
                {
                    liveAdmin.Print(HudPrintChannel.Chat, $" [AdminPanel] Failed to give '{itemName}'.");
                    return;
                }

                liveAdmin.Print(HudPrintChannel.Chat, $" [AdminPanel] Gave {itemName} to {targetName}.");
                _logger.LogInformation("[AdminPanel] {Admin} -> Give {Target} ({Steam}) item={Item}",
                    (ulong) liveAdmin.SteamId, targetName, (ulong) liveTarget.SteamId, itemName);
            },
            timeoutSeconds: 30);
    }

    /// <summary>
    /// Re-resolve admin + target to live clients on the game thread (inside an input callback).
    /// Returns false (and notifies the admin) when either is gone.
    /// </summary>
    private bool TryResolve(
        int adminSlot,
        int targetSlot,
        out IGameClient admin,
        out IGameClient target)
    {
        admin  = null!;
        target = null!;

        var liveAdmin = _bridge.ClientManager.GetGameClient((PlayerSlot) (byte) adminSlot);
        if (liveAdmin is not { IsInGame: true })
            return false;

        var liveTarget = _bridge.ClientManager.GetGameClient((PlayerSlot) (byte) targetSlot);
        if (liveTarget is not { IsInGame: true })
        {
            liveAdmin.Print(HudPrintChannel.Chat, " [AdminPanel] Target is no longer connected.");
            return false;
        }

        admin  = liveAdmin;
        target = liveTarget;
        return true;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────────────

    private static string FormatDuration(TimeSpan? duration)
    {
        if (duration is null)
            return "Permanent";
        if (duration.Value.TotalDays >= 30)
            return $"{(int) duration.Value.TotalDays}d";
        if (duration.Value.TotalDays >= 1)
            return $"{(int) duration.Value.TotalDays}d";
        if (duration.Value.TotalHours >= 1)
            return $"{(int) duration.Value.TotalHours}h";
        return $"{(int) duration.Value.TotalMinutes}m";
    }

    private static string Capitalize(string s) =>
        s.Length == 0 ? s : char.ToUpper(s[0]) + s[1..];
}
