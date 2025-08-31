// TitleBarThemingBehavior.cs
using Microsoft.Xaml.Behaviors;
using NPC_Plugin_Chooser_2.Models;
using Splat;
using System;
using System.Windows;
using System.Windows.Interop;

namespace NPC_Plugin_Chooser_2.Themes;

public class TitleBarThemingBehavior : Behavior<Window>
{
    protected override void OnAttached()
    {
        base.OnAttached();
        // Hook into the events when the behavior is attached to a window
        AssociatedObject.SourceInitialized += OnSourceInitialized;
        ThemeManager.ThemeChanged += OnThemeChanged;
    }

    protected override void OnDetaching()
    {
        // Unhook the events when the window closes
        AssociatedObject.SourceInitialized -= OnSourceInitialized;
        ThemeManager.ThemeChanged -= OnThemeChanged;
        base.OnDetaching();
    }

    private void OnSourceInitialized(object sender, EventArgs e)
    {
        // Apply the theme as soon as the window is created
        var settings = Locator.Current.GetService<Settings>();
        if (settings != null)
        {
            ApplyTitleBarTheme(settings.IsDarkMode);
        }
    }

    private void OnThemeChanged(bool isDark)
    {
        // Apply the theme whenever it's changed globally
        AssociatedObject.Dispatcher.Invoke(() => ApplyTitleBarTheme(isDark));
    }

    private void ApplyTitleBarTheme(bool isDark)
    {
        // This is the same logic we used before
        var handle = new WindowInteropHelper(AssociatedObject).Handle;
        if (handle != IntPtr.Zero)
        {
            DwmHelper.UseImmersiveDarkMode(handle, isDark);
        }
    }
}
