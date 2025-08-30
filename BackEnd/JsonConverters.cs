namespace NPC_Plugin_Chooser_2.BackEnd;

using System;
using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using Mutagen.Bethesda.Plugins;
using Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;

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

public class ModSettingConverter : JsonConverter
{
    public override bool CanConvert(Type objectType)
    {
        return objectType == typeof(ModSetting);
    }

    public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
    {
        // Load the JSON for the ModSetting into a temporary object
        JObject jo = JObject.Load(reader);

        // Create the final ModSetting object that we will populate
        ModSetting modSetting = new ModSetting();
        
        // Use the default serializer to populate all matching properties automatically
        serializer.Populate(jo.CreateReader(), modSetting);

        // **Here's the migration logic**
        // Check if the old "MugShotFolderPath" property exists and is a string
        if (jo.TryGetValue("MugShotFolderPath", StringComparison.OrdinalIgnoreCase, out JToken oldPathToken) && oldPathToken.Type == JTokenType.String)
        {
            string oldPath = oldPathToken.Value<string>();

            // If the new list is empty and the old path has a value,
            // create the list and add the old path to it.
            if ((modSetting.MugShotFolderPaths == null || modSetting.MugShotFolderPaths.Count == 0) && !string.IsNullOrEmpty(oldPath))
            {
                modSetting.MugShotFolderPaths = new List<string> { oldPath };
            }
        }
        
        return modSetting;
    }

    // We make this converter read-only.
    // When writing the JSON back to the file, we want the default serializer to handle it,
    // ensuring only the new "MugShotFolderPaths" property is saved.
    public override bool CanWrite => false;

    public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
    {
        throw new NotImplementedException("This converter is read-only and should not be used for writing JSON.");
    }
}

