using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;

namespace NPC_Plugin_Chooser_2.Themes
{
    public static class ThemeManager
    {
        /// <summary>
        /// Fired whenever the active theme changes. The bool indicates whether the theme is dark.
        /// </summary>
        public static event Action<bool>? ThemeChanged;

        private static readonly string ThemesFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Themes");

        /// <summary>
        /// Returns the display names (file names without extension) of all available themes.
        /// </summary>
        public static List<string> GetAvailableThemes()
        {
            if (!Directory.Exists(ThemesFolder))
                return new List<string>();

            return Directory.GetFiles(ThemesFolder, "*.xaml")
                .Select(f => Path.GetFileNameWithoutExtension(f))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Applies the theme with the given display name.
        /// Falls back to "DarkMode" if the requested theme is not found.
        /// </summary>
        public static void ApplyTheme(string themeName)
        {
            var themeDictionaries = Application.Current.Resources.MergedDictionaries;

            // Remove any previously applied theme dictionary (tagged via our attached marker)
            var existing = themeDictionaries.FirstOrDefault(d => d["__ThemeManager_Applied"] != null);
            if (existing != null)
                themeDictionaries.Remove(existing);

            var themeFile = Path.Combine(ThemesFolder, themeName + ".xaml");
            if (!File.Exists(themeFile))
            {
                // Fallback: try DarkMode
                themeFile = Path.Combine(ThemesFolder, "DarkMode.xaml");
                if (!File.Exists(themeFile))
                    return; // No themes available at all
            }

            ResourceDictionary newTheme;
            try
            {
                using var stream = File.OpenRead(themeFile);
                newTheme = (ResourceDictionary)XamlReader.Load(stream);
            }
            catch (Exception)
            {
                return; // Silently skip broken theme files
            }

            // Tag the dictionary so we can find and remove it later
            newTheme["__ThemeManager_Applied"] = true;

            themeDictionaries.Add(newTheme);

            bool isDark = IsDarkTheme(newTheme);
            ThemeChanged?.Invoke(isDark);
        }

        /// <summary>
        /// Determines whether a theme is "dark" by checking the luminance of PrimaryBackground.
        /// </summary>
        private static bool IsDarkTheme(ResourceDictionary theme)
        {
            if (theme["PrimaryBackground"] is SolidColorBrush brush)
            {
                var c = brush.Color;
                // Relative luminance approximation
                double luminance = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
                return luminance < 0.5;
            }
            // Default to dark if we can't determine
            return true;
        }

        /// <summary>
        /// Backward-compatible overload: maps the old IsDarkMode boolean to theme names.
        /// </summary>
        public static void ApplyTheme(bool isDark)
        {
            ApplyTheme(isDark ? "DarkMode" : "LightTheme");
        }
    }
}
