namespace CircuitRF.Ui.Layout;

/// <summary>
/// Whether a command can run right now, and — when it cannot — why (docs/sonnet-briefs/brief-L1h-scale-and-context-menu.md
/// R-L1h-3: "a command is either disabled with a stated reason, or it does something — never a
/// silent no-op"). One shared type so every menu-scoped command in the Layout Editor answers this
/// question the same way, and the menu/toolbar/any future keyboard binding can never disagree.
/// </summary>
public readonly record struct LayoutCommandAvailability(bool CanExecute, string? DisabledReason)
{
    public static readonly LayoutCommandAvailability Enabled = new(true, null);

    public static LayoutCommandAvailability Disabled(string reason) => new(false, reason);
}
