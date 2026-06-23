using System;
using AdminPanel.Shared;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
using Sharp.Shared.Listeners;
using Sharp.Shared.Objects;
using Sharp.Shared.Units;

namespace AdminPanel.Modules;

/// <summary>
/// Chat-capture input primitive. Holds a per-admin pending-input table (slot-indexed,
/// not a Dictionary, per ecosystem convention) and intercepts the admin's next chat
/// message via <see cref="IClientListener.OnClientSayCommand"/>.
/// <para>
/// When an action calls <see cref="RequestInput"/>, the admin is prompted and marked
/// pending. Their next say is consumed (not broadcast), parsed and validated per
/// <see cref="AdminInputKind"/>, and the result delivered on the game thread. A
/// non-repeatable timer cancels the request after the timeout; cancel keywords abort.
/// </para>
/// </summary>
internal sealed class AdminInputModule : IClientListener
{
    private const int MaxStringLength = 256;

    private static readonly string[] CancelKeywords = ["cancel", "abort", "!cancel"];

    private readonly InterfaceBridge            _bridge;
    private readonly ILogger<AdminInputModule>  _logger;

    // Slot-indexed pending table — index by (int) client.Slot.AsPrimitive().
    private readonly PendingInput?[] _pending = new PendingInput?[64];

    private bool _installed;

    public AdminInputModule(InterfaceBridge bridge, ILogger<AdminInputModule> logger)
    {
        _bridge = bridge;
        _logger = logger;
    }

    int IClientListener.ListenerVersion  => IClientListener.ApiVersion;
    int IClientListener.ListenerPriority => 0;

    // ──────────────────────────────────────────────────────────────────────────
    // Lifecycle
    // ──────────────────────────────────────────────────────────────────────────

    public void Start()
    {
        if (_installed)
            return;

        _bridge.ClientManager.InstallClientListener(this);
        _installed = true;
    }

    public void Stop()
    {
        if (_installed)
        {
            _bridge.ClientManager.RemoveClientListener(this);
            _installed = false;
        }

        // Drop any pending requests and their timers.
        for (var i = 0; i < _pending.Length; i++)
        {
            var pending = _pending[i];
            if (pending is null)
                continue;

            if (_bridge.ModSharp.IsValidTimer(pending.TimeoutTimer))
                _bridge.ModSharp.StopTimer(pending.TimeoutTimer);

            _pending[i] = null;
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Public request entry (called on the game thread)
    // ──────────────────────────────────────────────────────────────────────────

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

        if (adminSlot is < 0 or >= 64)
        {
            _logger.LogWarning("[AdminPanel] RequestInput with out-of-range slot {Slot}", adminSlot);
            return;
        }

        var admin = _bridge.ClientManager.GetGameClient((PlayerSlot) (byte) adminSlot);
        if (admin is not { IsInGame: true })
        {
            _logger.LogWarning("[AdminPanel] RequestInput for slot {Slot} — admin not in game", adminSlot);
            return;
        }

        // Replace any existing pending request for this admin (stop its timer first).
        var existing = _pending[adminSlot];
        if (existing is not null && _bridge.ModSharp.IsValidTimer(existing.TimeoutTimer))
            _bridge.ModSharp.StopTimer(existing.TimeoutTimer);
        _pending[adminSlot] = null;

        var seconds = timeoutSeconds <= 0 ? 30 : timeoutSeconds;

        // One-shot timeout. StopOnMapEnd so a stale prompt can't survive a map change.
        var timer = _bridge.ModSharp.PushTimer(
            () => OnTimeout(adminSlot),
            seconds,
            GameTimerFlags.StopOnMapEnd);

        _pending[adminSlot] = new PendingInput
        {
            Kind         = kind,
            Min          = min,
            Max          = max,
            OnResult     = onResult,
            OnCancel     = onCancel,
            TimeoutTimer = timer,
        };

        admin.Print(HudPrintChannel.Chat, $" [AdminPanel] {prompt}");
        admin.Print(HudPrintChannel.Chat, " [AdminPanel] Type your answer in chat, or 'cancel' to abort.");
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Chat interception (game thread)
    // ──────────────────────────────────────────────────────────────────────────

    // Drop any pending input for a slot when its occupant leaves, so a leaked timer can't
    // fire a stale callback against a new player who later takes the same slot.
    void IClientListener.OnClientDisconnected(IGameClient client, NetworkDisconnectionReason reason)
    {
        var slot = (int) client.Slot.AsPrimitive();
        if (slot is < 0 or >= 64)
            return;

        var pending = _pending[slot];
        if (pending is null)
            return;

        _pending[slot] = null;
        if (_bridge.ModSharp.IsValidTimer(pending.TimeoutTimer))
            _bridge.ModSharp.StopTimer(pending.TimeoutTimer);
    }

    ECommandAction IClientListener.OnClientSayCommand(
        IGameClient client,
        bool        teamOnly,
        bool        isCommand,
        string      commandName,
        string      message)
    {
        var slot = (int) client.Slot.AsPrimitive();
        if (slot is < 0 or >= 64)
            return ECommandAction.Skipped;

        var pending = _pending[slot];
        if (pending is null)
            return ECommandAction.Skipped; // not capturing — leave normal chat untouched

        // Consume this line regardless of outcome; clear state + cancel the timeout.
        _pending[slot] = null;
        if (_bridge.ModSharp.IsValidTimer(pending.TimeoutTimer))
            _bridge.ModSharp.StopTimer(pending.TimeoutTimer);

        var text = message?.Trim() ?? string.Empty;

        // Cancel keyword?
        foreach (var kw in CancelKeywords)
        {
            if (text.Equals(kw, StringComparison.OrdinalIgnoreCase))
            {
                DispatchCancel(slot, pending, " [AdminPanel] Input cancelled.");
                return ECommandAction.Stopped;
            }
        }

        switch (pending.Kind)
        {
            case AdminInputKind.Integer:
            {
                if (!long.TryParse(text, out var value))
                {
                    DispatchCancel(slot, pending, $" [AdminPanel] '{text}' is not a whole number. Cancelled.");
                    return ECommandAction.Stopped;
                }

                if (pending.Min is { } lo && value < lo)
                {
                    DispatchCancel(slot, pending, $" [AdminPanel] Value must be at least {lo}. Cancelled.");
                    return ECommandAction.Stopped;
                }

                if (pending.Max is { } hi && value > hi)
                {
                    DispatchCancel(slot, pending, $" [AdminPanel] Value must be at most {hi}. Cancelled.");
                    return ECommandAction.Stopped;
                }

                DispatchResult(slot, pending, value);
                return ECommandAction.Stopped;
            }

            case AdminInputKind.String:
            {
                if (text.Length == 0)
                {
                    DispatchCancel(slot, pending, " [AdminPanel] Empty input. Cancelled.");
                    return ECommandAction.Stopped;
                }

                if (text.Length > MaxStringLength)
                    text = text[..MaxStringLength];

                DispatchResult(slot, pending, text);
                return ECommandAction.Stopped;
            }

            default:
                DispatchCancel(slot, pending, " [AdminPanel] Input cancelled.");
                return ECommandAction.Stopped;
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Dispatch helpers (re-resolve live admin on the game thread before firing)
    // ──────────────────────────────────────────────────────────────────────────

    private void DispatchResult(int adminSlot, PendingInput pending, object value)
    {
        _bridge.ModSharp.InvokeFrameAction(() =>
        {
            var admin = _bridge.ClientManager.GetGameClient((PlayerSlot) (byte) adminSlot);
            if (admin is not { IsInGame: true })
                return;

            try
            {
                pending.OnResult(adminSlot, value);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "[AdminPanel] Input result callback threw");
            }
        });
    }

    private void DispatchCancel(int adminSlot, PendingInput pending, string message)
    {
        _bridge.ModSharp.InvokeFrameAction(() =>
        {
            var admin = _bridge.ClientManager.GetGameClient((PlayerSlot) (byte) adminSlot);
            if (admin is { IsInGame: true })
                admin.Print(HudPrintChannel.Chat, message);

            if (pending.OnCancel is null)
                return;

            try
            {
                pending.OnCancel(adminSlot);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "[AdminPanel] Input cancel callback threw");
            }
        });
    }

    private void OnTimeout(int adminSlot)
    {
        if (adminSlot is < 0 or >= 64)
            return;

        var pending = _pending[adminSlot];
        if (pending is null)
            return; // already resolved / cancelled

        _pending[adminSlot] = null;
        DispatchCancel(adminSlot, pending, " [AdminPanel] Input timed out.");
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Pending entry
    // ──────────────────────────────────────────────────────────────────────────

    private sealed class PendingInput
    {
        public required AdminInputKind      Kind         { get; init; }
        public required long?               Min          { get; init; }
        public required long?               Max          { get; init; }
        public required Action<int, object> OnResult     { get; init; }
        public required Action<int>?        OnCancel     { get; init; }
        public required Guid                TimeoutTimer { get; init; }
    }
}
