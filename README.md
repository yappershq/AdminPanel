# AdminPanel

In-game, menu-driven admin tool for ModSharp (CS2). Exposes the `!admin` command
(gated by `@adminpanel/open`) with a root menu → player picker → action → duration →
reason flow. Built-in player actions: Ban, Kick, Mute, Gag, Slay, Slap, Map Change.

## Projects

| Project | Purpose |
|---|---|
| `AdminPanel.Core` | The plugin (assembly `AdminPanel`). Hosts the menu, registry, DB, and AdminCommands/AdminManager/MenuManager integration. |
| `AdminPanel.Shared` | Public contract (`IAdminPanelShared` + action descriptors). The only assembly a 3rd-party module needs to reference. |

## Modular actions — extending the admin menu

AdminPanel is a **modular host**: other plugins in the ecosystem can inject their own
actions into the in-game admin menu instead of every action being hardcoded. There are two
kinds of action, both registered through one Shared API:

- **Player actions** — appear in the per-player action menu (after the built-in
  ban/kick/slap/slay/gag/mute/map entries), with a target player.
- **Global actions** — appear in the root admin menu, with no target player.

The contract uses **player slots (`int`)**, not `IGameClient` — pointer-safe across the
plugin boundary and matching the ecosystem event convention. AdminPanel resolves each slot
back to a live `IGameClient` (validating `IsInGame`) on the game thread at invoke time, so
your callback can never receive a stale handle.

### How a 3rd-party plugin registers an action

1. Reference `AdminPanel.Shared` (project reference or the shipped DLL).
2. Resolve `IAdminPanelShared` in your plugin's **`OnAllModulesLoaded`** — AdminPanel
   publishes it in its `PostInit`, and ModSharp guarantees all `PostInit`s finish before any
   `OnAllModulesLoaded` runs. The AdminPanel menu is rebuilt on each `!admin` invocation, so
   actions registered at OAM time are picked up automatically.
3. Call `RegisterPlayerAction` / `RegisterGlobalAction`.
4. Call `Unregister(id)` in your plugin's `Shutdown`.

> **Permissions:** an action's `Permission` (e.g. `"@csrep/lookup"`) gates its visibility
> using the same `IAdmin.HasPermission` check AdminPanel uses for built-in actions. A `null`
> `Permission` makes the action visible to anyone who can open the panel. Register any custom
> permission flags via **your own** plugin's `MountAdminManifest` — AdminManager resolves
> permissions across every plugin's manifest.

### Example — a `CsRepGuard` "csrep lookup" player action

```csharp
using AdminPanel.Shared;
using Sharp.Shared;
using Sharp.Shared.Managers;

public sealed class CsRepGuardPlugin : IModSharpModule
{
    private ISharpModuleManager _modules = /* from ISharedSystem.GetSharpModuleManager() */;
    private IAdminPanelShared?  _adminPanel;

    private const string ActionId = "csrepguard.lookup";

    public void OnAllModulesLoaded()
    {
        // AdminPanel published IAdminPanelShared in its PostInit; safe to resolve now.
        _adminPanel = _modules
            .GetOptionalSharpModuleInterface<IAdminPanelShared>(IAdminPanelShared.Identity)
            ?.Instance;

        if (_adminPanel is null)
            return; // AdminPanel not installed — degrade gracefully.

        _adminPanel.RegisterPlayerAction(new AdminPanelPlayerAction
        {
            Id         = ActionId,
            Label      = "CS Rep Lookup",
            Permission = "@csrep/lookup",   // gated; register this flag in your own manifest
            SortOrder  = 100,
            OnSelected = (adminSlot, targetSlot) =>
            {
                // Runs on the game thread. Both slots are validated in-game by AdminPanel.
                // Resolve targetSlot -> your data and print to the admin, open a sub-menu, etc.
                LookupAndReport(adminSlot, targetSlot);
            },
        });

        // A dynamic-label global action example:
        _adminPanel.RegisterGlobalAction(new AdminPanelGlobalAction
        {
            Id         = "csrepguard.toggle",
            Label      = "Toggle CSRep enforcement",
            Permission = "@csrep/admin",
            SortOrder  = 200,
            OnSelected = adminSlot => ToggleEnforcement(adminSlot),
        });
    }

    public void Shutdown()
    {
        _adminPanel?.Unregister(ActionId);
        _adminPanel?.Unregister("csrepguard.toggle");
    }
}
```

`AdminPanelPlayerAction` also supports a dynamic label via
`LabelFactory = adminSlot => ...` (takes precedence over `Label`), useful for showing live
state (e.g. an on/off toggle) in the menu entry text.
