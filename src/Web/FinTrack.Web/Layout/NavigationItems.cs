using Microsoft.AspNetCore.Components.Routing;
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
        new("", "Nav.Overview", Icons.Material.Outlined.Dashboard, NavLinkMatch.All),
        new("buckets", "Nav.Buckets", Icons.Material.Outlined.Savings, NavLinkMatch.Prefix),
        new("settings", "Nav.Settings", Icons.Material.Outlined.Settings, NavLinkMatch.Prefix),
    ];
}

/// <summary>One navigation entry.</summary>
/// <param name="Href">The page address, relative to the base address (empty for the start page).</param>
/// <param name="LabelKey">The resource key of the label.</param>
/// <param name="Icon">The Material icon.</param>
/// <param name="Match">Whether the item is active only on its own page or also on the pages under it (Settings and its status page).</param>
public sealed record NavigationItem(string Href, string LabelKey, string Icon, NavLinkMatch Match);
