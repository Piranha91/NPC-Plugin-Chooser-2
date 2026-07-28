using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using NPC_Plugin_Chooser_2.View_Models;

namespace NPC_Plugin_Chooser_2.Views
{
    /// <summary>
    /// Hides the lock toggle for a mod's primary folder — the entry whose folder name matches the mod's
    /// DisplayName.
    ///
    /// <para>A Refresh rebuilds the folder list <em>around</em> that folder, so it can never be dropped
    /// and there is nothing for a lock to protect against. Offering the toggle there would imply a
    /// choice that doesn't exist.</para>
    ///
    /// <para>Expects [folder path, owning <see cref="VM_ModSetting"/>, DisplayName]. DisplayName is read
    /// only to make the binding re-evaluate if the mod is renamed, which can change which folder counts
    /// as primary.</para>
    /// </summary>
    public class FolderLockVisibilityConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values == null || values.Length < 2) return Visibility.Collapsed;
            if (values[0] is not string path) return Visibility.Collapsed;
            if (values[1] is not VM_ModSetting modSetting) return Visibility.Collapsed;

            return modSetting.IsPrimaryFolderPath(path) ? Visibility.Collapsed : Visibility.Visible;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException($"{nameof(FolderLockVisibilityConverter)} is one-way.");
        }
    }
}
