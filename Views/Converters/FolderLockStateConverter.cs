using System;
using System.Globalization;
using System.Windows.Data;
using NPC_Plugin_Chooser_2.View_Models;

namespace NPC_Plugin_Chooser_2.Views
{
    /// <summary>
    /// Reports whether one folder path in a mod's "Corresponding Mod Folder Paths" list is locked
    /// against Refresh.
    ///
    /// <para>The list is bound as a collection of bare strings, so an item has no way to see the lock
    /// state, which lives on the owning <see cref="VM_ModSetting"/>. This converter takes the item, the
    /// owning VM, and <see cref="VM_ModSetting.LockedFolderRevision"/> — the revision is what forces
    /// WPF to re-evaluate the binding when a lock is toggled, since neither the string item nor the VM
    /// reference itself changes.</para>
    /// </summary>
    public class FolderLockStateConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values == null || values.Length < 2) return false;
            if (values[0] is not string path) return false;
            if (values[1] is not VM_ModSetting modSetting) return false;
            // values[2] is LockedFolderRevision — read purely to establish the dependency.

            return modSetting.IsFolderLocked(path);
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException(
                $"{nameof(FolderLockStateConverter)} is one-way; locks are toggled via ToggleFolderLockCommand.");
        }
    }
}
