using Microsoft.Xaml.Behaviors;
using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace NPC_Plugin_Chooser_2.Themes;

public class TitleBarThemingBehavior : Behavior<Window>
{
    protected override void OnAttached()
    {
        base.OnAttached();
        AssociatedObject.SourceInitialized += OnSourceInitialized;
        ThemeManager.ThemeChanged += OnThemeChanged;
    }

    protected override void OnDetaching()
    {
        AssociatedObject.SourceInitialized -= OnSourceInitialized;
        ThemeManager.ThemeChanged -= OnThemeChanged;
        base.OnDetaching();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        // Detect dark/light from the currently applied PrimaryBackground brush
        bool isDark = true;
        if (Application.Current.Resources["PrimaryBackground"] is SolidColorBrush brush)
        {
            var c = brush.Color;
            double luminance = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
            isDark = luminance < 0.5;
        }
        ApplyTitleBarTheme(isDark);
    }

    private void OnThemeChanged(bool isDark)
    {
        AssociatedObject.Dispatcher.Invoke(() => ApplyTitleBarTheme(isDark));
    }

    private void ApplyTitleBarTheme(bool isDark)
    {
        var handle = new WindowInteropHelper(AssociatedObject).Handle;
        if (handle != IntPtr.Zero)
        {
            DwmHelper.UseImmersiveDarkMode(handle, isDark);
        }
    }
}
