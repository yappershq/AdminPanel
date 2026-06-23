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

    // Set after construction (the input module is built alongside this registry). The
    // Shared RequestInput entry point delegates to it; null until wired (degrades safely).
    private AdminInputModule? _input;

    /// <summary>Wire the chat-capture input service. Called once during plugin construction.</summary>
    internal void AttachInput(AdminInputModule input) => _input = input;

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

    public void RequestInput(
        int                 adminSlot,
        string              prompt,
        AdminInputKind      kind,
        Action<int, object> onResult,
        long?               min            = null,
        long?               max            = null,
        int                 timeoutSeconds = 30,
        Action<int>?        onCancel       = null)
    {
        ArgumentNullException.ThrowIfNull(onResult);

        // Delegates to the chat-capture input service (owns the IClientListener + pending
        // table). Must be invoked on the game thread (documented on the contract).
        _input?.RequestInput(adminSlot, prompt, kind, onResult, min, max, timeoutSeconds, onCancel);
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
