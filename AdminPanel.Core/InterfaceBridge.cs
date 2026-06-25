using Microsoft.Extensions.Logging;
using Sharp.Modules.AdminCommands.Shared;
using Sharp.Modules.AdminManager.Shared;
using Sharp.Modules.MenuManager.Shared;
using Sharp.Modules.TargetingManager.Shared;
using Sharp.Shared;
using Sharp.Shared.Managers;

namespace AdminPanel;

/// <summary>
/// Holds all injected system references and optional-module handles.
/// Resolved lazily in OnAllModulesLoaded — never in Init/PostInit.
/// </summary>
internal sealed class InterfaceBridge
{
    internal IModSharpModule      Module             { get; }
    internal IModSharp            ModSharp           { get; }
    internal IClientManager       ClientManager      { get; }
    internal ISharpModuleManager  SharpModuleManager { get; }
    internal ILoggerFactory       LoggerFactory      { get; }

    // Resolved in OnAllModulesLoaded.
    internal IAdminService?     AdminService     { get; private set; }
    internal IAdminManager?     AdminManager     { get; private set; }
    internal IMenuManager?      MenuManager      { get; private set; }
    internal ITargetingManager? TargetingManager { get; private set; }

    public InterfaceBridge(IModSharpModule module, ISharedSystem sharedSystem)
    {
        Module             = module;
        ModSharp           = sharedSystem.GetModSharp();
        ClientManager      = sharedSystem.GetClientManager();
        SharpModuleManager = sharedSystem.GetSharpModuleManager();
        LoggerFactory      = sharedSystem.GetLoggerFactory();
    }

    internal void ResolveModules()
    {
        AdminService = SharpModuleManager
            .GetOptionalSharpModuleInterface<IAdminService>(IAdminService.Identity)?.Instance;
        AdminManager = SharpModuleManager
            .GetOptionalSharpModuleInterface<IAdminManager>(IAdminManager.Identity)?.Instance;
        MenuManager  = SharpModuleManager
            .GetOptionalSharpModuleInterface<IMenuManager>(IMenuManager.Identity)?.Instance;
        TargetingManager = SharpModuleManager
            .GetOptionalSharpModuleInterface<ITargetingManager>(ITargetingManager.Identity)?.Instance;
    }
}
