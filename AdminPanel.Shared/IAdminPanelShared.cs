using System;
using System.Collections.Generic;

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

    /// <summary>
    /// Request free-text input from an admin via chat capture. Prints <paramref name="prompt"/>
    /// to the admin and intercepts their next chat message instead of broadcasting it. The
    /// captured line is parsed per <paramref name="kind"/> and validated:
    /// <list type="bullet">
    ///   <item><see cref="AdminInputKind.Integer"/> — parsed with <c>long.TryParse</c>; if
    ///   <paramref name="min"/>/<paramref name="max"/> are set the value is range-checked.</item>
    ///   <item><see cref="AdminInputKind.String"/> — trimmed and length-capped.</item>
    /// </list>
    /// On a valid value <paramref name="onResult"/> fires on the game thread with the acting
    /// admin slot and the parsed value (boxed: <c>long</c> for Integer, <c>string</c> for String).
    /// Typing a cancel keyword (<c>cancel</c>/<c>abort</c>/<c>!cancel</c>) or letting the request
    /// time out aborts and invokes <paramref name="onCancel"/> (if supplied) on the game thread.
    /// Re-requesting input for the same admin replaces any pending request.
    /// <para>Must be called on the game thread.</para>
    /// </summary>
    /// <param name="adminSlot">Slot of the admin to prompt.</param>
    /// <param name="prompt">Prompt text printed to the admin's chat.</param>
    /// <param name="kind">How to parse the captured chat line.</param>
    /// <param name="onResult">
    /// Fired on the game thread with (adminSlot, value). <c>value</c> is a <c>long</c> for
    /// <see cref="AdminInputKind.Integer"/> and a <c>string</c> for <see cref="AdminInputKind.String"/>.
    /// </param>
    /// <param name="min">Optional inclusive minimum for <see cref="AdminInputKind.Integer"/>.</param>
    /// <param name="max">Optional inclusive maximum for <see cref="AdminInputKind.Integer"/>.</param>
    /// <param name="timeoutSeconds">Seconds before the pending request auto-cancels.</param>
    /// <param name="onCancel">Fired on the game thread (with the admin slot) on cancel/timeout.</param>
    void RequestInput(
        int                   adminSlot,
        string                prompt,
        AdminInputKind        kind,
        Action<int, object>   onResult,
        long?                 min            = null,
        long?                 max            = null,
        int                   timeoutSeconds = 30,
        Action<int>?          onCancel       = null);
}

/// <summary>Parse mode for a chat-captured admin input request.</summary>
public enum AdminInputKind
{
    /// <summary>Whole number; result boxed as <c>long</c>, optionally range-checked.</summary>
    Integer,

    /// <summary>Free text; result boxed as <c>string</c> (trimmed, length-capped).</summary>
    String,
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
    /// Optional category used to group this action in the menu. When null/empty, the action
    /// falls into the default external category ("Plugins"). Built-in categories an external
    /// action may opt into include <c>"Punish"</c>, <c>"Player"</c>, <c>"Teleport"</c>,
    /// <c>"Server"</c>, and <c>"Fun"</c>; any other value creates a new category bucket.
    /// </summary>
    public string? Category { get; init; }

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
    /// Optional category used to group this action in the root menu. When null/empty, the
    /// action falls into the default external category ("Plugins"). Built-in categories an
    /// external action may opt into include <c>"Server"</c> and <c>"Fun"</c>; any other value
    /// creates a new category bucket.
    /// </summary>
    public string? Category { get; init; }

    /// <summary>
    /// Invoked on the game thread when the action is selected. Ignored when
    /// <see cref="SubMenu"/> is set. Optional: an action may be submenu-only.
    /// Argument: acting admin slot (validated in-game by AdminPanel before this fires).
    /// </summary>
    public Action<int>? OnSelected { get; init; }

    /// <summary>
    /// Optional nested sub-menu. When set, selecting this action opens another menu level
    /// <b>rendered inside AdminPanel's own menu stack</b> (so Back/Exit work and the menu is
    /// not stomped by the selection-close race that hits a module opening its own MenuManager
    /// menu). Resolved with the acting admin slot each time the row is opened. Takes precedence
    /// over <see cref="OnSelected"/>.
    /// </summary>
    public Func<int, IReadOnlyList<AdminPanelMenuItem>>? SubMenu { get; init; }
}

/// <summary>
/// Pure-data descriptor for one row of an external action's nested sub-menu. AdminPanel
/// translates these into its own menu so navigation stays consistent. Kept dependency-free
/// (System only) to preserve the pure <see cref="IAdminPanelShared"/> contract — external
/// modules describe the menu as data instead of handing over a MenuManager menu.
/// </summary>
public sealed class AdminPanelMenuItem
{
    /// <summary>Row label.</summary>
    public required string Label { get; init; }

    /// <summary>
    /// Leaf action invoked on the game thread with the acting admin slot. The menu is closed
    /// before this fires (matches built-in action behaviour). Ignored when <see cref="SubMenu"/> is set.
    /// </summary>
    public Action<int>? OnSelected { get; init; }

    /// <summary>
    /// Nested level: when set, selecting this row opens another sub-menu (with a working Back).
    /// Resolved with the acting admin slot when opened. Takes precedence over <see cref="OnSelected"/>.
    /// </summary>
    public Func<int, IReadOnlyList<AdminPanelMenuItem>>? SubMenu { get; init; }
}
