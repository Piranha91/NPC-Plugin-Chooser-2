// Themes/ThemeManager.cs
using System;
using System.Linq;
using System.Windows;

namespace NPC_Plugin_Chooser_2.Themes
{
    public static class ThemeManager
    {
        public static event Action<bool> ThemeChanged;
        
        public static void ApplyTheme(bool isDark)
        {
            var themeDictionaries = Application.Current.Resources.MergedDictionaries;

            var existingTheme = themeDictionaries.FirstOrDefault(d =>
                d.Source != null && (d.Source.OriginalString.Contains("LightTheme.xaml") || d.Source.OriginalString.Contains("DarkMode.xaml")));

            if (existingTheme != null)
            {
                themeDictionaries.Remove(existingTheme);
            }

            string themeUri = isDark ? "Themes/DarkMode.xaml" : "Themes/LightTheme.xaml";
            var newTheme = new ResourceDictionary { Source = new Uri(themeUri, UriKind.Relative) };

            themeDictionaries.Add(newTheme);
            
            ThemeChanged?.Invoke(isDark);
        }
    }
}