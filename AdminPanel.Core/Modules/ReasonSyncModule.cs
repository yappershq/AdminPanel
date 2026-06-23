using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AdminPanel.Configuration;
using AdminPanel.Database;
using Microsoft.Extensions.Logging;

namespace AdminPanel.Modules;

/// <summary>
/// Polls adminpanel_meta.reasons_version and hot-reloads adminpanel_reasons when it bumps.
/// Follows the HexTags RuleSyncModule pattern: version guard + Interlocked tick guard.
/// </summary>
internal sealed class ReasonSyncModule
{
    private readonly AdminPanelConfig                                   _config;
    private readonly AdminPanelDatabase                                 _db;
    private readonly ILogger<ReasonSyncModule>                         _logger;

    // Live in-memory cache: action_type → ordered list of presets.
    private volatile ConcurrentDictionary<string, List<ReasonPreset>>  _cache = new();

    private Timer? _timer;
    private long   _lastVersion;
    private int    _polling; // 0/1 Interlocked guard — prevents overlapping ticks

    public ReasonSyncModule(AdminPanelConfig config, AdminPanelDatabase db, ILogger<ReasonSyncModule> logger)
    {
        _config = config;
        _db     = db;
        _logger = logger;
    }

    /// <summary>Start initial load + polling. Must be called after DB is connected.</summary>
    public void Start()
    {
        if (!_db.IsConnected)
        {
            _logger.LogWarning("[AdminPanel] DB not connected — reason presets disabled");
            return;
        }

        // OAM (caller) is sync — fire initial load as Task, version guard makes it idempotent with first tick.
        _ = Task.Run(InitialLoadAsync);

        var interval = Math.Max(15, _config.PollIntervalSeconds);
        _timer = new Timer(_ => Poll(), null,
            TimeSpan.FromSeconds(interval), TimeSpan.FromSeconds(interval));

        _logger.LogInformation("[AdminPanel] Reason polling every {Sec}s (ServerTag='{Tag}')",
            interval, _config.ServerTag);
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    /// <summary>Get cached presets for the given action type (e.g. "ban", "mute", "gag", "kick").</summary>
    public IReadOnlyList<ReasonPreset> GetPresets(string actionType)
    {
        return _cache.TryGetValue(actionType, out var list) ? list : [];
    }

    private async Task InitialLoadAsync()
    {
        try
        {
            await _db.EnsureSchemaAsync().ConfigureAwait(false);

            if (await _db.CountReasonsAsync().ConfigureAwait(false) == 0)
            {
                await _db.SeedDefaultsAsync().ConfigureAwait(false);
            }

            var presets = await _db.LoadReasonsAsync(_config.ServerTag).ConfigureAwait(false);
            ReplaceCache(presets);
            _lastVersion = await _db.GetVersionAsync().ConfigureAwait(false);

            _logger.LogInformation("[AdminPanel] Loaded {Count} reason presets from DB", presets.Count);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "[AdminPanel] Initial reason load failed");
        }
    }

    private void Poll()
    {
        if (Interlocked.CompareExchange(ref _polling, 1, 0) != 0)
            return; // prior tick still in flight

        _ = PollAsync();
    }

    private async Task PollAsync()
    {
        try
        {
            var version = await _db.GetVersionAsync().ConfigureAwait(false);
            if (version == _lastVersion)
                return; // no change

            var presets = await _db.LoadReasonsAsync(_config.ServerTag).ConfigureAwait(false);
            ReplaceCache(presets);
            _lastVersion = version;

            _logger.LogInformation("[AdminPanel] Reasons reloaded, v{Version} ({Count} presets)",
                version, presets.Count);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "[AdminPanel] Reason poll failed");
        }
        finally
        {
            Interlocked.Exchange(ref _polling, 0);
        }
    }

    private void ReplaceCache(List<ReasonPreset> presets)
    {
        var next = new ConcurrentDictionary<string, List<ReasonPreset>>(StringComparer.OrdinalIgnoreCase);
        foreach (var preset in presets)
        {
            var list = next.GetOrAdd(preset.ActionType, _ => []);
            list.Add(preset);
        }
        // Atomic volatile swap.
        _cache = next;
    }
}
