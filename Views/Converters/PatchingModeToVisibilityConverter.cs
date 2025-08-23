using System.Globalization;
using System.Windows;
using System.Windows.Data;
using NPC_Plugin_Chooser_2.Models;

namespace NPC_Plugin_Chooser_2.Views;

/// <summary>
    /// A value converter that converts a PatchingMode enum to a Visibility value.
    /// The UI element will be visible only if the bound PatchingMode value
    /// matches the PatchingMode value supplied in the ConverterParameter.
    /// </summary>
    public class PatchingModeToVisibilityConverter : IValueConverter
    {
        /// <summary>
        /// Converts a PatchingMode value to a Visibility value.
        /// </summary>
        /// <param name="value">The PatchingMode value from the binding source.</param>
        /// <param name="targetType">The type of the binding target property (should be Visibility).</param>
        /// <param name="parameter">The required PatchingMode for the element to be visible. Should be a string that can be parsed into the PatchingMode enum (e.g., "Create" or "CreateAndPatch").</param>
        /// <param name="culture">The culture to use in the converter.</param>
        /// <returns>Visibility.Visible if the value matches the parameter; otherwise, Visibility.Collapsed.</returns>
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            // Ensure the input value is a PatchingMode enum
            if (value is not PatchingMode currentMode)
            {
                return Visibility.Collapsed;
            }

            // Ensure the parameter is a string that can be parsed to the PatchingMode enum
            if (parameter is not string requiredModeString || !Enum.TryParse(requiredModeString, out PatchingMode requiredMode))
            {
                // Or throw an ArgumentException if the parameter is mandatory and invalid
                return Visibility.Collapsed;
            }

            // Return Visible if the current mode matches the required mode, otherwise Collapsed
            return currentMode == requiredMode ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>
        /// Converting from Visibility back to PatchingMode is not supported.
        /// </summary>
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            // This converter is one-way, so we don't implement ConvertBack.
            throw new NotImplementedException("Cannot convert from Visibility back to PatchingMode.");
        }
    }