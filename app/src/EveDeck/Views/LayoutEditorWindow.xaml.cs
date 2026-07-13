using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using EveDeck.Models;
using EveDeck.Utilities;
using Border = System.Windows.Controls.Border;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Key = System.Windows.Input.Key;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Orientation = System.Windows.Controls.Orientation;
using Rectangle = System.Windows.Shapes.Rectangle;
using TextBlock = System.Windows.Controls.TextBlock;

namespace EveDeck.Views;

// One slot being edited, in ABSOLUTE PHYSICAL pixels (same space as LayoutSlot on a captured
// custom profile). The editor window mutates X/Y/Width/Height; the caller reads ResultSlots back.
public sealed class LayoutEditorSlot
{
    public int SlotNumber { get; set; }
    public string Label { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
}

// WYSIWYG slot placement editor: covers the ENTIRE virtual desktop (all monitors) with a
// translucent overlay showing each slot at real scale. Drag to move, drag edges/corners to
// resize, toolbar to add/remove slots and toggle snapping. All geometry is tracked in physical
// pixels and converted to DIPs only for rendering, matching how layout apply positions windows.
public partial class LayoutEditorWindow : Window
{
    private const int EdgeGripPx = 14;        // physical px border zone that triggers resize
    private const int SnapPx = 16;            // physical px snap threshold
    private const int MinSlotW = 320;
    private const int MinSlotH = 180;
    private const int GridCols = 32;
    private const int GridRows = 18;
    private const int CascadePx = 64;         // offset applied to fully-overlapping slots on open

    private sealed class SlotVisual
    {
        public required LayoutEditorSlot Model { get; init; }
        public required Border Border { get; init; }
        public required TextBlock NumberText { get; init; }
        public required Color Hue { get; init; }
    }

    [Flags]
    private enum DragMode { None = 0, Move = 1, Left = 2, Right = 4, Top = 8, Bottom = 16 }

    private readonly IReadOnlyList<MonitorInfo> _monitors;
    private readonly MonitorInfo _targetMonitor;
    private readonly WindowRect _bounds;      // union of all monitor bounds (virtual desktop)
    private readonly List<SlotVisual> _slots = new();
    private double _dpi = 1.0;
    private SlotVisual? _selected;
    private DragMode _drag = DragMode.None;
    private System.Windows.Point _dragStartDip;
    private (int X, int Y, int W, int H) _dragStartRect;
    private MonitorInfo _dragMonitor;         // monitor whose grid the current drag snaps to

    public List<LayoutEditorSlot> ResultSlots { get; } = new();

    public LayoutEditorWindow(IReadOnlyList<MonitorInfo> monitors, MonitorInfo targetMonitor, IEnumerable<LayoutEditorSlot> slots)
    {
        InitializeComponent();
        _monitors = monitors;
        _targetMonitor = targetMonitor;
        _dragMonitor = targetMonitor;

        var minX = monitors.Min(m => m.Bounds.X);
        var minY = monitors.Min(m => m.Bounds.Y);
        _bounds = new WindowRect
        {
            X = minX,
            Y = minY,
            Width = monitors.Max(m => m.Bounds.X + m.Bounds.Width) - minX,
            Height = monitors.Max(m => m.Bounds.Y + m.Bounds.Height) - minY,
        };

        Loaded += (_, _) =>
        {
            _dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
            DrawMonitorOutlines();

            // Profiles like Stacked put every slot on the same rect - cascade duplicates apart so
            // each one can actually be grabbed (positions are meaningless while stacked anyway).
            var seen = new Dictionary<string, int>();
            foreach (var slot in slots.OrderBy(s => s.SlotNumber))
            {
                var key = $"{slot.X},{slot.Y},{slot.Width},{slot.Height}";
                var dupIndex = seen.GetValueOrDefault(key, 0);
                seen[key] = dupIndex + 1;
                if (dupIndex > 0)
                {
                    slot.X = Math.Min(slot.X + dupIndex * CascadePx, _bounds.X + _bounds.Width - slot.Width);
                    slot.Y = Math.Min(slot.Y + dupIndex * CascadePx, _bounds.Y + _bounds.Height - slot.Height);
                }
                AddVisual(slot);
            }
            Focus();
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        Win32Native.SetWindowPos(hwnd, 0, _bounds.X, _bounds.Y, _bounds.Width, _bounds.Height,
            Win32Native.SwpNoZOrder | Win32Native.SwpShowWindow);
    }

    // ── Physical px <-> DIP (canvas) conversion ─────────────────────────────────

    private double ToDipX(double physX) => (physX - _bounds.X) / _dpi;
    private double ToDipY(double physY) => (physY - _bounds.Y) / _dpi;
    private double ToPhysX(double dipX) => dipX * _dpi + _bounds.X;
    private double ToPhysY(double dipY) => dipY * _dpi + _bounds.Y;

    private MonitorInfo MonitorAt(int physX, int physY) =>
        _monitors.FirstOrDefault(m => physX >= m.Bounds.X && physX < m.Bounds.X + m.Bounds.Width
                                   && physY >= m.Bounds.Y && physY < m.Bounds.Y + m.Bounds.Height)
        ?? _targetMonitor;

    // ── Monitor outlines ─────────────────────────────────────────────────────────

    private void DrawMonitorOutlines()
    {
        foreach (var m in _monitors)
        {
            var rect = new Rectangle
            {
                Stroke = new SolidColorBrush(Color.FromArgb(0x66, 0x8C, 0xA0, 0xB8)),
                StrokeThickness = 1.5,
                StrokeDashArray = new DoubleCollection { 6, 4 },
                Width = Math.Max(4, m.Bounds.Width / _dpi - 2),
                Height = Math.Max(4, m.Bounds.Height / _dpi - 2),
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(rect, ToDipX(m.Bounds.X) + 1);
            Canvas.SetTop(rect, ToDipY(m.Bounds.Y) + 1);
            EditorCanvas.Children.Add(rect);

            var label = new TextBlock
            {
                Text = $"{m.DeviceName} {m.Bounds.Width}x{m.Bounds.Height}" + (m.Id == _targetMonitor.Id ? "  (target)" : ""),
                Foreground = new SolidColorBrush(Color.FromArgb(0x99, 0x8C, 0xA0, 0xB8)),
                FontSize = 13,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(label, ToDipX(m.Bounds.X) + 10);
            Canvas.SetTop(label, ToDipY(m.Bounds.Y + m.Bounds.Height) - 28);
            EditorCanvas.Children.Add(label);
        }
    }

    // ── Slot visuals ─────────────────────────────────────────────────────────────

    // Distinct hue per slot (golden-angle rotation) so overlapping slots stay tellable-apart.
    private static Color SlotHue(int slotNumber)
    {
        var h = slotNumber * 137.508 % 360.0;
        var (r, g, b) = HsvToRgb(h, 0.65, 1.0);
        return Color.FromRgb(r, g, b);
    }

    private static (byte R, byte G, byte B) HsvToRgb(double h, double s, double v)
    {
        var c = v * s;
        var x = c * (1 - Math.Abs(h / 60.0 % 2 - 1));
        var m = v - c;
        var (r, g, b) = ((int)(h / 60.0)) switch
        {
            0 => (c, x, 0.0),
            1 => (x, c, 0.0),
            2 => (0.0, c, x),
            3 => (0.0, x, c),
            4 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return ((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }

    private SlotVisual AddVisual(LayoutEditorSlot model)
    {
        var hue = SlotHue(model.SlotNumber);
        var number = new TextBlock
        {
            Text = model.SlotNumber.ToString(),
            Foreground = Brushes.White,
            FontSize = 44,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var label = new TextBlock
        {
            Text = model.Label,
            Foreground = new SolidColorBrush(Color.FromRgb(0xE2, 0xEA, 0xF5)),
            FontSize = 15,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var stack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        stack.Children.Add(number);
        stack.Children.Add(label);

        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x24, hue.R, hue.G, hue.B)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0xD8, hue.R, hue.G, hue.B)),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(4),
            Child = stack,
        };

        var visual = new SlotVisual { Model = model, Border = border, NumberText = number, Hue = hue };
        _slots.Add(visual);
        EditorCanvas.Children.Add(border);
        UpdateVisual(visual);
        return visual;
    }

    private void UpdateVisual(SlotVisual v)
    {
        Canvas.SetLeft(v.Border, ToDipX(v.Model.X));
        Canvas.SetTop(v.Border, ToDipY(v.Model.Y));
        v.Border.Width = Math.Max(4, v.Model.Width / _dpi);
        v.Border.Height = Math.Max(4, v.Model.Height / _dpi);
        v.NumberText.Text = v.Model.SlotNumber.ToString();
    }

    private void Select(SlotVisual? visual)
    {
        if (_selected is not null)
        {
            _selected.Border.BorderBrush = new SolidColorBrush(Color.FromArgb(0xD8, _selected.Hue.R, _selected.Hue.G, _selected.Hue.B));
            _selected.Border.BorderThickness = new Thickness(2);
            _selected.Border.Background = new SolidColorBrush(Color.FromArgb(0x24, _selected.Hue.R, _selected.Hue.G, _selected.Hue.B));
        }
        _selected = visual;
        if (visual is not null)
        {
            visual.Border.BorderBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xD7, 0x80));
            visual.Border.BorderThickness = new Thickness(3);
            visual.Border.Background = new SolidColorBrush(Color.FromArgb(0x3C, visual.Hue.R, visual.Hue.G, visual.Hue.B));
            // Bring to front so its resize grips win over overlapping slots.
            EditorCanvas.Children.Remove(visual.Border);
            EditorCanvas.Children.Add(visual.Border);
            _slots.Remove(visual);
            _slots.Add(visual);
        }
    }

    // ── Hit testing ──────────────────────────────────────────────────────────────

    private SlotVisual? HitSlot(double physX, double physY)
    {
        // Topmost first (last in list is rendered on top).
        for (var i = _slots.Count - 1; i >= 0; i--)
        {
            var m = _slots[i].Model;
            if (physX >= m.X - EdgeGripPx && physX <= m.X + m.Width + EdgeGripPx
                && physY >= m.Y - EdgeGripPx && physY <= m.Y + m.Height + EdgeGripPx)
                return _slots[i];
        }
        return null;
    }

    private DragMode HitZone(LayoutEditorSlot m, double physX, double physY)
    {
        var mode = DragMode.None;
        if (Math.Abs(physX - m.X) <= EdgeGripPx) mode |= DragMode.Left;
        if (Math.Abs(physX - (m.X + m.Width)) <= EdgeGripPx) mode |= DragMode.Right;
        if (Math.Abs(physY - m.Y) <= EdgeGripPx) mode |= DragMode.Top;
        if (Math.Abs(physY - (m.Y + m.Height)) <= EdgeGripPx) mode |= DragMode.Bottom;
        return mode == DragMode.None ? DragMode.Move : mode;
    }

    // ── Mouse interaction ────────────────────────────────────────────────────────

    private void OnCanvasMouseDown(object sender, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(EditorCanvas);
        var px = ToPhysX(p.X);
        var py = ToPhysY(p.Y);
        var hit = HitSlot(px, py);
        Select(hit);
        if (hit is null) return;

        _drag = HitZone(hit.Model, px, py);
        _dragStartDip = p;
        _dragStartRect = (hit.Model.X, hit.Model.Y, hit.Model.Width, hit.Model.Height);
        _dragMonitor = MonitorAt(hit.Model.X + hit.Model.Width / 2, hit.Model.Y + hit.Model.Height / 2);
        EditorCanvas.CaptureMouse();
        e.Handled = true;
    }

    private void OnCanvasMouseMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(EditorCanvas);

        if (_drag == DragMode.None || _selected is null)
        {
            // Cursor feedback only.
            var hover = HitSlot(ToPhysX(p.X), ToPhysY(p.Y));
            if (hover is null) { Cursor = Cursors.Arrow; return; }
            var zone = HitZone(hover.Model, ToPhysX(p.X), ToPhysY(p.Y));
            Cursor = zone switch
            {
                DragMode.Move => Cursors.SizeAll,
                DragMode.Left or DragMode.Right => Cursors.SizeWE,
                DragMode.Top or DragMode.Bottom => Cursors.SizeNS,
                (DragMode.Left | DragMode.Top) or (DragMode.Right | DragMode.Bottom) => Cursors.SizeNWSE,
                (DragMode.Left | DragMode.Bottom) or (DragMode.Right | DragMode.Top) => Cursors.SizeNESW,
                _ => Cursors.Arrow,
            };
            return;
        }

        // While dragging, snap to the grid of whichever monitor the cursor is currently over.
        _dragMonitor = MonitorAt((int)ToPhysX(p.X), (int)ToPhysY(p.Y));

        var dx = (int)Math.Round((p.X - _dragStartDip.X) * _dpi);
        var dy = (int)Math.Round((p.Y - _dragStartDip.Y) * _dpi);
        var m = _selected.Model;
        var b = _bounds;

        if (_drag == DragMode.Move)
        {
            var x = _dragStartRect.X + dx;
            var y = _dragStartRect.Y + dy;
            (x, y) = SnapMove(x, y, m.Width, m.Height);
            m.X = Math.Clamp(x, b.X, b.X + b.Width - m.Width);
            m.Y = Math.Clamp(y, b.Y, b.Y + b.Height - m.Height);
        }
        else
        {
            var left = _dragStartRect.X;
            var top = _dragStartRect.Y;
            var right = _dragStartRect.X + _dragStartRect.W;
            var bottom = _dragStartRect.Y + _dragStartRect.H;

            if (_drag.HasFlag(DragMode.Left)) left = SnapCoord(left + dx, vertical: true);
            if (_drag.HasFlag(DragMode.Right)) right = SnapCoord(right + dx, vertical: true);
            if (_drag.HasFlag(DragMode.Top)) top = SnapCoord(top + dy, vertical: false);
            if (_drag.HasFlag(DragMode.Bottom)) bottom = SnapCoord(bottom + dy, vertical: false);

            // Math.Clamp throws if min > max. The upper/lower bounds below derive from the OTHER
            // edge +/- MinSlotW/H, which can invert if this slot started out smaller than the
            // editor's minimum (some family templates, e.g. Whammy Board or Side Stack at high
            // account counts, legitimately generate tiles under 320x180) -- Math.Max/Min pin the
            // bound back to the monitor edge instead of crashing in that case.
            left = Math.Clamp(left, b.X, Math.Max(b.X, right - MinSlotW));
            right = Math.Clamp(right, Math.Min(b.X + b.Width, left + MinSlotW), b.X + b.Width);
            top = Math.Clamp(top, b.Y, Math.Max(b.Y, bottom - MinSlotH));
            bottom = Math.Clamp(bottom, Math.Min(b.Y + b.Height, top + MinSlotH), b.Y + b.Height);

            m.X = left;
            m.Y = top;
            m.Width = right - left;
            m.Height = bottom - top;
        }

        UpdateVisual(_selected);
    }

    private void OnCanvasMouseUp(object sender, MouseButtonEventArgs e)
    {
        _drag = DragMode.None;
        EditorCanvas.ReleaseMouseCapture();
    }

    // ── Snapping ────────────────────────────────────────────────────────────────

    // Snap candidates for one axis: every monitor's edges plus every other slot's edges.
    private List<int> EdgeCandidates(bool vertical)
    {
        var list = new List<int>();
        foreach (var mon in _monitors)
        {
            if (vertical) { list.Add(mon.Bounds.X); list.Add(mon.Bounds.X + mon.Bounds.Width); }
            else { list.Add(mon.Bounds.Y); list.Add(mon.Bounds.Y + mon.Bounds.Height); }
        }
        foreach (var s in _slots)
        {
            if (s == _selected) continue;
            if (vertical) { list.Add(s.Model.X); list.Add(s.Model.X + s.Model.Width); }
            else { list.Add(s.Model.Y); list.Add(s.Model.Y + s.Model.Height); }
        }
        return list;
    }

    // Snap a single edge coordinate: nearest slot/monitor edge within threshold wins, else the
    // grid of the monitor being dragged over.
    private int SnapCoord(int value, bool vertical)
    {
        if (SnapEdgesCheck.IsChecked == true)
        {
            var best = int.MaxValue;
            var snapped = value;
            foreach (var c in EdgeCandidates(vertical))
            {
                var d = Math.Abs(value - c);
                if (d <= SnapPx && d < best) { best = d; snapped = c; }
            }
            if (best != int.MaxValue) return snapped;
        }
        if (SnapGridCheck.IsChecked == true)
        {
            var mb = _dragMonitor.Bounds;
            var origin = vertical ? mb.X : mb.Y;
            var cell = vertical ? (double)mb.Width / GridCols : (double)mb.Height / GridRows;
            return origin + (int)Math.Round(Math.Round((value - origin) / cell) * cell);
        }
        return value;
    }

    // Snap a moving rect: both edges of each axis compete for the nearest candidate.
    private (int X, int Y) SnapMove(int x, int y, int w, int h)
    {
        if (SnapEdgesCheck.IsChecked == true)
        {
            var bestX = int.MaxValue;
            var newX = x;
            foreach (var c in EdgeCandidates(vertical: true))
            {
                var dl = Math.Abs(x - c);
                if (dl <= SnapPx && dl < bestX) { bestX = dl; newX = c; }
                var dr = Math.Abs(x + w - c);
                if (dr <= SnapPx && dr < bestX) { bestX = dr; newX = c - w; }
            }
            var bestY = int.MaxValue;
            var newY = y;
            foreach (var c in EdgeCandidates(vertical: false))
            {
                var dt = Math.Abs(y - c);
                if (dt <= SnapPx && dt < bestY) { bestY = dt; newY = c; }
                var db = Math.Abs(y + h - c);
                if (db <= SnapPx && db < bestY) { bestY = db; newY = c - h; }
            }
            if (bestX != int.MaxValue) x = newX;
            if (bestY != int.MaxValue) y = newY;
            if (bestX != int.MaxValue && bestY != int.MaxValue) return (x, y);
        }
        if (SnapGridCheck.IsChecked == true)
        {
            var mb = _dragMonitor.Bounds;
            var cellW = (double)mb.Width / GridCols;
            var cellH = (double)mb.Height / GridRows;
            x = mb.X + (int)Math.Round(Math.Round((x - mb.X) / cellW) * cellW);
            y = mb.Y + (int)Math.Round(Math.Round((y - mb.Y) / cellH) * cellH);
        }
        return (x, y);
    }

    // ── Toolbar / keyboard ──────────────────────────────────────────────────────

    private void OnAddSlot(object sender, RoutedEventArgs e)
    {
        var b = _targetMonitor.Bounds;
        var number = _slots.Count == 0 ? 1 : _slots.Max(s => s.Model.SlotNumber) + 1;
        var model = new LayoutEditorSlot
        {
            SlotNumber = number,
            Label = $"Slot {number}",
            Width = b.Width / 4,
            Height = b.Height / 4,
            X = b.X + (b.Width - b.Width / 4) / 2,
            Y = b.Y + (b.Height - b.Height / 4) / 2,
        };
        Select(AddVisual(model));
    }

    private void OnDeleteSlot(object sender, RoutedEventArgs e) => DeleteSelected();

    private void DeleteSelected()
    {
        if (_selected is null) return;
        if (_slots.Count <= 1) return; // a profile needs at least one slot

        EditorCanvas.Children.Remove(_selected.Border);
        _slots.Remove(_selected);
        _selected = null;

        // Keep slot numbers contiguous 1..n (seat numbers, hotkeys and swap groups expect this).
        var renumbered = _slots.OrderBy(s => s.Model.SlotNumber).ToList();
        for (var i = 0; i < renumbered.Count; i++)
        {
            renumbered[i].Model.SlotNumber = i + 1;
            UpdateVisual(renumbered[i]);
        }
    }

    private void OnSave(object sender, RoutedEventArgs e) => Commit();

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Commit()
    {
        ResultSlots.Clear();
        ResultSlots.AddRange(_slots.Select(s => s.Model).OrderBy(m => m.SlotNumber));
        DialogResult = true;
        Close();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape: DialogResult = false; Close(); break;
            case Key.Enter: Commit(); break;
            case Key.Delete: DeleteSelected(); break;
        }
    }
}
