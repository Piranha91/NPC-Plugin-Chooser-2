// ReactiveUI 20+ modularization shim.
//
// Bumping Mutagen.Bethesda.WPF to 0.54.0 pulls ReactiveUI 23.x (via Noggog.WPF), which removed
// the static `RxApp` class and moved its schedulers to `ReactiveUI.RxSchedulers`. This project
// only ever uses RxApp.MainThreadScheduler and RxApp.TaskpoolScheduler, both of which exist
// verbatim on RxSchedulers, so aliasing RxApp -> RxSchedulers keeps every existing call site
// (`RxApp.MainThreadScheduler`, etc.) compiling unchanged.
//
// (The `DisposeWith` extension also moved out of System.Reactive in this bump, but Noggog.CSharpExt
//  injects its own global `Noggog.IDisposableExt.DisposeWith` with an identical signature, so those
//  call sites continue to resolve without a shim.)
global using RxApp = ReactiveUI.RxSchedulers;
