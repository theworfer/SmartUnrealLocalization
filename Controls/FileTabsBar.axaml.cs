using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.VisualTree;
using SmartUnrealLocalization.ViewModels;
using SmartUnrealLocalization.Views;

namespace SmartUnrealLocalization.Controls
{
    /// <summary>
    /// Horizontal scrollable file-tab strip (Etap 13). Bound to
    /// <c>MainWindowViewModel.OpenSessions</c>; each tab drives the VM's
    /// ActivateTab / CloseTab / TogglePin. Left-click activates, the × closes,
    /// the pin toggles.
    ///
    /// <para>
    /// Drag-reorder is browser-style: while the left button is held the dragged
    /// tab follows the pointer via a render-only TranslateTransform and its
    /// neighbours slide aside; the collection is NOT touched until release.
    /// (Reordering live during the drag rebuilt the ItemsControl containers,
    /// which destroyed the pointer capture mid-drag — the release event then
    /// never arrived and plain hovering kept reordering tabs.)
    /// </para>
    /// </summary>
    public partial class FileTabsBar : UserControl
    {
        public FileTabsBar() => InitializeComponent();

        private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

        // ── Drag-reorder state ───────────────────────────────────────────────
        // A press records the candidate; a small move turns it into a drag;
        // release either commits the reorder (dragged) or activates (click).
        private OpenFileSession? _pressSession;
        private double _pressStartX;
        private bool _dragging;
        private const double DragThreshold = 5;
        private const double TabSpacing = 0; // tabs touch; the in-template divider provides the gap

        /// <summary>Geometry of one tab captured at drag start (positions are
        /// relative to this control and stay fixed for the whole drag).</summary>
        private sealed record TabSlot(OpenFileSession Session, Control Container, double X, double Width)
        {
            public double Center => X + Width / 2;
        }

        private List<TabSlot>? _slots;
        private int _dragIndex;   // dragged tab's index at drag start
        private int _targetIndex; // slot it would land in if released now

        private void OnTabPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            // A second button pressed mid-drag (right/middle while left is held)
            // re-enters this handler; re-baselining _pressStartX with the drag
            // transforms still applied would corrupt the captured geometry.
            if (_pressSession is not null) return;
            if (sender is not Control c || c.DataContext is not OpenFileSession session) return;
            if (DataContext is not MainWindowViewModel) return;

            // Let the pin / close buttons handle their own clicks (their press
            // bubbles up to this Border too).
            if (e.Source is Visual v && v.GetSelfAndVisualAncestors().OfType<Button>().Any())
                return;
            if (!e.GetCurrentPoint(c).Properties.IsLeftButtonPressed) return;

            // Capture so Moved/Released keep arriving here while the pointer
            // crosses sibling tabs. Activation is deferred to release (so a
            // drag doesn't also count as a click).
            _pressSession = session;
            _pressStartX = e.GetPosition(this).X;
            _dragging = false;
            e.Pointer.Capture(c);
            e.Handled = true;
        }

        private void OnTabPointerMoved(object? sender, PointerEventArgs e)
        {
            if (_pressSession is null || DataContext is not MainWindowViewModel vm) return;

            // Stale press (button already up — e.g. the release landed elsewhere
            // after a lost capture): hard-reset so hovering can never reorder.
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                CancelDrag();
                return;
            }

            var pos = e.GetPosition(this);
            if (!_dragging)
            {
                if (Math.Abs(pos.X - _pressStartX) < DragThreshold) return;
                if (!BeginDrag(vm)) return;
                _dragging = true;
            }
            UpdateDrag(pos.X);
        }

        private void OnTabPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (_pressSession is not null && DataContext is MainWindowViewModel vm)
            {
                if (_dragging) FinishDrag(vm);          // land in the computed slot
                else _ = vm.ActivateTab(_pressSession); // plain click → activate
            }
            ResetPress();
            // Release capture LAST: Capture(null) fires PointerCaptureLost
            // synchronously, and the handler must see the already-reset state.
            if (ReferenceEquals(e.Pointer.Captured, sender)) e.Pointer.Capture(null);
        }

        private void OnTabCaptureLost(object? sender, PointerCaptureLostEventArgs e) => CancelDrag();

        // ── Drag mechanics ───────────────────────────────────────────────────

        /// <summary>Snapshot every tab's geometry and lift the dragged tab.</summary>
        private bool BeginDrag(MainWindowViewModel vm)
        {
            if (_pressSession is null) return false;
            var host = this.FindControl<ItemsControl>("TabsHost");
            if (host is null) return false;

            var sessions = vm.OpenSessions;
            _dragIndex = sessions.IndexOf(_pressSession);
            if (_dragIndex < 0) return false;

            var slots = new List<TabSlot>(sessions.Count);
            for (int i = 0; i < sessions.Count; i++)
            {
                if (host.ContainerFromIndex(i) is not Control cont) return false;
                if (cont.TranslatePoint(new Point(0, 0), this) is not { } origin) return false;
                slots.Add(new TabSlot(sessions[i], cont, origin.X, cont.Bounds.Width));
            }
            _slots = slots;
            _targetIndex = _dragIndex;
            slots[_dragIndex].Container.ZIndex = 100; // float above neighbours
            // Hide the lifted tab's own divider (it lives inside the translated
            // subtree and would float along with the tab).
            slots[_dragIndex].Container.Classes.Add("dragging");

            // Chrome behaviour: starting to drag an inactive tab switches to it,
            // so the tab under your pointer is the highlighted/active one. Lazy
            // (not-yet-loaded) tabs are skipped — loading mid-drag could pop a
            // validation modal; they activate on drop instead (see FinishDrag).
            if (_pressSession.IsLoaded && !ReferenceEquals(vm.ActiveSession, _pressSession))
                _ = vm.ActivateTab(_pressSession);
            return true;
        }

        /// <summary>Move the dragged tab with the pointer, slide neighbours
        /// aside, and recompute the landing slot. Pure RenderTransforms —
        /// layout and the collection stay untouched.</summary>
        private void UpdateDrag(double pointerX)
        {
            if (_slots is null) return;
            var drag = _slots[_dragIndex];
            bool pinned = drag.Session.IsPinned;

            // Keep the dragged tab inside its pinned/unpinned group's extent.
            var group = _slots.Where(s => s.Session.IsPinned == pinned).ToList();
            double minX = group.Min(s => s.X);
            double maxRight = group.Max(s => s.X + s.Width);
            double dx = pointerX - _pressStartX;
            dx = Math.Clamp(dx, minX - drag.X, maxRight - (drag.X + drag.Width));

            drag.Container.RenderTransform = new TranslateTransform(dx, 0);

            double dragCenter = drag.Center + dx;
            double shift = drag.Width + TabSpacing;
            int passedRight = 0, passedLeft = 0;
            for (int i = 0; i < _slots.Count; i++)
            {
                if (i == _dragIndex) continue;
                var slot = _slots[i];
                double offset = 0;
                if (slot.Session.IsPinned == pinned)
                {
                    if (i > _dragIndex && dragCenter > slot.Center) { offset = -shift; passedRight++; }
                    else if (i < _dragIndex && dragCenter < slot.Center) { offset = shift; passedLeft++; }
                }
                slot.Container.RenderTransform =
                    offset == 0 ? null : new TranslateTransform(offset, 0);
            }
            _targetIndex = _dragIndex + passedRight - passedLeft;
        }

        /// <summary>Drop: clear the visuals, apply the single collection Move.</summary>
        private void FinishDrag(MainWindowViewModel vm)
        {
            // Snapshot first: OpenSessions.Move rebuilds the item containers,
            // which kills the pointer capture and synchronously re-enters
            // OnTabCaptureLost → ResetPress, nulling _pressSession under us.
            var session = _pressSession;
            ClearDragVisuals();
            int from = session is null ? -1 : vm.OpenSessions.IndexOf(session);
            if (from >= 0 && _targetIndex != from &&
                _targetIndex >= 0 && _targetIndex < vm.OpenSessions.Count)
                vm.OpenSessions.Move(from, _targetIndex);
            vm.CommitTabOrder(); // pinned-first safety net + persist
            // Lazy tabs couldn't activate at drag start — do it now (Chrome
            // always ends a drag with the dragged tab active).
            if (session is not null && !ReferenceEquals(vm.ActiveSession, session))
                _ = vm.ActivateTab(session);
            _slots = null;
        }

        /// <summary>Abort (capture lost / button found up): restore visuals, drop state.</summary>
        private void CancelDrag()
        {
            ClearDragVisuals();
            _slots = null;
            ResetPress();
        }

        private void ClearDragVisuals()
        {
            if (_slots is null) return;
            foreach (var s in _slots)
            {
                s.Container.RenderTransform = null;
                s.Container.ZIndex = 0;
                s.Container.Classes.Remove("dragging");
            }
        }

        private void ResetPress()
        {
            _pressSession = null;
            _dragging = false;
        }

        private void OnTabClose(object? sender, RoutedEventArgs e)
        {
            if (sender is Control c && c.DataContext is OpenFileSession s &&
                DataContext is MainWindowViewModel vm)
                _ = vm.CloseTab(s);
        }

        private void OnTabPin(object? sender, RoutedEventArgs e)
        {
            if (sender is Control c && c.DataContext is OpenFileSession s &&
                DataContext is MainWindowViewModel vm)
                vm.TogglePin(s);
        }

        /// <summary>"+" after the last tab → the regular Open-file flow.</summary>
        private void OnNewTab(object? sender, RoutedEventArgs e) =>
            (TopLevel.GetTopLevel(this) as MainWindow)?.RequestOpenFile();

        /// <summary>Vertical wheel over the strip scrolls the tabs horizontally
        /// (browser behaviour — a horizontal-only strip never gets Y-wheel otherwise).</summary>
        private void OnTabsWheel(object? sender, PointerWheelEventArgs e)
        {
            if (sender is not ScrollViewer sv) return;
            double delta = Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y) ? e.Delta.X : e.Delta.Y;
            if (delta == 0) return;
            sv.Offset = new Vector(sv.Offset.X - delta * 48, sv.Offset.Y);
            e.Handled = true;
        }
    }
}
