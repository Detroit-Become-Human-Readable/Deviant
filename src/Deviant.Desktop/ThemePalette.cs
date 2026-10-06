using Microsoft.Win32;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace Deviant.Desktop;

internal static class ThemePalette
{
    private static Application? application;
    private static bool? lightOverride;

    public static void Initialize(Application app, bool? forceLight = null)
    {
        application = app;
        lightOverride = forceLight;
        Apply();
        SystemEvents.UserPreferenceChanged += UserPreferenceChanged;
    }

    public static void Dispose()
    {
        SystemEvents.UserPreferenceChanged -= UserPreferenceChanged;
        application = null;
        lightOverride = null;
    }

    private static void UserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not UserPreferenceCategory.Color and not UserPreferenceCategory.General) return;
        var app = application;
        if (app is null || app.Dispatcher.HasShutdownStarted) return;
        _ = app.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, (Action)Apply);
    }

    private static void Apply()
    {
        var app = application;
        if (app is null) return;

        if (SystemParameters.HighContrast)
        {
            Set(app, SystemColors.WindowBrush, SystemColors.WindowTextBrush,
                SystemColors.WindowBrush, SystemColors.ControlBrush, SystemColors.ControlBrush,
                SystemColors.ControlTextBrush, SystemColors.ActiveBorderBrush, SystemColors.ControlBrush,
                SystemColors.HighlightBrush, SystemColors.ControlBrush, SystemColors.GrayTextBrush,
                SystemColors.HighlightBrush, SystemColors.HighlightTextBrush, SystemColors.HighlightBrush,
                SystemColors.WindowFrameBrush);
            return;
        }

        if (IsLightMode())
        {
            Set(app, Color.FromRgb(0xF8, 0xF8, 0xF8), Color.FromRgb(0x1C, 0x1C, 0x1C),
                Color.FromRgb(0xFF, 0xFF, 0xFF), Color.FromRgb(0xF2, 0xF4, 0xF7),
                Color.FromRgb(0xFF, 0xFF, 0xFF), Color.FromRgb(0x1C, 0x1C, 0x1C),
                Color.FromRgb(0xD1, 0xD5, 0xDB), Color.FromRgb(0xE9, 0xEE, 0xF5),
                Color.FromRgb(0xDE, 0xE7, 0xF5), Color.FromRgb(0xF1, 0xF1, 0xF1),
                Color.FromRgb(0x75, 0x75, 0x75), Color.FromRgb(0xC8, 0xE0, 0xF8),
                Color.FromRgb(0x10, 0x20, 0x30), Color.FromRgb(0x00, 0x67, 0xC0),
                Color.FromRgb(0x00, 0x67, 0xC0));
        }
        else
        {
            Set(app, Color.FromRgb(0x20, 0x20, 0x20), Color.FromRgb(0xF5, 0xF5, 0xF5),
                Color.FromRgb(0x2A, 0x2A, 0x2A), Color.FromRgb(0x32, 0x32, 0x32),
                Color.FromRgb(0x34, 0x34, 0x34), Color.FromRgb(0xF5, 0xF5, 0xF5),
                Color.FromRgb(0x4A, 0x4A, 0x4A), Color.FromRgb(0x41, 0x41, 0x41),
                Color.FromRgb(0x49, 0x49, 0x49), Color.FromRgb(0x2B, 0x2B, 0x2B),
                Color.FromRgb(0x9A, 0x9A, 0x9A), Color.FromRgb(0x35, 0x5D, 0x83),
                Color.FromRgb(0xFF, 0xFF, 0xFF), Color.FromRgb(0x78, 0xA9, 0xFF),
                Color.FromRgb(0x78, 0xA9, 0xFF));
        }
    }

    private static bool IsLightMode()
    {
        if (lightOverride is { } overrideValue) return overrideValue;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is not int value || value != 0;
        }
        catch (System.Security.SecurityException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    private static void Set(Application app, object windowBackground, object windowForeground,
        object surfaceBackground, object surfaceAlternateBackground, object controlBackground,
        object controlForeground, object controlBorder, object controlHover, object controlPressed,
        object controlDisabledBackground, object controlDisabledForeground, object selectionBackground,
        object selectionForeground, object accent, object gridLine)
    {
        var resources = app.Resources;
        resources["WindowBackground"] = AsBrush(windowBackground);
        resources["WindowForeground"] = AsBrush(windowForeground);
        resources["SurfaceBackground"] = AsBrush(surfaceBackground);
        resources["SurfaceAlternateBackground"] = AsBrush(surfaceAlternateBackground);
        resources["ControlBackground"] = AsBrush(controlBackground);
        resources["ControlForeground"] = AsBrush(controlForeground);
        resources["ControlBorder"] = AsBrush(controlBorder);
        resources["ControlHoverBackground"] = AsBrush(controlHover);
        resources["ControlPressedBackground"] = AsBrush(controlPressed);
        resources["ControlDisabledBackground"] = AsBrush(controlDisabledBackground);
        resources["ControlDisabledForeground"] = AsBrush(controlDisabledForeground);
        resources["SelectionBackground"] = AsBrush(selectionBackground);
        resources["SelectionForeground"] = AsBrush(selectionForeground);
        resources["AccentBrush"] = AsBrush(accent);
        resources["FocusBorder"] = AsBrush(accent);
        resources["GridLineBrush"] = AsBrush(gridLine);
    }

    private static Brush AsBrush(object value) => value switch
    {
        Brush brush => brush,
        Color color => CreateBrush(color),
        _ => throw new ArgumentException("Theme palette values must be colors or brushes.", nameof(value))
    };

    private static SolidColorBrush CreateBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
