using MudBlazor;

namespace FinTrack.Web.Layout;

/// <summary>
/// The top-level pages, shown in the drawer (desktop) and the bottom navigation (mobile). Only pages
/// that exist are listed; DESIGN 8 gives the full order as they arrive: Overview, Buckets, Plans,
/// Loans, Investments, Journal, Settings.
/// </summary>
public static class NavigationItems
{
    /// <summary>All items, in order.</summary>
    public static IReadOnlyList<NavigationItem> All { get; } =
    [
        new(string.Empty, "Nav.Status", Icons.Material.Outlined.MonitorHeart),
    ];
}

/// <summary>One navigation entry.</summary>
/// <param name="Href">The page address, relative to the base address (empty for the start page).</param>
/// <param name="LabelKey">The resource key of the label.</param>
/// <param name="Icon">The Material icon.</param>
public sealed record NavigationItem(string Href, string LabelKey, string Icon);
