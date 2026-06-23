using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using AdminPanel.Configuration;
using AdminPanel.Database;
using Microsoft.Extensions.Logging;
using Sharp.Modules.AdminCommands.Shared;
using Sharp.Modules.AdminManager.Shared;
using Sharp.Modules.MenuManager.Shared;
using Sharp.Shared;
using Sharp.Shared.Enums;
using Sharp.Shared.Managers;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;

namespace AdminPanel.Modules;

/// <summary>
/// Registers the !admin command and builds the full menu flow:
///   Player picker → Action menu → [Duration →] Reason → Execute
/// All admin permission checks use the AdminManager IAdmin.HasPermission pattern.
/// </summary>
internal sealed class AdminCommandModule
{
    private const string ModuleId        = "AdminPanel";
    private const string PermOpen        = "@adminpanel/open";
    private const string PermBan         = "@admin/ban";
    private const string PermKick        = "@admin/kick";
    private const string PermMute        = "@admin/mute";
    private const string PermGag         = "@admin/gag";
    private const string PermSlay        = "@admin/slay";
    private const string PermSlap        = "@admin/slap";
    private const string PermMap         = "@admin/map";

    private readonly InterfaceBridge              _bridge;
    private readonly AdminPanelConfig             _config;
    private readonly ReasonSyncModule             _reasons;
    private readonly ILogger<AdminCommandModule>  _logger;

    private IClientManager.DelegateClientCommand? _fallback;
    private bool                                  _usedRegistry;

    public AdminCommandModule(
        InterfaceBridge bridge,
        AdminPanelConfig config,
        ReasonSyncModule reasons,
        ILogger<AdminCommandModule> logger)
    {
        _bridge  = bridge;
        _config  = config;
        _reasons = reasons;
        _logger  = logger;
    }

    public void Start()
    {
        if (_bridge.MenuManager is null)
        {
            _logger.LogWarning("[AdminPanel] MenuManager unavailable — !{Cmd} disabled", _config.Command);
            return;
        }

        if (_bridge.AdminManager is { } am)
        {
            am.MountAdminManifest(ModuleId, () => new AdminTableManifest(
                new Dictionary<string, HashSet<string>>
                {
                    ["adminpanel"] = [PermOpen, PermBan, PermKick, PermMute, PermGag, PermSlay, PermSlap, PermMap]
                },
                [],
                []));

            am.GetCommandRegistry(ModuleId)
              .RegisterAdminCommand(_config.Command, OnAdminCommand, ImmutableArray.Create(PermOpen));

            _usedRegistry = true;
            _logger.LogInformation("[AdminPanel] !{Cmd} registered (perm {Perm})", _config.Command, PermOpen);
        }
        else
        {
            _fallback = (client, cmd) =>
            {
                OnAdminCommand(client, cmd);
                return ECommandAction.Handled;
            };
            _bridge.ClientManager.InstallCommandCallback(_config.Command, _fallback);
            _logger.LogWarning("[AdminPanel] AdminManager unavailable — !{Cmd} registered WITHOUT permission check", _config.Command);
        }
    }

    public void Stop()
    {
        if (!_usedRegistry && _fallback is not null)
            _bridge.ClientManager.RemoveCommandCallback(_config.Command, _fallback);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Command entry
    // ──────────────────────────────────────────────────────────────────────────

    private void OnAdminCommand(IGameClient? invoker, StringCommand _cmd)
    {
        if (invoker is null || invoker.IsFakeClient)
            return;

        ShowPlayerPicker(invoker);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 1. Player picker
    // ──────────────────────────────────────────────────────────────────────────

    private void ShowPlayerPicker(IGameClient admin)
    {
        if (_bridge.MenuManager is not { } mm) return;

        var builder = Menu.Create().Title("Admin Panel — Select Player");

        var players = _bridge.ClientManager.GetGameClients();
        bool any = false;

        foreach (var client in players)
        {
            if (client.IsFakeClient || client.IsHltv)
                continue;
            if (client.Equals(admin))
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

        builder.ExitItem("Exit");
        mm.DisplayMenu(admin, builder.Build());
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 2. Action menu
    // ──────────────────────────────────────────────────────────────────────────

    private Menu BuildActionMenu(IGameClient admin, IGameClient target, string targetName)
    {
        var adminObj = _bridge.AdminManager?.GetAdmin(admin.SteamId);

        bool HasPerm(string perm) =>
            _bridge.AdminManager is null || (adminObj?.HasPermission(perm) ?? false);

        var builder = Menu.Create().Title($"Actions — {targetName}");

        AddActionItem(builder, "Ban",        PermBan,  HasPerm(PermBan),  ctrl =>
            ctrl.Next(c => BuildDurationMenu(c, target, targetName, "ban", "Ban")));

        AddActionItem(builder, "Kick",       PermKick, HasPerm(PermKick), ctrl =>
            ctrl.Next(c => BuildReasonMenu(c, target, targetName, "kick", "Kick", null)));

        AddActionItem(builder, "Mute",       PermMute, HasPerm(PermMute), ctrl =>
            ctrl.Next(c => BuildDurationMenu(c, target, targetName, "mute", "Mute")));

        AddActionItem(builder, "Gag",        PermGag,  HasPerm(PermGag),  ctrl =>
            ctrl.Next(c => BuildDurationMenu(c, target, targetName, "gag", "Gag")));

        AddActionItem(builder, "Slay",       PermSlay, HasPerm(PermSlay), ctrl =>
        {
            ctrl.Exit();
            ExecuteSlay(admin, target, targetName);
        });

        AddActionItem(builder, "Slap",       PermSlap, HasPerm(PermSlap), ctrl =>
        {
            ctrl.Exit();
            ExecuteSlap(admin, target, targetName);
        });

        AddActionItem(builder, "Map Change", PermMap,  HasPerm(PermMap),  ctrl =>
            ctrl.Next(c => BuildMapMenu(c)));

        builder.BackItem("« Back");
        builder.ExitItem("Exit");

        return builder.Build();
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
            .BackItem("« Back")
            .ExitItem("Exit")
            .Build();
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

        builder.BackItem("« Back");
        builder.ExitItem("Exit");
        return builder.Build();
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

        if (!target.IsConnected)
        {
            admin.Print(HudPrintChannel.Chat, " [AdminPanel] Target is no longer connected.");
            return;
        }

        svc.Apply(admin, target, opType, duration, reason);

        var durStr = FormatDuration(duration);
        admin.Print(HudPrintChannel.Chat,
            $" [AdminPanel] {Capitalize(actionType)}d {targetName} ({durStr}): {reason}");
        _logger.LogInformation("[AdminPanel] {Admin} -> {Action} {Target} ({Steam}) dur={Dur} reason={Reason}",
            (ulong) admin.SteamId, actionType, targetName, (ulong) target.SteamId, durStr, reason);
    }

    private void ExecuteKick(IGameClient admin, IGameClient target, string targetName, string reason)
    {
        if (!target.IsConnected)
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
