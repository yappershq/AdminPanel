using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AdminPanel.Configuration;
using Microsoft.Extensions.Logging;
using SqlSugar;

namespace AdminPanel.Database;

/// <summary>
/// SqlSugar-backed DB layer for AdminPanel. Holds one pooled SqlSugarScope (MaxPoolSize=4).
/// All public methods are async, defensive, and run off the game thread.
/// </summary>
internal sealed class AdminPanelDatabase : IDisposable
{
    private readonly ILogger<AdminPanelDatabase> _logger;
    private SqlSugarScope?                        _db;

    public bool IsConnected => _db is not null;

    public AdminPanelDatabase(ILogger<AdminPanelDatabase> logger) => _logger = logger;

    public bool Connect(DatabaseConfig cfg)
    {
        try
        {
            var connStr =
                $"Server={cfg.Host};Port={cfg.Port};Database={cfg.Database};" +
                $"User={cfg.User};Password={cfg.Password};" +
                $"AllowPublicKeyRetrieval=true;SslMode=Preferred;" +
                $"Maximum Pool Size={cfg.MaxPoolSize};Minimum Pool Size=0;";

            _db = new SqlSugarScope(new ConnectionConfig
            {
                DbType                = DbType.MySql,
                ConnectionString      = connStr,
                IsAutoCloseConnection = true,
                InitKeyType           = InitKeyType.Attribute,
            });

            // Probe so bad config fails loudly at load.
            _ = _db.Ado.GetInt("SELECT 1");
            _logger.LogInformation("[AdminPanel] Connected to DB {Host}/{Db}", cfg.Host, cfg.Database);
            return true;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "[AdminPanel] Failed to connect to DB — reasons will be empty");
            _db = null;
            return false;
        }
    }

    /// <summary>Create tables if they don't exist and seed the version meta row.</summary>
    public async Task EnsureSchemaAsync()
    {
        if (_db is null) return;
        try
        {
            // CodeFirst creates tables from attributes.
            _db.CodeFirst.InitTables<ReasonPreset>();
            _db.CodeFirst.InitTables<MetaRow>();

            // Ensure the version row exists (INSERT IGNORE equivalent).
            await _db.Insertable(new MetaRow { KeyName = "reasons_version", Value = 0 })
                     .InsertColumns("key_name", "value")
                     .ExecuteCommandAsync()
                     .ConfigureAwait(false);
        }
        catch (Exception e) when (IsDuplicateKey(e))
        {
            // Version row already present — expected on all subsequent starts.
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "[AdminPanel] EnsureSchema partially failed");
        }
    }

    public async Task<int> CountReasonsAsync()
    {
        if (_db is null) return 0;
        try
        {
            return await _db.Queryable<ReasonPreset>().CountAsync().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "[AdminPanel] CountReasons failed");
            return 0;
        }
    }

    /// <summary>Seed default reason presets into an empty table.</summary>
    public async Task SeedDefaultsAsync()
    {
        if (_db is null) return;
        try
        {
            var rows = new List<ReasonPreset>
            {
                // Ban reasons
                new() { ActionType = "ban",  Label = "Cheating",      ReasonText = "Cheating",      SortOrder = 1, ServerTag = "all" },
                new() { ActionType = "ban",  Label = "Griefing",      ReasonText = "Griefing",      SortOrder = 2, ServerTag = "all" },
                new() { ActionType = "ban",  Label = "Ban evasion",   ReasonText = "Ban evasion",   SortOrder = 3, ServerTag = "all" },
                new() { ActionType = "ban",  Label = "Toxic behavior",ReasonText = "Toxic behavior",SortOrder = 4, ServerTag = "all" },

                // Mute reasons
                new() { ActionType = "mute", Label = "Mic spam",      ReasonText = "Mic spam",      SortOrder = 1, ServerTag = "all" },
                new() { ActionType = "mute", Label = "Toxicity",      ReasonText = "Toxicity",      SortOrder = 2, ServerTag = "all" },
                new() { ActionType = "mute", Label = "Advertising",   ReasonText = "Advertising",   SortOrder = 3, ServerTag = "all" },

                // Gag reasons
                new() { ActionType = "gag",  Label = "Chat spam",     ReasonText = "Chat spam",     SortOrder = 1, ServerTag = "all" },
                new() { ActionType = "gag",  Label = "Toxicity",      ReasonText = "Toxicity",      SortOrder = 2, ServerTag = "all" },
                new() { ActionType = "gag",  Label = "Advertising",   ReasonText = "Advertising",   SortOrder = 3, ServerTag = "all" },

                // Kick reasons
                new() { ActionType = "kick", Label = "AFK",           ReasonText = "AFK",           SortOrder = 1, ServerTag = "all" },
                new() { ActionType = "kick", Label = "Inappropriate name", ReasonText = "Inappropriate name", SortOrder = 2, ServerTag = "all" },
                new() { ActionType = "kick", Label = "Slot needed",   ReasonText = "Slot needed",   SortOrder = 3, ServerTag = "all" },
            };

            await _db.Insertable(rows).ExecuteCommandAsync().ConfigureAwait(false);

            // Bump version so other servers reload.
            await _db.Updateable<MetaRow>()
                     .SetColumns(r => r.Value == r.Value + 1)
                     .Where(r => r.KeyName == "reasons_version")
                     .ExecuteCommandAsync()
                     .ConfigureAwait(false);

            _logger.LogInformation("[AdminPanel] Seeded {Count} default reason presets", rows.Count);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "[AdminPanel] SeedDefaults failed");
        }
    }

    /// <summary>Load enabled reasons for this server (tag 'all' or matching serverTag).</summary>
    public async Task<List<ReasonPreset>> LoadReasonsAsync(string serverTag)
    {
        if (_db is null) return [];
        try
        {
            return await _db.Queryable<ReasonPreset>()
                            .Where(r => r.Enabled == 1 && (r.ServerTag == "all" || r.ServerTag == serverTag))
                            .OrderBy(r => r.ActionType)
                            .OrderBy(r => r.SortOrder)
                            .ToListAsync()
                            .ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "[AdminPanel] LoadReasons failed");
            return [];
        }
    }

    /// <summary>Read the current reasons_version value.</summary>
    public async Task<long> GetVersionAsync()
    {
        if (_db is null) return -1;
        try
        {
            var row = await _db.Queryable<MetaRow>()
                               .Where(r => r.KeyName == "reasons_version")
                               .FirstAsync()
                               .ConfigureAwait(false);
            return row?.Value ?? 0;
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "[AdminPanel] GetVersion failed");
            return -1;
        }
    }

    private static bool IsDuplicateKey(Exception e) =>
        e.Message.Contains("Duplicate entry", StringComparison.OrdinalIgnoreCase) ||
        e.Message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        _db?.Dispose();
        _db = null;
    }
}
