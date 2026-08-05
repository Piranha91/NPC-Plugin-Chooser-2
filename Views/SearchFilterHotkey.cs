using System;
using System.Windows;
using System.Windows.Input;
using NPC_Plugin_Chooser_2.View_Models;

namespace NPC_Plugin_Chooser_2.Views
{
    /// <summary>
    /// Wires up the global "clear the active search menu" hotkey, Ctrl+Shift+C.
    ///
    /// Hooked at the WINDOW level rather than on the individual views: a UserControl's
    /// PreviewKeyDown / InputBindings only see the key when focus is already inside that
    /// control, so a view-level binding silently does nothing when focus sits on a tab
    /// button or elsewhere in the chrome. The auto-advance hotkey in
    /// <see cref="NpcsView"/> is attached the same way for the same reason.
    ///
    /// Ctrl+Shift+C (rather than plain Shift+C) so the shortcut also works while the
    /// caret is inside a search box — the place you are most likely to want it. WPF's
    /// TextBox does not bind Ctrl+Shift+C, so nothing is being shadowed.
    /// </summary>
    public static class SearchFilterHotkey
    {
        /// <summary>
        /// Attaches the hotkey to <paramref name="window"/>. <paramref name="resolveTarget"/> is
        /// evaluated on each keypress rather than captured once, so a host whose active view model
        /// changes over time (the main window's tabs) always clears the view that is on screen now.
        /// Returns a disposable that detaches the handler.
        /// </summary>
        public static IDisposable Attach(Window window, Func<ISearchFilterHost?> resolveTarget)
        {
            if (window == null) throw new ArgumentNullException(nameof(window));
            if (resolveTarget == null) throw new ArgumentNullException(nameof(resolveTarget));

            void Handler(object sender, KeyEventArgs e)
            {
                if (e.Key != Key.C) return;
                const ModifierKeys required = ModifierKeys.Control | ModifierKeys.Shift;
                if ((e.KeyboardDevice.Modifiers & required) != required) return;
                // Alt+Ctrl+Shift+C is not ours — let it fall through rather than swallowing it.
                if ((e.KeyboardDevice.Modifiers & ModifierKeys.Alt) != 0) return;

                var target = resolveTarget();
                if (target == null) return;

                target.ClearSearchFilters();
                e.Handled = true;
            }

            window.PreviewKeyDown += Handler;
            return new Detacher(() => window.PreviewKeyDown -= Handler);
        }

        private sealed class Detacher : IDisposable
        {
            private Action? _detach;
            public Detacher(Action detach) => _detach = detach;

            public void Dispose()
            {
                _detach?.Invoke();
                _detach = null;
            }
        }
    }
}
