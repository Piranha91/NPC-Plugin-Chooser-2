namespace NPC_Plugin_Chooser_2.BackEnd;

using System;
using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using Mutagen.Bethesda.Plugins;

public class FormKeyTypeConverter : TypeConverter
{
    // The regex expects a string without the surrounding brackets, so you add them if needed.
    private static readonly Regex formatRegex = new Regex(
        @"^(?<key>[0-9A-Fa-f]{6}):(?<filename>.+)\.(?<ext>esp|esm|esl)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Indicate we can convert from string.
    public override bool CanConvertFrom(ITypeDescriptorContext context, Type sourceType)
    {
        return sourceType == typeof(string) || base.CanConvertFrom(context, sourceType);
    }

    // Convert a string value to a FormKey.
    public override object ConvertFrom(ITypeDescriptorContext context, CultureInfo culture, object value)
    {
        if (value is string s)
        {
            // If the string is not surrounded by brackets, optionally normalize it.
            if (!s.StartsWith("[") && !s.EndsWith("]"))
            {
                s = $"[{s}]";
            }

            // Use a regex that expects the surrounding brackets.
            var match = Regex.Match(s, @"^\[(?<key>[0-9A-Fa-f]{6}):(?<filename>.+)\.(?<ext>esp|esm|esl)\]$",
                                    RegexOptions.IgnoreCase | RegexOptions.Compiled);
            if (!match.Success)
            {
                throw new FormatException($"Input string '{s}' is not in the expected format '[xxxxxx:substring.ext]'.");
            }

            // Enforce the case conventions.
            string hexPart = match.Groups["key"].Value.ToUpperInvariant();
            string filename = match.Groups["filename"].Value;
            string ext = match.Groups["ext"].Value.ToLowerInvariant();

            // Rebuild the standardized key.
            string formattedKey = $"{hexPart}:{filename}.{ext}";

            // Create and return the FormKey instance using the factory method.
            return FormKey.Factory(formattedKey);
        }
        return base.ConvertFrom(context, culture, value);
    }
}

