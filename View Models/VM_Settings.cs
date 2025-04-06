using Mutagen.Bethesda.Skyrim;
using Noggog;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace NPC_Plugin_Chooser_2.View_Models;

public class VM_Settings
{
    [Reactive]
    public string ModsFolder { get; set; } = string.Empty;
    [Reactive]
    public string MugshotsFolder { get; set; } = string.Empty;
    [Reactive] 
    public SkyrimRelease SkyrimRelease { get; set; } = SkyrimRelease.SkyrimSE;
    [Reactive]
    public string SkyrimGamePath { get; set; } = string.Empty;
    [Reactive] 
    public bool EnvironmentIsValid { get; set; } = false;
    [Reactive]
    public string OutputModName { get; set; } = "NPC Plugin Chooser.esp";

    public VM_Settings(EnvironmentStateProvider environmentStateProvider, Settings model)
    {
        this.WhenAnyValue(x => x.ModsFolder).Subscribe(s =>
        {
            model.ModsFolder = s;
        });
        
        this.WhenAnyValue(x => x.MugshotsFolder).Subscribe(s =>
        {
            model.MugshotsFolder = s;
        });
        
        this.WhenAnyValue(x => x.SkyrimGamePath).Subscribe(s =>
        {
            model.SkyrimGamePath = s;
            environmentStateProvider.UpdateEnvironment();
            EnvironmentIsValid = environmentStateProvider.EnvironmentIsValid;
        });
        
        this.WhenAnyValue(x => x.SkyrimRelease).Subscribe(r =>
        {
            model.SkyrimRelease = r;
            environmentStateProvider.UpdateEnvironment();
            EnvironmentIsValid = environmentStateProvider.EnvironmentIsValid;
        });
        
        this.WhenAnyValue(x => x.OutputModName).Subscribe(s =>
        {
            model.OutputModName = s;
            environmentStateProvider.UpdateEnvironment();
            EnvironmentIsValid = environmentStateProvider.EnvironmentIsValid;
        });
    }
}