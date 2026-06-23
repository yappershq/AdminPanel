using System;
using System.Collections.Generic;
using System.Linq;
using AdminPanel.Shared;

namespace AdminPanel.Modules;

/// <summary>
/// Thread-safe registry of externally-registered admin actions, implementing the public
/// <see cref="IAdminPanelShared"/> contract.
/// <para>
/// Registrations arrive during consumers' <c>OnAllModulesLoaded</c> (which runs after every
/// plugin's PostInit). Because the admin menu is built lazily per <c>!admin</c> invocation,
/// late registrations are naturally picked up. Reads snapshot the dictionaries under lock.
/// </para>
/// </summary>
internal sealed class AdminActionRegistry : IAdminPanelShared
{
    private readonly object                                       _gate    = new();
    private readonly Dictionary<string, AdminPanelPlayerAction>   _players = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AdminPanelGlobalAction>   _globals = new(StringComparer.Ordinal);

    public void RegisterPlayerAction(AdminPanelPlayerAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentException.ThrowIfNullOrEmpty(action.Id);
        ArgumentNullException.ThrowIfNull(action.OnSelected);

        lock (_gate)
            _players[action.Id] = action;
    }

    public void RegisterGlobalAction(AdminPanelGlobalAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentException.ThrowIfNullOrEmpty(action.Id);
        ArgumentNullException.ThrowIfNull(action.OnSelected);

        lock (_gate)
            _globals[action.Id] = action;
    }

    public void Unregister(string actionId)
    {
        if (string.IsNullOrEmpty(actionId))
            return;

        lock (_gate)
        {
            _players.Remove(actionId);
            _globals.Remove(actionId);
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Internal read access (game thread, menu build)
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>Ordered snapshot of registered player-actions (SortOrder, then Id).</summary>
    internal IReadOnlyList<AdminPanelPlayerAction> GetPlayerActions(int adminSlot)
    {
        lock (_gate)
        {
            return _players.Values
                .OrderBy(a => a.SortOrder)
                .ThenBy(a => a.ResolveLabel(adminSlot), StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    /// <summary>Ordered snapshot of registered global-actions (SortOrder, then Label).</summary>
    internal IReadOnlyList<AdminPanelGlobalAction> GetGlobalActions()
    {
        lock (_gate)
        {
            return _globals.Values
                .OrderBy(a => a.SortOrder)
                .ThenBy(a => a.Label, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}
