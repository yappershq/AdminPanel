I now have a complete inventory. Here is the survey and integration plan.

---

# AdminPanel — ModSharp Ecosystem Integration Survey

## Integration model (key finding)
AdminPanel is a **host**: actions are injected via `IAdminPanelShared.RegisterPlayerAction/RegisterGlobalAction` (slots `int`, callback on game thread). The cleanest pattern for the starter batch is to **add the registration code inside AdminPanel itself** (AdminPanel.Core resolves each plugin's `*.Shared` interface via `GetOptionalSharpModuleInterface` in OAM and registers actions, degrading gracefully if a plugin is absent) — rather than touching each plugin. AdminPanel already wires `IAdminService / IAdminManager / IMenuManager` in `InterfaceBridge` (the built-in ban/kick/mute/gag/slay/slap/map flow). Typed input (int/string) currently has **no input primitive** in the Shared contract — only target+toggle/select via sub-menus. That's the main gap for "duration/amount" actions (see note at end).

Per-plugin Shared `Identity` strings confirmed: `IVipShared`, `IVipPerkRegistry`, `IReservedSlotsShared`, `IPoopShared` (FullName), `ICallAdminShared`, `IPlayerAnalyticsService` (="PlayerAnalytics.Core"), `IHexTagsShared`, `IMapManager`, `IAdminDbShared`, `IChatProcessorShared`, `IMixScrims`, `IMmoCore`/`IPlayerService` (="Mmo.Core.Player").

---

## Table

| Plugin | Feature/action | How to invoke from AdminPanel | Input type | Suggested label + permission |
|---|---|---|---|---|
| **Vip.Core** | Grant VIP | `IVipShared.GrantAsync(steamId, expiresAtUtc, flags, adminSteam, note)` — full async API exists | steamId(target)+**duration**+flags(string) | "Grant VIP ▸" / `@adminpanel/vip` |
| **Vip.Core** | Revoke VIP | `IVipShared.RevokeAsync(steamId, adminSteam)` | target only | "Revoke VIP" / `@adminpanel/vip` |
| **Vip.Core** | Check VIP / flag | `IsVip(steamId)` / `HasFlag(steamId, flag)` | target | "VIP info" / `@adminpanel/vip` |
| **Vip.Perk.*** | Toggle a player's perk pref | `IVipPerkRegistry.Get/SetPreferences(steamId, perkId, prefs)` | target+select | "VIP perks ▸" / `@adminpanel/vip` |
| **Poop.Core** | Spawn poop on target | `IPoopShared.SpawnPoop...` (target overload, `playSounds`) | target+toggle | "Poop player" / `@adminpanel/fun` |
| **Poop.Core** | Set/inspect color, stats | `GetPlayerColorPreferenceAsync`, `GetPlayerStatsAsync` | target | "Poop stats" / `@adminpanel/fun` |
| **Poop.Core** | *Mute toggle* | NOT in Shared — `!poopmute` is a clientpref command for self | self toggle | (B) needs hook — see below |
| **ReservedSlots** | (read VIP status) | `IsVipBySteamId(steamId)` — read-only, no grant/revoke | target | low value; B if write wanted |
| **CallAdmin.Core** | Claim report | `ClaimReportAsync(reportId, admin)` | reportId(select)+admin | "CallAdmin ▸ Claim" / `@calladmin/handle` |
| **CallAdmin.Core** | Mark handled / dismiss | `MarkHandledAsync` / `DismissReportAsync(reportId, admin)` | reportId(select) | "Resolve report" / `@calladmin/handle` |
| **CallAdmin.Core** | List pending | `GetPendingReportsAsync()` → drive a select sub-menu | none | "Open reports" / `@calladmin/handle` |
| **PlayerAnalytics** | Connection history (IP↔SteamID, alts) | `GetConnectionHistoryAsync(steamId)` / `GetRecentConnectionsAsync` | target | "Connection history" / `@adminpanel/lookup` |
| **HexTags.Core** | Hide/show a player's tag | `SetHidden(slot, bool)` / `IsHidden(slot)` — **slot-based, perfect fit** | target+toggle | "Toggle chat tag" / `@adminpanel/tags` |
| **MapChooser** | Force change map | `IMapManager.ChangeLevel(mapName/IGameMap)` + `GetMaps()` for picker | select (map) | "Change map ▸" (global) / `@adminpanel/map` |
| **MapChooser** | *Start RTV/map vote* | NOT exposed — only events + ChangeLevel | — | (B) needs hook |
| **AdminDb.Core** | Refresh admin cache | `IAdminDbShared.RefreshNow()` | none | "Reload admins" (global) / `@adminpanel/reload` |
| **CsRepGuard** | Reputation lookup | NO Shared interface — only `!csrep`/`!rep` admin commands (`@csrepguard/lookup`) | target | "CS Rep lookup" / `@csrepguard/lookup` — (B) small hook, or exec command |
| **Sleuth** | Toggle covert/stealth | NO Shared — `RegisterAdminCommand` (one cmd, self toggle), `@sleuth/...` | self toggle | "Stealth mode" / sleuth perm — (B) hook |
| **AfkManager** | Move self to spec | NO Shared — `afk_spec` **server command, self-only**; immunity is convar-driven, not runtime | self | low value; (B) for per-target move |
| **OfflineMod** | Punish disconnected player | NO Shared — `!offline` opens its **own MenuManager menu** (`@offlinemod/punish`) | menu | "Offline punish" (global) → just exec `offline` / `@offlinemod/punish` |
| **AntiVpnGuard** | Add/remove VPN bypass | `GuardBypassStore` has `Contains` only (read); add/remove is DB/file, not public | target | (B) needs add/remove method exposed |
| **AltGuard** | Add bypass / unban-evasion | Same: `GuardBypassStore`, no public add/remove | target | (B) needs hook |
| **MMO Core** | Give resource/currency | `IPlayerService.GrantResourceAsync(playerId, resourceItemId, amount, ...)` (+`GetOrCreatePlayerIdAsync`) | target+**amount(int)**+resource(select) | "MMO give ▸" / `@adminpanel/mmo` (minigames server only) |
| **MMO Core** | Spend/deduct | `SpendResourceAsync(...)` | target+amount | "MMO take ▸" / `@adminpanel/mmo` |
| **ServerAds.Core** | Announce / broadcast ad | NO public method — internal `AdBroadcastModule`, config-driven only | text(string) | (B) needs `Announce(string)` hook |
| **ChatProcessor** | (chat formatting) | `IChatProcessorShared` exposes only pre/post events | — | (C) not admin-actionable |
| **MixScrims** | (message bus) | `IMixScrims` = pub/sub bus, scrim flow | — | (C) not general-admin |
| **Sharp.Modules AdminCommands/Manager/MenuManager** | Ban/Kick/Mute/Gag/Slay/Slap/Map | Already integrated (built-in actions) | — | (already shipped) |

---

## Prioritized plan

### (A) Directly integrable now — STARTER BATCH (Shared service or stable command, no upstream change)
Build these into AdminPanel.Core OAM as registered actions:
1. **HexTags — Toggle chat tag** (`SetHidden(slot,bool)`) — slot-based, ideal toggle, dynamic `LabelFactory` showing on/off. Easiest win.
2. **VIP — Grant / Revoke / Info** (`GrantAsync`/`RevokeAsync`/`IsVip`). Grant needs duration+flags input (see gap note); Revoke/Info work today with target-only.
3. **MapChooser — Change map** (global action → `GetMaps()` picker → `ChangeLevel`). High value.
4. **CallAdmin — Reports menu** (global → `GetPendingReportsAsync` picker → `ClaimReportAsync`/`MarkHandledAsync`/`DismissReportAsync`).
5. **PlayerAnalytics — Connection history** (player action → `GetConnectionHistoryAsync`, print to admin).
6. **AdminDb — Reload admins** (global, `RefreshNow()`).
7. **Poop — Poop player** (target spawn) + **Poop stats** (read).
8. **MMO — Give/Take resource** (minigames-scoped; needs amount input — see gap).
9. **OfflineMod — Offline punish** (global → execute the existing `offline` admin command via AdminService/command dispatch; reuses its own menu).

### (B) Needs a small Shared hook added to that plugin first
- **CsRepGuard**: add `ICsRepGuardShared.LookupAsync(steamId)` (today only `!csrep` command — could exec the command as a stopgap).
- **Sleuth**: add `ISleuthShared.SetCovert(slot,bool)` / `IsCovert(slot)` for menu toggle (today self-only admin command).
- **ServerAds**: add `IServerAdsShared.Announce(string)` for ad-hoc broadcasts.
- **AntiVpnGuard / AltGuard**: expose `AddBypassAsync(steamId, reason, addedBy)` + `RemoveBypassAsync` (store has only `Contains`); the "exclude + unban" admin action.
- **MapChooser**: add `StartMapVote()` / `StartRtv()` to `IMapManager` (only `ChangeLevel` today).
- **Poop mute**: expose `SetMuted(slot,bool)` (today clientpref self-command).
- **ReservedSlots**: add grant/revoke if runtime RS management wanted (currently read-only).
- **AfkManager**: add per-target `MoveToSpec(slot)` / runtime `SetImmunity(slot,level)` (today self-command + convar).

### (C) Skip — not admin-relevant
- **ChatProcessor** (formatting events), **MixScrims** (scrim pub/sub bus), ReservedSlots read-only status, AfkManager self-spec.

---

## Critical gap to resolve before A2/A8 ship
`IAdminPanelShared` has **no typed-input primitive** — only target + toggle/sub-menu select. Actions needing **duration** (VIP grant) or **int amount** (MMO give/take) can't collect input through the current contract. Two options:
- **Preferred:** extend the Shared contract with an input-request callback (e.g. `RegisterPlayerAction` variant exposing `RequestInt`/`RequestString`/`RequestDuration` via AdminPanel's MenuManager-driven prompt, mirroring the built-in ban-duration/reason flow AdminPanel already implements).
- **Stopgap:** ship preset sub-menus (VIP: 1d/7d/30d/perm; MMO: 100/1k/10k) as discrete menu entries — no contract change, integrable today.

Relevant files: `/home/claude/AdminPanel/AdminPanel.Shared/IAdminPanelShared.cs`, `/home/claude/AdminPanel/AdminPanel.Core/InterfaceBridge.cs`, `/home/claude/AdminPanel/AdminPanel.Core/Modules/AdminCommandModule.cs` (built-in duration/reason flow to mirror), and each plugin's Shared interface listed above.
