using System;

namespace AdminPanel.Shared;

/// <summary>
/// Public contract published by AdminPanel via <c>ISharpModuleManager</c> in PostInit.
/// External modules resolve it in their <c>OnAllModulesLoaded</c> (after all PostInits
/// have run) and inject their own actions into the in-game admin menu.
/// <para>
/// Slots are used instead of <c>IGameClient</c> in this contract — pointer-safe across
/// the plugin boundary and matching the ecosystem event convention. AdminPanel resolves
/// the slot to an <c>IGameClient</c> at invoke time on the game thread.
/// </para>
/// </summary>
public interface IAdminPanelShared
{
    /// <summary>Identity for <c>SharpModuleManager.GetOptionalSharpModuleInterface</c>.</summary>
    public const string Identity = nameof(IAdminPanelShared);

    /// <summary>
    /// Register a player-targeted action. It appears in the per-player action menu
    /// (after the built-in ban/kick/slap/slay/gag/mute/map actions), permission-gated
    /// against the acting admin. Replaces any existing action with the same
    /// <see cref="AdminPanelPlayerAction.Id"/>.
    /// </summary>
    void RegisterPlayerAction(AdminPanelPlayerAction action);

    /// <summary>
    /// Register a global action (no target player). It appears in the root admin menu,
    /// permission-gated against the acting admin. Replaces any existing action with the
    /// same <see cref="AdminPanelGlobalAction.Id"/>.
    /// </summary>
    void RegisterGlobalAction(AdminPanelGlobalAction action);

    /// <summary>Remove a previously registered player- or global-action by id.</summary>
    void Unregister(string actionId);
}

/// <summary>
/// Descriptor for an action that targets a specific player. Surfaced in the per-player
/// action menu.
/// </summary>
public sealed class AdminPanelPlayerAction
{
    /// <summary>Stable unique id (used for replace / <see cref="IAdminPanelShared.Unregister"/>).</summary>
    public required string Id { get; init; }

    /// <summary>
    /// Static menu label. Ignored when <see cref="LabelFactory"/> is set.
    /// </summary>
    public string? Label { get; init; }

    /// <summary>
    /// Optional dynamic label resolved per render with the acting admin's slot.
    /// Takes precedence over <see cref="Label"/>.
    /// </summary>
    public Func<int, string>? LabelFactory { get; init; }

    /// <summary>
    /// Admin flag gating visibility (e.g. <c>"@csrep/lookup"</c>). When null, the action
    /// is visible to anyone who can open the panel.
    /// </summary>
    public string? Permission { get; init; }

    /// <summary>Sort order within the appended action block (ascending, then by label).</summary>
    public int SortOrder { get; init; }

    /// <summary>
    /// Invoked on the game thread when the action is selected.
    /// Arguments: acting admin slot, target player slot. Both are validated in-game
    /// by AdminPanel before this fires.
    /// </summary>
    public required Action<int, int> OnSelected { get; init; }

    /// <summary>
    /// Resolves the display label for a given acting admin slot:
    /// <see cref="LabelFactory"/> if set, else <see cref="Label"/>, else <see cref="Id"/>.
    /// </summary>
    public string ResolveLabel(int adminSlot)
        => LabelFactory?.Invoke(adminSlot) ?? Label ?? Id;
}

/// <summary>
/// Descriptor for an action with no target player. Surfaced in the root admin menu.
/// </summary>
public sealed class AdminPanelGlobalAction
{
    /// <summary>Stable unique id (used for replace / <see cref="IAdminPanelShared.Unregister"/>).</summary>
    public required string Id { get; init; }

    /// <summary>Menu label.</summary>
    public required string Label { get; init; }

    /// <summary>
    /// Admin flag gating visibility. When null, the action is visible to anyone who can
    /// open the panel.
    /// </summary>
    public string? Permission { get; init; }

    /// <summary>Sort order within the global-action block (ascending, then by label).</summary>
    public int SortOrder { get; init; }

    /// <summary>
    /// Invoked on the game thread when the action is selected.
    /// Argument: acting admin slot (validated in-game by AdminPanel before this fires).
    /// </summary>
    public required Action<int> OnSelected { get; init; }
}
