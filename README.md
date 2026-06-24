<div align="center">
  <h1><strong>AdminPanel</strong></h1>
  <p>In-game, menu-driven admin tool for ModSharp / CS2 — pick a player, pick an action, done.</p>
</div>

<p align="center">
  <a href="https://github.com/Kxnrl/modsharp-public"><img src="https://img.shields.io/badge/framework-ModSharp-5865F2?logo=github" alt="ModSharp"></a>
  <img src="https://img.shields.io/badge/game-CS2-orange" alt="CS2">
  <img src="https://img.shields.io/github/stars/yappershq/AdminPanel?style=flat&logo=github" alt="Stars">
</p>

---

AdminPanel adds a single `!admin` command that opens a MenuManager-driven panel: **player picker → action → (duration →) reason → execute**. It covers punishment (ban/kick/mute/gag/silence), player ops (slay, slap, god, hp, respawn, noclip, freeze, speed, gravity, team, rename, money, strip, give) and server ops (map change). Punishments route through ModSharp's AdminCommands; ban/mute/gag durations and reason presets are pulled from a MySQL database that hot-reloads on a version bump. Other plugins can inject their own actions into the menu via the `IAdminPanelShared` contract.

## 🚀 Install

Copy the build output into your ModSharp install (`<sharp>` = your `sharp` directory):

| From | To |
|------|----|
| `.build/modules/AdminPanel.Core/` | `<sharp>/modules/AdminPanel.Core/` |
| `.build/shared/AdminPanel.Shared/AdminPanel.Shared.dll` | `<sharp>/shared/` |
| `AdminPanel.Core/.assets/configs/adminpanel.json` | `<sharp>/configs/adminpanel.json` |
| `AdminPanel.Core/.assets/locales/adminpanel.json` | `<sharp>/locales/adminpanel.json` |

Set your MySQL connection in `<sharp>/configs/adminpanel.json`, then restart the server (or change map) to load.

**Requires** the ModSharp **AdminCommands**, **AdminManager**, and **MenuManager** modules (permission gating + the punishment backend + the menu UI), plus a reachable **MySQL** database for reason presets.

## ⌨️ Commands

| Command | Description | Permission |
|---------|-------------|------------|
| `!admin` | Open the admin panel (command name configurable via `Command`) | `adminpanel:open` |

Every action inside the panel is independently gated against the acting admin and only renders if they hold the matching flag:

| Category | Actions | Permissions |
|----------|---------|-------------|
| Punish | Ban, Kick, Mute, Gag, Silence | `admin:ban`, `admin:kick`, `admin:mute`, `admin:gag`, `admin:silence` |
| Player | Slay, Slap, God Mode, Set HP, Respawn, Noclip, Freeze/Unfreeze, Speed, Gravity, Team, Rename, Set Money, Strip Weapons, Give Item | `admin:slay`, `admin:slap`, `admin:god`, `admin:hp`, `admin:respawn`, `admin:noclip`, `admin:freeze`, `admin:speed`, `admin:gravity`, `admin:team`, `admin:rename`, `admin:money`, `admin:strip`, `admin:give` |
| Teleport | Bring, Goto | `admin:bring`, `admin:goto` |
| Server | Map Change | `admin:map` |

Ban/mute/gag/silence offer preset durations (1h / 1d / 7d / 30d / Permanent) plus a **Custom…** entry that captures a free-text value from chat. Reason menus likewise offer DB presets plus a **Custom reason…** entry.

## ⚙️ Configuration

`<sharp>/configs/adminpanel.json` (falls back to defaults if absent):

| Setting | Default | Meaning |
|---------|---------|---------|
| `Database` | — | MySQL connection: `Host`, `Port`, `Database`, `User`, `Password`, `MaxPoolSize` |
| `PollIntervalSeconds` | `30` | How often (min 15s) to poll the DB version for changed reason presets |
| `SlapDamage` | `10` | Damage dealt by the Slap action |
| `Command` | `admin` | Chat command that opens the panel (registered as `!<value>`) |
| `ServerTag` | `all` | Scope tag used when loading reason presets for this server |

## 🔧 How it works

On load, AdminPanel connects to MySQL, ensures its schema, and seeds default reason presets if the table is empty. A background timer polls `adminpanel_meta.reasons_version`; when it bumps, the preset cache is reloaded atomically — so a website or other tool can edit reasons in the DB and have them appear in-game without a restart. The `!admin` flow builds menus on every invocation, checking each action's permission against the acting admin via AdminManager. Punishments are applied through the AdminCommands service; player/teleport ops act on the pawn/controller directly (e.g. `DispatchTraceAttack` for slap, `Teleport` for bring/goto), and free-text input (custom duration, reason, name, item, etc.) is captured by intercepting the admin's next chat line.

## 🧩 Public API

AdminPanel publishes `IAdminPanelShared` so other plugins can add their own actions to the menu (also available as the dependency-free NuGet package `YappersHQ.AdminPanel.Shared`). Resolve it in your `OnAllModulesLoaded`:

```csharp
var panel = sharpModuleManager
    .GetOptionalSharpModuleInterface<IAdminPanelShared>(IAdminPanelShared.Identity)?.Instance;

panel?.RegisterPlayerAction(new AdminPanelPlayerAction
{
    Id         = "myplugin.greet",
    Label      = "Greet player",
    Category   = "Fun",          // groups under a built-in or new category
    Permission = "@myplugin/greet",
    OnSelected = (adminSlot, targetSlot) => { /* runs on the game thread */ },
});
```

`RegisterGlobalAction` adds a target-less action to the root menu, `Unregister(id)` removes one, and `RequestInput(...)` prompts the admin for typed integer/string input via chat capture (with range checks, timeout, and cancel keywords).

## 📦 Build

```bash
dotnet build -c Release
```

Outputs the module to `.build/modules/AdminPanel.Core/AdminPanel.dll` (with its runtime dependencies) and the public contract to `.build/shared/AdminPanel.Shared/AdminPanel.Shared.dll`.

---

<div align="center">
  <p>Made with ❤️ by <a href="https://github.com/yappershq">yappershq</a></p>
  <p>⭐ Star this repo if you find it useful!</p>
</div>
