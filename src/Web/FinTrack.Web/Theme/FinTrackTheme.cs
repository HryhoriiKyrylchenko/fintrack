using MudBlazor;

namespace FinTrack.Web.Theme;

/// <summary>
/// The "Indigo" theme of docs/DESIGN.md section 3: the only place where colours, fonts and radii
/// are written down. Components use the theme (MudBlazor parameters or its CSS variables), never
/// their own values. Switching theme means changing these tokens only.
/// </summary>
public static class FinTrackTheme
{
    // Inter is self-hosted (wwwroot/fonts, OFL-1.1); the system stack is the fallback.
    private static readonly string[] FontStack =
        ["Inter", "system-ui", "-apple-system", "Segoe UI", "Roboto", "Helvetica Neue", "Arial", "sans-serif"];

    private const string Regular = "400";
    private const string SemiBold = "600";

    /// <summary>The application theme with its light and dark palettes.</summary>
    public static MudTheme Theme { get; } = new()
    {
        PaletteLight = new PaletteLight
        {
            Primary = "#4F46E5",
            PrimaryContrastText = "#FFFFFF",

            // Primary is the only brand colour (DESIGN 3): "secondary" is the neutral secondary-text
            // grey, so Color.Secondary means muted text, never a second accent.
            Secondary = "#64748B",
            SecondaryContrastText = "#FFFFFF",
            Tertiary = "#64748B",
            Background = "#F8FAFC",
            BackgroundGray = "#F1F5F9",
            Surface = "#FFFFFF",
            AppbarBackground = "#FFFFFF",
            AppbarText = "#0F172A",
            DrawerBackground = "#FFFFFF",
            DrawerText = "#0F172A",
            DrawerIcon = "#64748B",
            TextPrimary = "#0F172A",
            TextSecondary = "#64748B",
            ActionDefault = "#64748B",
            Divider = "#E2E8F0",
            DividerLight = "#E2E8F0",
            LinesDefault = "#E2E8F0",
            LinesInputs = "#E2E8F0",
            TableLines = "#E2E8F0",
            TableHover = "#F1F5F9",
            Skeleton = "#F1F5F9",
            Success = "#16A34A",
            Error = "#DC2626",
            Warning = "#D97706",
            Info = "#0284C7",
        },
        PaletteDark = new PaletteDark
        {
            Primary = "#818CF8",
            PrimaryContrastText = "#0B1020",
            Secondary = "#94A3B8",
            SecondaryContrastText = "#0B1020",
            Tertiary = "#94A3B8",
            Background = "#0B1020",
            BackgroundGray = "#1E2640",
            Surface = "#151B2E",
            AppbarBackground = "#151B2E",
            AppbarText = "#E5E7EB",
            DrawerBackground = "#151B2E",
            DrawerText = "#E5E7EB",
            DrawerIcon = "#94A3B8",
            TextPrimary = "#E5E7EB",
            TextSecondary = "#94A3B8",
            ActionDefault = "#94A3B8",
            Divider = "#273049",
            DividerLight = "#273049",
            LinesDefault = "#273049",
            LinesInputs = "#273049",
            TableLines = "#273049",
            TableHover = "#1E2640",
            Skeleton = "#1E2640",
            Success = "#4ADE80",
            Error = "#F87171",
            Warning = "#FBBF24",
            Info = "#38BDF8",
        },

        // DESIGN 5: page title 24, section title 18, body 14 to 16, caption 12; weights 400 and 600.
        Typography = new Typography
        {
            Default = new DefaultTypography { FontFamily = FontStack, FontWeight = Regular, FontSize = "0.875rem" },
            H5 = new H5Typography { FontFamily = FontStack, FontWeight = SemiBold, FontSize = "1.5rem", LineHeight = "1.33" },
            H6 = new H6Typography { FontFamily = FontStack, FontWeight = SemiBold, FontSize = "1.125rem", LineHeight = "1.4" },
            Subtitle1 = new Subtitle1Typography { FontFamily = FontStack, FontWeight = SemiBold, FontSize = "1rem" },
            Subtitle2 = new Subtitle2Typography { FontFamily = FontStack, FontWeight = SemiBold, FontSize = "0.875rem" },
            Body1 = new Body1Typography { FontFamily = FontStack, FontWeight = Regular, FontSize = "1rem" },
            Body2 = new Body2Typography { FontFamily = FontStack, FontWeight = Regular, FontSize = "0.875rem" },
            Button = new ButtonTypography { FontFamily = FontStack, FontWeight = SemiBold, FontSize = "0.875rem", TextTransform = "none" },
            Caption = new CaptionTypography { FontFamily = FontStack, FontWeight = Regular, FontSize = "0.75rem" },
        },

        // DESIGN 8: 8 px on controls (cards get 12 px in app.css).
        LayoutProperties = new LayoutProperties { DefaultBorderRadius = "8px" },
    };
}
