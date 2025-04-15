// VM_Run.cs
using System;
using System.Linq; // Added for Any()
using System.Reactive;
using System.Reactive.Concurrency; // <-- ADD THIS NAMESPACE for Schedule extension method
using System.Reactive.Linq;
using System.Text;
using System.Threading.Tasks;
using Mutagen.Bethesda.Plugins; // Added for FormKey/ModKey
using Mutagen.Bethesda.Skyrim; // Added for INpcGetter
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using System.IO;
using Mutagen.Bethesda; // Added for Path

namespace NPC_Plugin_Chooser_2.View_Models
{
    public class VM_Run : ReactiveObject
    {
        private readonly EnvironmentStateProvider _environmentStateProvider;
        private readonly Settings _settings;
        private readonly NpcConsistencyProvider _consistencyProvider; // Access selected mods

        [Reactive] public string LogOutput { get; private set; } = string.Empty;
        [Reactive] public bool IsRunning { get; private set; }

        public ReactiveCommand<Unit, Unit> RunCommand { get; }

        public VM_Run(EnvironmentStateProvider environmentStateProvider, Settings settings, NpcConsistencyProvider consistencyProvider)
        {
            _environmentStateProvider = environmentStateProvider;
            _settings = settings;
            _consistencyProvider = consistencyProvider;

            // Command can only execute if environment is valid and not already running
            var canExecute = this.WhenAnyValue(x => x.IsRunning, x => x._environmentStateProvider.EnvironmentIsValid,
                                               (running, valid) => !running && valid);

            RunCommand = ReactiveCommand.CreateFromTask(RunPatchingLogic, canExecute);

            // Update IsRunning status when command executes/completes
            RunCommand.IsExecuting.BindTo(this, x => x.IsRunning);

            // Log exceptions from the command
            RunCommand.ThrownExceptions.Subscribe(ex =>
            {
                AppendLog($"ERROR: {ex.GetType().Name} - {ex.Message}");
                AppendLog(ExceptionLogger.GetExceptionStack(ex)); // Use your logger
                AppendLog("Patching failed.");
            });
        }

        private async Task RunPatchingLogic()
        {
            LogOutput = string.Empty; // Clear log on new run
            AppendLog("Starting patch generation...");
            AppendLog($"Output Mod: {_environmentStateProvider.OutputPluginName}");
            AppendLog($"Skyrim Version: {_environmentStateProvider.SkyrimVersion}");
            AppendLog($"Data Path: {_environmentStateProvider.DataFolderPath}");

            if (!_environmentStateProvider.EnvironmentIsValid || _environmentStateProvider.LoadOrder == null)
            {
                 AppendLog("Environment is not valid. Cannot run patcher. Check Settings.");
                 return;
            }

            AppendLog($"Load Order Count: {_environmentStateProvider.LoadOrder.Count}");

            // --- Your Backend Logic Here ---
            // This is where you'd interact with Mutagen based on the selections
            // stored in _consistencyProvider or _settings.SelectedAppearanceMods

            await Task.Delay(500); // Simulate work

            try
            {
                // Re-initialize output mod for the run
                _environmentStateProvider.OutputMod = new SkyrimMod(ModKey.FromName(_environmentStateProvider.OutputPluginName, ModType.Plugin), _environmentStateProvider.SkyrimVersion);


                AppendLog("\nProcessing NPC Appearance Selections:");

                 // Use the consistency provider's data which reflects the actual selections
                 var selections = _settings.SelectedAppearanceMods; // Get the dictionary from settings (managed by consistency provider)
                 if (!selections.Any()) {
                     AppendLog("No NPC appearance selections have been made or loaded.");
                 }

                int processedCount = 0;
                foreach (var kvp in selections)
                {
                    var npcFormKey = kvp.Key;
                    var selectedModName = kvp.Value;

                    // Resolve the NPC in the link cache using the FormKey from the selection
                    if (_environmentStateProvider.LinkCache.TryResolve<INpcGetter>(npcFormKey, out var winningNpcOverride))
                    {
                        AppendLog($"- NPC: {winningNpcOverride.Name?.String ?? winningNpcOverride.EditorID ?? npcFormKey.ToString()} ({npcFormKey}) -> Selected Mod: {selectedModName}");

                        // Find the specific context for the NPC from the *selected* mod
                        var contexts = _environmentStateProvider.LinkCache.ResolveAllContexts(winningNpcOverride);
                        var sourceContext = contexts.FirstOrDefault(c => c.ModKey == selectedModName); // THIS WILL NEED TO BE FIXED

                        if (sourceContext?.Record is INpcGetter sourceNpc) {
                           // Create or get the override record in our output patch
                           var patchNpc = _environmentStateProvider.OutputMod.Npcs.GetOrAddAsOverride(winningNpcOverride); // Override the winning record

                           // *** Implement the actual copying logic ***
                           // You need to decide exactly which fields constitute "appearance"
                           // and copy them from sourceNpc to patchNpc.
                           // Be careful not to copy everything, only appearance-related fields.

                           // Example copying (Adapt based on VM_NpcSelection.ModifiesAppearance logic):
                           patchNpc.FaceMorph = sourceNpc.FaceMorph?.DeepCopy(); // Use DeepCopy for complex types
                           patchNpc.FaceParts = sourceNpc.FaceParts?.DeepCopy();
                           if (sourceNpc.HairColor != null && !sourceNpc.HairColor.IsNull) // Check for null links
                           {
                               patchNpc.HairColor.SetTo(sourceNpc.HairColor);
                           }
                           patchNpc.HeadParts.Clear(); // Clear existing before adding
                           patchNpc.HeadParts.AddRange(sourceNpc.HeadParts); // Copy HeadParts (check if DeepCopy needed based on links)
                           if (sourceNpc.HeadTexture != null && !sourceNpc.HeadTexture.IsNull)
                           {
                               patchNpc.HeadTexture.SetTo(sourceNpc.HeadTexture);
                           }
                           patchNpc.TextureLighting = sourceNpc.TextureLighting;
                           patchNpc.TintLayers.Clear();
                           patchNpc.TintLayers.AddRange(sourceNpc.TintLayers.Select(t => t.DeepCopy())); // Tints need deep copy
                           if (sourceNpc.WornArmor != null && !sourceNpc.WornArmor.IsNull)
                           {
                               patchNpc.WornArmor.SetTo(sourceNpc.WornArmor);
                           }
                            // Add Height/Weight if desired
                            patchNpc.Height = sourceNpc.Height;
                            patchNpc.Weight = sourceNpc.Weight;

                            // Potentially add Face Data (FaceFX?) if needed/available

                           processedCount++;
                        } else {
                           AppendLog($"  WARNING: Could not find NPC record context in selected mod {selectedModName}. Skipping.");
                        }
                    }
                    else
                    {
                        AppendLog($"  WARNING: Could not resolve NPC {npcFormKey} in Link Cache. Skipping.");
                    }
                     await Task.Delay(5); // Tiny delay to keep UI responsive during loop
                }

                if (processedCount > 0)
                {
                     AppendLog($"\nCopied appearance data for {processedCount} NPC(s) to {_environmentStateProvider.OutputPluginName}.");

                     // Save the output mod
                     string outputPath = Path.Combine(_environmentStateProvider.DataFolderPath.ToString(), _environmentStateProvider.OutputPluginName); // Ensure DataFolderPath is a string
                     AppendLog($"Attempting to save output mod to: {outputPath}");
                     _environmentStateProvider.OutputMod.WriteToBinary(outputPath);
                     AppendLog($"Output mod saved successfully.");
                }
                 else
                 {
                     AppendLog("\nNo NPC appearances were processed or copied. Output mod not saved.");
                 }

                AppendLog("\nPatch generation process completed.");
            }
            catch (Exception ex)
            {
                 // Log exceptions during the patching process itself
                 AppendLog($"\nFATAL PATCHING ERROR: {ex.Message}");
                 AppendLog(ExceptionLogger.GetExceptionStack(ex));
                 AppendLog($"Output mod ({_environmentStateProvider.OutputPluginName}) was NOT saved due to errors.");
                 // Do not rethrow here if you want the command's IsExecuting to become false normally.
                 // The error is already logged via the command's ThrownExceptions handler.
                 // throw; // Re-throwing prevents the command from naturally completing its 'IsExecuting' cycle
            }
        }

        private void AppendLog(string message)
        {
            // Ensure updates happen on the UI thread if called from background tasks
            // This should now compile correctly with 'using System.Reactive.Concurrency;'
            RxApp.MainThreadScheduler.Schedule(() =>
            {
                LogOutput += message + Environment.NewLine;
                 // TODO: Maybe auto-scroll log viewer? (Requires access to the TextBox control, usually via Interaction)
            });
        }
    }
}