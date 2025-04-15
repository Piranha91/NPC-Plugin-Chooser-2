using System.IO;

namespace NPC_Plugin_Chooser_2.BackEnd;

public class Auxilliary
{
    private readonly EnvironmentStateProvider _environmentStateProvider;
    
    public Auxilliary(EnvironmentStateProvider environmentStateProvider)
    {
        _environmentStateProvider = environmentStateProvider;
    }
    
    public string FormKeyStringToFormIDString(string formKeyString)
    {
        if (TryFormKeyStringToFormIDString(formKeyString, out string formIDstr))
        {
            return formIDstr;
        }
        return String.Empty;
    }

    public bool TryFormKeyStringToFormIDString(string formKeyString, out string formIDstr)
    {
        formIDstr = string.Empty;
        var split = formKeyString.Split(':');
        if (split.Length != 2) { return false; }

        if (split[1] == _environmentStateProvider.OutputPluginName + ".esp")
        {
            formIDstr = _environmentStateProvider.LoadOrder.ListedOrder.Count().ToString("X"); // format FormID assuming the generated patch will be last in the load order
        }
        else
        {
            for (int i = 0; i < _environmentStateProvider.LoadOrder.ListedOrder.Count(); i++)
            {
                var currentListing = _environmentStateProvider.LoadOrder.ListedOrder.ElementAt(i);
                if (currentListing.ModKey.FileName == split[1])
                {
                    formIDstr = i.ToString("X"); // https://www.delftstack.com/howto/csharp/integer-to-hexadecimal-in-csharp/
                    break;
                }
            }
        }
        if (!formIDstr.Any())
        {
            return false;
        }

        if (formIDstr.Length == 1)
        {
            formIDstr = "0" + formIDstr;
        }

        formIDstr += split[0];
        return true;
    }
    
    public enum PathType
    {
        File,
        Directory
    }
    public static dynamic CreateDirectoryIfNeeded(string path, PathType type)
    {
        if (type == PathType.File)
        {
            FileInfo file = new FileInfo(path);
            file.Directory.Create(); // If the directory already exists, this method does nothing.
            return file;
        }
        else
        {
            DirectoryInfo directory = new DirectoryInfo(path);
            directory.Create();
            return directory;
        }
    }
}