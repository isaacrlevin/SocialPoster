using MudBlazor;

namespace SocialPoster.Components;

public static class AppTheme
{
    public static readonly MudTheme Theme = new()
    {
        PaletteLight = new PaletteLight
        {
            Primary = "#4f46e5",
            Secondary = "#0ea5e9",
            Tertiary = "#ec4899",
            // Page background is noticeably darker than the white card surface, and borders are
            // strong enough to define card edges without relying on hue (colorblind-friendly).
            Background = "#e6e9f4",
            BackgroundGray = "#eef0f8",
            Surface = "#ffffff",
            AppbarBackground = "#ffffff",
            AppbarText = "#1f2340",
            DrawerBackground = "#ffffff",
            DrawerText = "#3b4064",
            DrawerIcon = "#666d96",
            TextPrimary = "#1f2340",
            TextSecondary = "#4f5578",
            LinesDefault = "#c3c8de",
            Divider = "#d3d7e8",
            Success = "#16a34a",
        },
        PaletteDark = new PaletteDark
        {
            Primary = "#8b8bff",
            Secondary = "#38bdf8",
            Tertiary = "#f472b6",
            Background = "#0f1120",
            Surface = "#181b30",
            AppbarBackground = "#181b30",
            AppbarText = "#e8eaf6",
            DrawerBackground = "#14172a",
            DrawerText = "#c4c8e4",
            DrawerIcon = "#8c92bd",
            TextPrimary = "#eceefb",
            TextSecondary = "#a3a9cf",
            LinesDefault = "#2a2e4a",
            Divider = "#2a2e4a",
            Success = "#4ade80",
        },
        LayoutProperties = new LayoutProperties
        {
            DefaultBorderRadius = "12px",
            DrawerWidthLeft = "248px",
        },
        Typography = new Typography
        {
            Default = new DefaultTypography
            {
                FontFamily =
                [
                    "Inter", "Segoe UI Variable", "Segoe UI", "system-ui", "-apple-system", "Roboto", "Helvetica Neue",
                    "Arial", "sans-serif"
                ],
            },
            Button = new ButtonTypography { TextTransform = "none", FontWeight = "600" },
            H4 = new H4Typography { FontWeight = "700" },
            H5 = new H5Typography { FontWeight = "700" },
            H6 = new H6Typography { FontWeight = "650" },
        },
    };
}