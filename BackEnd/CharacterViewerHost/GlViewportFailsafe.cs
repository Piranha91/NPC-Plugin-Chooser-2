using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using NPC_Plugin_Chooser_2.View_Models;
using NPC_Plugin_Chooser_2.Views;
using OpenTK.Wpf;

namespace NPC_Plugin_Chooser_2.BackEnd.CharacterViewerHost;

/// <summary>
/// Keeps a failing on-screen 3D viewport from taking the whole app down.
///
/// <para>Every interactive 3D view (live tiles, the Settings-tab preview, the 3D preview and
/// compare popups) is an OpenTK <see cref="GLWpfControl"/>, which shares its GL output with WPF
/// through a D3D9Ex surface. On some machines (hybrid-GPU laptops, Remote Desktop, VMs) the
/// D3D9 calls fail, e.g. <c>CreateDepthStencilSurface</c> returning E_INVALIDARG. The failure is
/// thrown from inside <c>GLWpfControl.OnRender</c> during WPF layout, so none of our code is on
/// the stack to catch it. Unhandled, it killed the app, and since live tiles restore the last
/// selected NPC at startup, it killed every later launch too.</para>
///
/// <para>Handled here instead: the exception is marked handled, every GLWpfControl in every open
/// window is collapsed (a collapsed element is neither arranged nor rendered, so the failure
/// cannot repeat), and live tiles plus the Settings preview are switched off so no new viewport
/// is created in their place. Live tiles stay off (persisted) because the same machine fails the
/// same way on the next launch. The off-screen mugshot renderer never touches D3D9 interop and
/// keeps working.</para>
/// </summary>
public static class GlViewportFailsafe
{
    // Backstop against an endless handled-exception loop if a viewport somehow keeps rendering
    // after being collapsed: past this many, stop handling and let the crash path take over.
    private const int MaxHandledFailures = 25;

    private static int _handledFailures;
    private static bool _userNotified;

    /// <summary>Hook for <see cref="Application.DispatcherUnhandledException"/>. Register it
    /// after the crash logger so the failure is still recorded in CrashLog.txt.</summary>
    public static void OnDispatcherUnhandledException(
        Func<VM_Settings?> resolveSettings, DispatcherUnhandledExceptionEventArgs e)
    {
        if (e.Handled || !IsGlViewportFailure(e.Exception)) return;
        if (++_handledFailures > MaxHandledFailures) return;

        e.Handled = true;

        CollapseAllGlViewports();

        // Defer the settings change and the dialog: we are inside a layout/render pass, and
        // both would re-enter layout (tile rebuild) or pump a nested message loop (ShowDialog).
        var exception = e.Exception;
        Dispatcher.CurrentDispatcher.InvokeAsync(() =>
        {
            DisableViewportFeatures(resolveSettings);
            NotifyUserOnce(exception);
        }, DispatcherPriority.Background);
    }

    /// <summary>True when the exception (or an inner one) was thrown from OpenTK's WPF
    /// interop layer (namespace <c>OpenTK.Wpf</c>, which includes its <c>Interop</c> types).</summary>
    public static bool IsGlViewportFailure(Exception? ex)
    {
        for (var current = ex; current != null; current = current.InnerException)
        {
            var frames = new StackTrace(current, false).GetFrames();
            if (frames.Any(f => f.GetMethod()?.DeclaringType?.Namespace?
                    .StartsWith("OpenTK.Wpf", StringComparison.Ordinal) == true))
            {
                return true;
            }
        }
        return false;
    }

    private static void CollapseAllGlViewports()
    {
        foreach (Window window in Application.Current.Windows)
        {
            try { CollapseGlViewportsUnder(window); }
            catch (Exception ex)
            {
                Debug.WriteLine($"GlViewportFailsafe: could not walk '{window.Title}': {ex.Message}");
            }
        }
    }

    private static void CollapseGlViewportsUnder(DependencyObject node)
    {
        if (node is GLWpfControl gl)
        {
            gl.Visibility = Visibility.Collapsed;
            return;
        }
        int count = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < count; i++)
        {
            CollapseGlViewportsUnder(VisualTreeHelper.GetChild(node, i));
        }
    }

    private static void DisableViewportFeatures(Func<VM_Settings?> resolveSettings)
    {
        try
        {
            var settings = resolveSettings();
            if (settings == null) return;
            // Persists to the model and rebuilds the current NPC's tiles without a live tile.
            settings.EnableLiveTilesOnSelection = false;
            settings.InternalShowPreview = false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"GlViewportFailsafe: could not disable 3D viewports: {ex.Message}");
        }
    }

    private static void NotifyUserOnce(Exception ex)
    {
        if (_userNotified) return;
        _userNotified = true;
        try
        {
            ScrollableMessageBox.ShowWarning(
                "N.P.C.2 could not display an interactive 3D view on this PC's graphics setup, " +
                "so it has been closed and Live Tiles have been turned off.\n\n" +
                "Mugshot images and mugshot generation are not affected. Interactive 3D previews " +
                "will likely fail the same way on this setup.\n\n" +
                "If this is a laptop with two graphics cards, setting N.P.C.2 to use the " +
                "high-performance GPU in Windows Graphics settings may fix it.\n\n" +
                $"Details: {ex.GetType().Name}: {ex.Message}",
                "3D View Unavailable");
        }
        catch (Exception dialogEx)
        {
            Debug.WriteLine($"GlViewportFailsafe: could not show notice: {dialogEx.Message}");
        }
    }
}
