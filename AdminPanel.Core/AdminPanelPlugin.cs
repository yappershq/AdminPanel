using System;
using System.IO;
using System.Text.Json;
using AdminPanel.Configuration;
using AdminPanel.Database;
using AdminPanel.Modules;
using AdminPanel.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Sharp.Shared;

namespace AdminPanel;

/// <summary>
/// AdminPanel — in-game menu-driven admin tool. Exposes !admin command (gated by
/// adminpanel:open) with a player picker → action → duration → reason flow.
/// </summary>
public sealed class AdminPanelPlugin : IModSharpModule
{
    public string DisplayName   => "AdminPanel";
    public string DisplayAuthor => "yappershq";

    private readonly ILogger<AdminPanelPlugin> _logger;
    private readonly InterfaceBridge           _bridge;
    private readonly AdminPanelConfig          _config;
    private readonly AdminPanelDatabase        _db;
    private readonly ReasonSyncModule          _reasonSync;
    private readonly AdminActionRegistry       _actionRegistry;
    private readonly AdminInputModule          _input;
    private readonly AdminCommandModule        _commands;

    public AdminPanelPlugin(
        ISharedSystem  sharedSystem,
        string         dllPath,
        string         sharpPath,
        Version        version,
        IConfiguration configuration,
        bool           hotReload)
    {
        var loggerFactory = sharedSystem.GetLoggerFactory();
        _logger = loggerFactory.CreateLogger<AdminPanelPlugin>();

        _config = LoadConfig(dllPath);
        _bridge = new InterfaceBridge(this, sharedSystem);
        _db     = new AdminPanelDatabase(loggerFactory.CreateLogger<AdminPanelDatabase>());

        _reasonSync = new ReasonSyncModule(
            _config,
            _db,
            loggerFactory.CreateLogger<ReasonSyncModule>());

        _actionRegistry = new AdminActionRegistry();

        _input = new AdminInputModule(
            _bridge,
            loggerFactory.CreateLogger<AdminInputModule>());

        // Route the public Shared RequestInput through the input service.
        _actionRegistry.AttachInput(_input);

        _commands = new AdminCommandModule(
            _bridge,
            _config,
            _reasonSync,
            _actionRegistry,
            _input,
            loggerFactory.CreateLogger<AdminCommandModule>());
    }

    public bool Init()
    {
        // Connect DB early (sync probe); heavy async work happens in OnAllModulesLoaded.
        _db.Connect(_config.Database);
        return true;
    }

    public void PostInit()
    {
        // Publish the public action-registry contract here (PostInit) so consumers can
        // resolve it in their OnAllModulesLoaded. ModSharp guarantees every plugin's
        // PostInit finishes before any OnAllModulesLoaded runs, so late registrations
        // are safe — the menu is rebuilt on each !admin invocation.
        _bridge.SharpModuleManager.RegisterSharpModuleInterface<IAdminPanelShared>(
            this, IAdminPanelShared.Identity, _actionRegistry);
    }

    public void OnAllModulesLoaded()
    {
        _bridge.ResolveModules();
        _reasonSync.Start();
        _input.Start();
        _commands.Start();

        _logger.LogInformation(
            "[AdminPanel] Loaded (AdminCommands={AC}, AdminManager={AM}, MenuManager={MM}, DB={DB})",
            _bridge.AdminService  is not null,
            _bridge.AdminManager  is not null,
            _bridge.MenuManager   is not null,
            _db.IsConnected);
    }

    public void Shutdown()
    {
        _commands.Stop();
        _input.Stop();
        _reasonSync.Stop();
        _db.Dispose();
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Config loading
    // ──────────────────────────────────────────────────────────────────────────

    private AdminPanelConfig LoadConfig(string dllPath)
    {
        // Assets are deployed alongside the DLL; fall back to defaults on any failure.
        try
        {
            var assetDir    = Path.Combine(Path.GetDirectoryName(dllPath)!, ".assets", "configs");
            var configPath  = Path.Combine(assetDir, "adminpanel.json");

            if (!File.Exists(configPath))
            {
                _logger.LogWarning("[AdminPanel] Config not found at {Path}, using defaults", configPath);
                return new AdminPanelConfig();
            }

            using var stream = File.OpenRead(configPath);
            var cfg = JsonSerializer.Deserialize<AdminPanelConfig>(stream) ?? new AdminPanelConfig();
            _logger.LogInformation("[AdminPanel] Config loaded from {Path}", configPath);
            return cfg;
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "[AdminPanel] Config load failed, using defaults");
            return new AdminPanelConfig();
        }
    }
}
