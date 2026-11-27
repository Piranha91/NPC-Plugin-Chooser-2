using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using ReactiveUI;
using Application = System.Windows.Application;

namespace NPC_Plugin_Chooser_2.View_Models;

// The selection indicator of the NPC list: red (NPC not in the load order), purple (the selected
// mod has no data), green (valid), and the two "valid, with a note" refinements of green drawn
// with diagonal hatches:
//
//   green with ORANGE hatches  'Include Outfit' acts on an NPC its template dresses
//   green with RED hatches     the wig would be forwarded into an outfit that holds headgear on
//                              the same slot (no stored choice, or the stored choice Forward)
//
// The decisions are NpcSelectionIndicatorRules' (pure); this file gathers their inputs and applies
// the result to the row.
//
// Cost. A pass over every NPC with a selection (RefreshAllSelectionIndicators) does dictionary
// lookups only. The outfit note reads one cached flag per row and resolves the winning record
// only for rows that have the note. The wig question resolves an outfit per NPC, so it is never
// answered here: it goes to WigHeadgearIndicatorScanner, which works on the thread pool, and
// only for rows whose mod's effective wig mode is 'Forward to Outfit'. The row is redrawn when
// its answer arrives (OnWigConflictAnswers).
public partial class VM_NpcSelectionBar
{
    private WigHeadgearChoiceCoordinator? _wigHeadgearChoices;
    private WigHeadgearIndicatorScanner? _wigConflictScanner;
    private Dictionary<(Color Base, Color Hatch), Brush>? _hatchedBrushes;

    /// <summary>Called once from the constructor.</summary>
    private void InitSelectionIndicators(WigForwarder wigForwarder, WigHeadgearChoiceCoordinator wigHeadgearChoices)
    {
        _wigHeadgearChoices = wigHeadgearChoices;

        var scanner = new WigHeadgearIndicatorScanner(
            query =>
            {
                // A patch run loads and releases the same plugins on its own threads. An answer
                // worked out beside it could be read from a plugin being released, and "could not
                // read" looks like "no conflict"; so nothing is answered, or kept, while a run is
                // going. The rows ask again when the run ends (the coordinator's Changed).
                if (wigHeadgearChoices.IsRunActive) return null;
                var conflicts = wigForwarder.FindHeadgearConflictsForSelection(
                    query.Key.Npc, query.Key.SourceNpc, query.ModSetting);
                return wigHeadgearChoices.IsRunActive ? null : conflicts;
            },
            OnWigConflictAnswers);
        _wigConflictScanner = scanner;
        Disposable.Create(scanner.Dispose).DisposeWith(_disposables);

        // A stored or cleared answer changes which rows still show the conflict.
        wigHeadgearChoices.Changed
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => RefreshAllSelectionIndicators())
            .DisposeWith(_disposables);

        // The kept converted-wig hair colour: the 3D preview's "Keep for this NPC" stores or
        // removes the choice and says so; the row (its "Clear" menu item) follows without the list
        // being rebuilt. The badge is on the mugshot tiles, which listen for themselves
        // (VM_NpcsMenuMugshot.ListenForKeptWigHairTint).
        ClearKeptWigHairTintCommand = ReactiveCommand.Create<VM_NpcsMenuSelection>(ClearKeptWigHairTint)
            .DisposeWith(_disposables);
        MessageBus.Current.Listen<NpcWigHairTintKeptChangedMessage>()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(message => ApplyKeptWigHairTint(message.NpcFormKey))
            .DisposeWith(_disposables);
    }

    /// <summary>The NPC row's right-click "Clear kept wig hair colour": removes that NPC's kept
    /// converted-wig hair colour. The parameter is the row.</summary>
    public ReactiveCommand<VM_NpcsMenuSelection, System.Reactive.Unit> ClearKeptWigHairTintCommand { get; private set; } = null!;

    /// <summary>Makes one row say what <see cref="Settings.NpcWigHairTintModes"/> holds for it
    /// now (whether its "Clear kept wig hair color" item is offered). Does nothing for an NPC
    /// that has no row.</summary>
    internal void ApplyKeptWigHairTint(FormKey npcFormKey)
    {
        var npcVM = _npcVmLookup != null && _npcVmLookup.TryGetValue(npcFormKey, out var found)
            ? found
            : AllNpcs.FirstOrDefault(n => n.NpcFormKey.Equals(npcFormKey));
        npcVM?.SetKeptWigHairTint(_settings.GetNpcWigHairTintMode(npcFormKey));
    }

    /// <summary>The NPC row's "Clear kept wig hair color".</summary>
    internal void ClearKeptWigHairTint(VM_NpcsMenuSelection? npcVM)
    {
        if (npcVM == null) return;
        ClearKeptWigHairTintFor(npcVM.NpcFormKey);
    }

    /// <summary>Removes the NPC's kept converted-wig hair colour and tells whoever shows it (the
    /// badge on its mugshot tiles, an open 3D preview's Keep box). Behind both "Clear kept wig
    /// hair color" items: the NPC row's and the mugshot tile's.</summary>
    internal void ClearKeptWigHairTintFor(FormKey npcFormKey)
    {
        if (_settings.RemoveNpcWigHairTintModes(new[] { npcFormKey }) == 0) return;

        // The row directly, so it follows even where nothing is listening; then everyone else.
        ApplyKeptWigHairTint(npcFormKey);
        MessageBus.Current.SendMessage(new NpcWigHairTintKeptChangedMessage(npcFormKey));
    }

    /// <summary>
    /// Re-evaluates the indicator of every NPC with a selection. Called when the theme changes,
    /// when wig-and-headgear answers change, and when the NPCs tab is shown again (the settings
    /// the notes depend on are edited on other tabs). Rows whose inputs did not change keep their
    /// brush and cost a lookup; wig questions already answered are not asked again.
    /// </summary>
    public void RefreshSelectionIndicators() => RefreshAllSelectionIndicators();

    /// <summary>Re-evaluates one NPC's indicator (its outfit override changed).</summary>
    private void RefreshSelectionIndicator(FormKey npcFormKey)
    {
        var npcVM = _npcVmLookup != null && _npcVmLookup.TryGetValue(npcFormKey, out var found)
            ? found
            : AllNpcs.FirstOrDefault(n => n.NpcFormKey.Equals(npcFormKey));
        if (npcVM == null || string.IsNullOrEmpty(npcVM.SelectedAppearanceModName)) return;
        ApplySelectionIndicator(npcVM, new IndicatorPass { SingleRow = true });
    }

    /// <summary>Drops every wig answer and the work in progress: the NPC list is being rebuilt,
    /// possibly on other records.</summary>
    private void ResetWigConflictAnswers() => _wigConflictScanner?.Reset();

    /// <summary>The worker's "these rows have an answer now". Runs on the worker thread.</summary>
    private void OnWigConflictAnswers(IReadOnlyList<FormKey> answeredNpcs)
    {
        RxApp.MainThreadScheduler.Schedule(() =>
        {
            var pass = new IndicatorPass();
            foreach (var npcFormKey in answeredNpcs)
            {
                if (!_npcVmLookup.TryGetValue(npcFormKey, out var npcVM)) continue;
                if (string.IsNullOrEmpty(npcVM.SelectedAppearanceModName)) continue;
                ApplySelectionIndicator(npcVM, pass);
            }

            var scanner = _wigConflictScanner;
            if (scanner != null)
            {
                Debug.WriteLine($"NPC list wig/headgear indicator: {scanner.QueriesRun} record queries so far, " +
                                $"{scanner.TotalQueryTime.TotalMilliseconds:F0} ms in the back end.");
            }
        });
    }

    /// <summary>What one pass over rows shares: the mods by name and the settings signature,
    /// each worked out once and only when a row needs it.</summary>
    private sealed class IndicatorPass
    {
        /// <summary>True for a pass over one row: the mod is then found by a single search of
        /// the mod list instead of indexing all of it.</summary>
        public bool SingleRow;
        public Dictionary<string, IndicatorMod>? Mods;
        public string? SettingsSignature;
    }

    private sealed class IndicatorMod
    {
        public IndicatorMod(VM_ModSetting vm) => Vm = vm;
        public VM_ModSetting Vm { get; }

        /// <summary>Null until asked; then whether this mod's effective wig mode is 'Forward to
        /// Outfit'.</summary>
        public bool? ForwardsWigsToOutfits;

        /// <summary>The mod as the back end reads it. Built only for a mod that forwards wigs to
        /// outfits.</summary>
        public ModSetting? Snapshot;

        public string? Signature;
    }

    private IndicatorMod? FindIndicatorMod(IndicatorPass pass, string? modName)
    {
        if (string.IsNullOrEmpty(modName)) return null;

        var all = _lazyModsVm?.Value?.AllModSettings;

        if (pass.SingleRow)
        {
            var single = all?.FirstOrDefault(ms => ms.DisplayName.Equals(modName, StringComparison.OrdinalIgnoreCase));
            return single == null ? null : new IndicatorMod(single);
        }

        if (pass.Mods == null)
        {
            pass.Mods = new Dictionary<string, IndicatorMod>(StringComparer.OrdinalIgnoreCase);
            if (all != null)
            {
                foreach (var modVm in all)
                {
                    // First of a name wins, as the FirstOrDefault lookups elsewhere in this class do.
                    pass.Mods.TryAdd(modVm.DisplayName, new IndicatorMod(modVm));
                }
            }
        }

        return pass.Mods.TryGetValue(modName, out var mod) ? mod : null;
    }

    /// <summary>
    /// Whether the mod's effective wig mode is 'Forward to Outfit'
    /// (<see cref="Settings.GetEffectiveWigMode"/> on a snapshot of the mod). The first two tests
    /// are conditions that mode cannot do without, checked on the view model so that no snapshot
    /// is built for the mods, usually all of them, that do not forward wigs to outfits.
    /// </summary>
    private bool ForwardsWigsToOutfits(IndicatorMod mod)
    {
        if (mod.ForwardsWigsToOutfits is { } known) return known;

        bool forwards = false;
        if (_settings.WigHandlingActiveForOutputMode &&
            (mod.Vm.OverrideWigHandlingMode ?? _settings.DefaultWigHandlingMode) == WigHandlingMode.ForwardToOutfit)
        {
            mod.Snapshot = mod.Vm.SaveToModel();
            forwards = NpcSelectionIndicatorRules.WigConflictQueryApplies(_settings.GetEffectiveWigMode(mod.Snapshot));
        }

        mod.ForwardsWigsToOutfits = forwards;
        return forwards;
    }

    /// <summary>The settings a wig answer depends on beyond the mod and the NPC: which records
    /// are in use, how the output is delivered, and the global defaults a mod can fall back on.</summary>
    private string GetSettingsSignature(IndicatorPass pass)
    {
        if (pass.SettingsSignature != null) return pass.SettingsSignature;

        var linkCache = _environmentStateProvider.LinkCache;
        pass.SettingsSignature = string.Join("|",
            linkCache == null ? 0 : RuntimeHelpers.GetHashCode(linkCache),
            (int)_settings.PatchingMode,
            _settings.UsesSurrogates ? 1 : 0,
            _settings.SkyPatcherDirectivesAvailable ? 1 : 0,
            _settings.UsesRecast ? 1 : 0,
            (int)_settings.TemplateHandlingMode,
            (int)_settings.ManualWigBlockScope,
            _settings.ManualWigArmatures?.Count ?? 0,
            _settings.ManualNonWigArmatures?.Count ?? 0);
        return pass.SettingsSignature;
    }

    private static string GetModSignature(IndicatorMod mod)
    {
        if (mod.Signature != null) return mod.Signature;

        var vm = mod.Vm;
        mod.Signature = string.Join("|",
            vm.IncludeOutfits ? 1 : 0,
            vm.OverrideTemplateHandlingMode is { } templateMode ? (int)templateMode : -1,
            vm.DetectedWigArmors.Count,
            vm.DetectedWigArmatures.Count,
            vm.CorrespondingModKeys.Count,
            vm.CorrespondingFolderPaths.Count);
        return mod.Signature;
    }

    private Brush GetHatchedBrush(Color baseColor, Color hatchColor)
    {
        _hatchedBrushes ??= new Dictionary<(Color, Color), Brush>();
        var key = (baseColor, hatchColor);
        if (!_hatchedBrushes.TryGetValue(key, out var brush))
        {
            // One frozen brush per colour pair, shared by every row: a row whose state did not
            // change keeps the very same instance, so it raises no property change.
            brush = NpcSelectionIndicatorRules.CreateHatchedBrush(key.Item1, key.Item2);
            _hatchedBrushes[key] = brush;
        }

        return brush;
    }

    /// <summary>
    /// Works out and applies the indicator of one NPC THAT HAS A SELECTION
    /// (<see cref="VM_NpcsMenuSelection.SelectedAppearanceModName"/> and
    /// <see cref="VM_NpcsMenuSelection.SelectedModHasData"/> already set by the caller).
    /// </summary>
    private void ApplySelectionIndicator(VM_NpcsMenuSelection npcVM, IndicatorPass pass)
    {
        var outfitNote = TemplatedOutfitNote.None;
        IReadOnlyList<WigHeadgearConflict>? conflicts = null;
        WigHeadgearChoice? storedChoice = null;
        bool wigConflictIsOpen = false;

        // The notes refine green only: nothing is looked up for a red or purple row.
        if (npcVM.IsInLoadOrder && npcVM.SelectedModHasData &&
            FindIndicatorMod(pass, npcVM.SelectedAppearanceModName) is { } mod)
        {
            outfitNote = ResolveOutfitNote(npcVM, mod);

            if (_wigConflictScanner is { } scanner && ForwardsWigsToOutfits(mod) && mod.Snapshot != null)
            {
                storedChoice = _settings.GetWigHeadgearChoice(npcVM.NpcFormKey);

                // An NPC answered with Omit or Convert shows no conflict whatever the records
                // say, so there is nothing to find out about it.
                if (storedChoice is null or WigHeadgearChoice.Forward)
                {
                    var source = _consistencyProvider.GetSelectedMod(npcVM.NpcFormKey).SourceNpcFormKey;
                    var key = new WigConflictQueryKey(
                        npcVM.NpcFormKey,
                        mod.Vm.DisplayName,
                        source.IsNull ? npcVM.NpcFormKey : source,
                        WigHandlingMode.ForwardToOutfit,
                        string.Join("|", GetSettingsSignature(pass), GetModSignature(mod),
                            _settings.NpcOutfitOverrides.TryGetValue(npcVM.NpcFormKey, out var outfitOverride)
                                ? (int)outfitOverride
                                : -1));

                    if (scanner.TryGetAnswer(key, out var answer))
                    {
                        conflicts = answer;
                        wigConflictIsOpen = NpcSelectionIndicatorRules.WigConflictIsOpen(answer.Count > 0, storedChoice);
                    }
                    else if (_wigHeadgearChoices?.IsRunActive != true)
                    {
                        // Answered on the thread pool; OnWigConflictAnswers redraws the row.
                        // (Not during a patch run: see InitSelectionIndicators.)
                        scanner.Request(new WigConflictQuery(key, mod.Snapshot));
                    }
                }
            }
        }

        var state = NpcSelectionIndicatorRules.Resolve(hasSelection: true, npcVM.IsInLoadOrder,
            npcVM.SelectedModHasData, outfitNote, wigConflictIsOpen);

        SolidColorBrush solid = state switch
        {
            NpcSelectionIndicatorState.NotInLoadOrder => NpcIndicatorRedBrush,
            NpcSelectionIndicatorState.SelectedModHasNoData => NpcIndicatorPurpleBrush,
            _ => NpcIndicatorGreenBrush,
        };

        // The hatched brush is built from the colours of the theme's brushes. Reading a brush's
        // colour needs the thread that owns the brush, which the three solid states never did
        // (they only hand the brush on). Everything that calls this runs on the UI thread; should
        // a caller ever not, the row is drawn as plain valid, with its note, instead of throwing,
        // and the next refresh on the UI thread hatches it.
        Brush indicatorBrush = solid;
        bool hatched = false;
        if (NpcSelectionIndicatorRules.IsHatched(state) && ReadColor(solid) is { } baseColor)
        {
            Color? hatchColor = state == NpcSelectionIndicatorState.ValidWithWigConflict
                ? ReadColor(NpcIndicatorRedBrush)
                : NpcSelectionIndicatorRules.ResolveOutfitNoteHatchColor(baseColor,
                    ReadColor(Application.Current.Resources[NpcSelectionIndicatorRules.OutfitNoteHatchBrushKey] as SolidColorBrush));
            if (hatchColor is { } hatch)
            {
                indicatorBrush = GetHatchedBrush(baseColor, hatch);
                hatched = true;
            }
        }

        npcVM.SelectionIndicatorBrush = indicatorBrush;
        npcVM.SelectionIndicatorTextBrush = solid;
        npcVM.SelectionIndicatorIsHatched = hatched;
        npcVM.SelectionIndicatorNote = NpcSelectionIndicatorRules.BuildNote(state, outfitNote, conflicts, storedChoice);
    }

    /// <summary>The brush's colour, or null when this thread may not read it (an unfrozen brush
    /// belongs to the thread that created it).</summary>
    private static Color? ReadColor(SolidColorBrush? brush) =>
        brush != null && (brush.IsFrozen || brush.CheckAccess()) ? brush.Color : null;

    /// <summary>Removes the indicator of an NPC whose selection was cleared.</summary>
    private void ClearSelectionIndicator(VM_NpcsMenuSelection npcVM)
    {
        npcVM.SelectionIndicatorBrush = null;
        npcVM.SelectionIndicatorTextBrush = null;
        npcVM.SelectionIndicatorIsHatched = false;
        npcVM.SelectionIndicatorNote = null;
        _wigConflictScanner?.Forget(npcVM.NpcFormKey);
    }

    private TemplatedOutfitNote ResolveOutfitNote(VM_NpcsMenuSelection npcVM, IndicatorMod mod)
    {
        bool includeOutfit = NpcSelectionIndicatorRules.IncludeOutfitInEffect(
            mod.Vm.IncludeOutfits,
            _settings.NpcOutfitOverrides.TryGetValue(npcVM.NpcFormKey, out var outfitOverride)
                ? outfitOverride
                : null);

        if (!includeOutfit || !npcVM.WinningOverrideDressedByTemplate) return TemplatedOutfitNote.None;

        // Which of the two cases applies is Settings' to say, and it says it of the record.
        bool directiveReplaces = _settings.SkyPatcherDirectivesAvailable;
        var linkCache = _environmentStateProvider.LinkCache;
        if (linkCache != null && linkCache.TryResolve<INpcGetter>(npcVM.NpcFormKey, out var winningRecord))
        {
            directiveReplaces = _settings.OutfitDirectiveReplacesTemplatedInventory(winningRecord);
        }

        return NpcSelectionIndicatorRules.ResolveOutfitNote(includeOutfit, dressedByTemplate: true, directiveReplaces);
    }
}
