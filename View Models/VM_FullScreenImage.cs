using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using System.Windows.Media; // Add this using directive

namespace NPC_Plugin_Chooser_2.View_Models;

public class VM_FullScreenImage : ReactiveObject
{
    [Reactive] public string? ImagePath { get; private set; }
    [Reactive] public ImageSource? MugshotSource { get; private set; } // Add this property

    public VM_FullScreenImage(string imagePath)
    {
        ImagePath = imagePath;
    }

    // Add this new constructor overload for in-memory images
    public VM_FullScreenImage(ImageSource source)
    {
        MugshotSource = source;
    }
}