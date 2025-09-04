using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace NPC_Plugin_Chooser_2.View_Models;

public class VM_FullScreenImage : ReactiveObject
{
    [Reactive] public string ImagePath { get; private set; }

    public VM_FullScreenImage(string imagePath)
    {
        ImagePath = imagePath;
    }
}