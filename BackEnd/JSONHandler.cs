using DynamicData;
using Mutagen.Bethesda.Plugins;

namespace NPC_Plugin_Chooser_2.BackEnd;

using Newtonsoft.Json;
using Mutagen.Bethesda.Json;
using System.IO;
using Newtonsoft.Json.Linq;
using Mutagen.Bethesda.Skyrim;


public class JSONhandler<T>
{
    public static JsonSerializerSettings GetMyMutagenJSONSettings()
    {
        var jsonSettings = new JsonSerializerSettings();
        jsonSettings.AddMutagenConverters();
        jsonSettings.ObjectCreationHandling = ObjectCreationHandling.Replace;
        jsonSettings.Formatting = Formatting.Indented;
        jsonSettings.Converters.Add(new Newtonsoft.Json.Converters.StringEnumConverter()); // https://stackoverflow.com/questions/2441290/javascriptserializer-json-serialization-of-enum-as-string

        jsonSettings.Converters.Add(new ModSettingConverter()); // For 2.0.5 upgrade to convert string MugShotFolderPath to List<string> MugShotFolderPaths
        
        return jsonSettings;
    }

    public static T? Deserialize(string jsonInputStr, out bool success, out string exception, bool canBeNull = false)
    {
        try
        {
            success = true;
            exception = "";
            var output = JsonConvert.DeserializeObject<T>(jsonInputStr, GetMyMutagenJSONSettings());
            if (output == null && !canBeNull)
            {
                success = false;
                exception = "JSON object was null";
            }
            return output;
        }
        catch (Exception ex)
        {
            success = false;
            exception = ExceptionLogger.GetExceptionStack(ex);
            return default(T);
        }
    }

    public static T? LoadJSONFile(string loadLoc, out bool success, out string exception, bool canBeNull = false)
    {
        if (!File.Exists(loadLoc))
        {
            success = false;
            exception = "File " + loadLoc + " does not exist.";
            return default(T);
        }

        string contents = String.Empty;

        try
        {
            contents = File.ReadAllText(loadLoc);
        }
        catch (Exception ex)
        {
            success = false;
            exception = ExceptionLogger.GetExceptionStack(ex);
            return default(T);
        }

        return Deserialize(contents, out success, out exception, canBeNull);
    }

    public static string Serialize(T input, out bool success, out string exception)
    {
        try
        {
            success = true;
            exception = "";
            return JsonConvert.SerializeObject(input, Formatting.Indented, GetMyMutagenJSONSettings());
        }
        catch (Exception ex)
        {
            exception = ex.Message;
            success = false;
            return "";
        }
    }

    /// <summary>
    /// Serializes <paramref name="input"/> to <paramref name="saveLoc"/> atomically: the JSON is
    /// written and flushed to a sibling <c>.tmp</c> file, which then replaces the target in one
    /// step. A crash or power loss mid-save leaves the previous file intact instead of a
    /// truncated one, and a failed serialization never touches the target at all.
    /// With <paramref name="keepBackup"/>, the replaced version is kept as <c>saveLoc.bak</c>.
    /// </summary>
    public static void SaveJSONFile(T input, string saveLoc, out bool success, out string exception, bool keepBackup = false)
    {
        try
        {
            string json = Serialize(input, out success, out exception);
            if (!success) return;
            Auxilliary.CreateDirectoryIfNeeded(saveLoc, Auxilliary.PathType.File);
            WriteAllTextAtomic(saveLoc, json, keepBackup ? saveLoc + ".bak" : null);
        }
        catch(Exception ex)
        {
            exception = ex.Message;
            success = false;
        }
    }

    /// <summary>
    /// Writes <paramref name="contents"/> to a temp file beside <paramref name="path"/>, flushes it
    /// to disk, then swaps it in. <paramref name="backupPath"/> (optional) receives the old file.
    /// </summary>
    public static void WriteAllTextAtomic(string path, string contents, string? backupPath = null)
    {
        string tempPath = path + ".tmp";
        using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(fs))
        {
            writer.Write(contents);
            writer.Flush();
            fs.Flush(flushToDisk: true);
        }

        if (File.Exists(path))
        {
            try
            {
                File.Replace(tempPath, path, backupPath, ignoreMetadataErrors: true);
                return;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (PlatformNotSupportedException) { }

            // File.Replace is unavailable on some file systems (and under some virtual file
            // systems). Fall back to backup copy + overwriting move, which is still never
            // left half-written: the move either happens or it doesn't.
            if (backupPath != null) File.Copy(path, backupPath, overwrite: true);
        }
        File.Move(tempPath, path, overwrite: true);
    }

    public static T? CloneViaJSON(T input, bool canBeNull = false)
    {
        return Deserialize(Serialize(input, out _, out _), out _, out _, canBeNull);
    }
}