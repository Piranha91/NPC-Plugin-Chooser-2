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

        return jsonSettings;
    }

    public static T Deserialize(string jsonInputStr, out bool success, out string exception)
    {
        try
        {
            success = true;
            exception = "";
            return JsonConvert.DeserializeObject<T>(jsonInputStr, GetMyMutagenJSONSettings());
        }
        catch (Exception ex)
        {
            success = false;
            exception = ExceptionLogger.GetExceptionStack(ex);
            return default(T);
        }
    }

    public static T LoadJSONFile(string loadLoc, out bool success, out string exception)
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

        return Deserialize(contents, out success, out exception);
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

    public static void SaveJSONFile(T input, string saveLoc, out bool success, out string exception)
    {
        try
        {
            Auxilliary.CreateDirectoryIfNeeded(saveLoc, Auxilliary.PathType.File);
            File.WriteAllText(saveLoc, Serialize(input, out success, out exception));
        }
        catch(Exception ex)
        {
            exception = ex.Message;
            success = false;
        }
    }

    public static T CloneViaJSON(T input)
    {
        return Deserialize(Serialize(input, out _, out _), out _, out _);
    }
}