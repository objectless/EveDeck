using System.Text;
using System.Windows;
using Clipboard = System.Windows.Clipboard;
using EveDeck.Models;

namespace EveDeck.ViewModels;

public sealed partial class MainWindowViewModel
{
    // ── Layout preview ─────────────────────────────────────────────────────────

    private void RebuildLayoutPreview()
    {
        LayoutPreviewSlots.Clear();
        if (SelectedProfile is null || SelectedProfile.Slots.Count == 0) return;

        var minX = SelectedProfile.Slots.Min(s => s.X);
        var minY = SelectedProfile.Slots.Min(s => s.Y);
        var maxX = SelectedProfile.Slots.Max(s => s.X + s.Width);
        var maxY = SelectedProfile.Slots.Max(s => s.Y + s.Height);
        var scale = Math.Min(300.0 / Math.Max(1, maxX - minX), 190.0 / Math.Max(1, maxY - minY));

        // Biggest rect first (painted at the back): when slots overlap -- e.g. corner previews dragged
        // on top of a dominant master rect -- the smaller cells should sit visually above the larger
        // one instead of a big master cell blotting them out. See RebuildMiniMap for the interactive
        // (drag-drop hit-testing) version of the same fix.
        foreach (var group in SelectedProfile.Slots
            .OrderByDescending(s => (long)s.Width * s.Height).ThenBy(s => s.SlotNumber)
            .GroupBy(s => $"{s.X},{s.Y},{s.Width},{s.Height}"))
        {
            var slot = group.First();
            LayoutPreviewSlots.Add(new LayoutSlotPreview
            {
                SlotNumber = slot.SlotNumber,
                DisplayText = string.Join("/", group.Select(s => s.SlotNumber)),
                Label = string.IsNullOrWhiteSpace(slot.Label) ? $"Slot {slot.SlotNumber}" : slot.Label,
                X = (slot.X - minX) * scale + 8,
                Y = (slot.Y - minY) * scale + 8,
                Width = Math.Max(18, slot.Width * scale),
                Height = Math.Max(16, slot.Height * scale)
            });
        }

        RebuildMiniMap();
    }

    internal void RebuildMiniMap()
    {
        MiniMapSlots.Clear();
        if (SelectedProfile is null || SelectedProfile.Slots.Count == 0) return;

        var minX = SelectedProfile.Slots.Min(s => s.X);
        var minY = SelectedProfile.Slots.Min(s => s.Y);
        var maxX = SelectedProfile.Slots.Max(s => s.X + s.Width);
        var maxY = SelectedProfile.Slots.Max(s => s.Y + s.Height);
        var scale = Math.Min(320.0 / Math.Max(1, maxX - minX), 196.0 / Math.Max(1, maxY - minY));

        // Grid profiles show the at-rest seat arrangement: master in the centre, each corner showing its
        // home seat. Non-grid (single/stacked) layouts keep the simple seat-number == position mapping.
        var grid = SelectedProfile.SupportsCornerGrid;
        var center = grid ? CenterSlotNumber : -1;
        var master = ActiveMasterSeat;
        var arrangement = grid ? ComputeHomeArrangement() : null;

        // Biggest rect first (painted/hit-tested at the back): a dominant master cell can now be
        // dragged-over by corner slots (see project-overlay-persist-preview-opacity), so its minimap
        // cell must not swallow drag-drop hit-testing for the smaller cells sitting on top of it.
        // Canvas gives the LAST-added child hit-test priority for overlapping regions, so smaller
        // cells need to be added after the larger ones they overlap.
        foreach (var group in SelectedProfile.Slots
            .OrderByDescending(s => (long)s.Width * s.Height).ThenBy(s => s.SlotNumber)
            .GroupBy(s => $"{s.X},{s.Y},{s.Width},{s.Height}"))
        {
            var slot = group.First();
            var isCenter = grid && slot.SlotNumber == center;
            var occupantSeat = grid
                ? (isCenter ? master : arrangement!.GetValueOrDefault(slot.SlotNumber, slot.SlotNumber))
                : slot.SlotNumber;
            var assignment = Assignments.FirstOrDefault(a => a.SlotNumber == occupantSeat);

            MiniMapSlots.Add(new MiniMapSlot
            {
                SlotNumber = slot.SlotNumber,
                X = (slot.X - minX) * scale + 10,
                Y = (slot.Y - minY) * scale + 10,
                Width = Math.Max(30, slot.Width * scale),
                Height = Math.Max(22, slot.Height * scale),
                PositionCode = isCenter ? "Master" : (grid ? CornerCode(slot.SlotNumber) : assignment?.PositionCode ?? slot.SlotNumber.ToString()),
                AssignedLabel = assignment?.DisplayLabel ?? $"Slot {slot.SlotNumber}",
                IsAssigned = assignment?.IsAssigned ?? false,
                OccupantSeat = occupantSeat,
                IsCenter = isCenter,
                IsMaster = isCenter,
            });
        }
    }

    // ── Monitor preview ────────────────────────────────────────────────────────

    private void RebuildMonitorPreview()
    {
        MonitorPreviewItems.Clear();
        if (Monitors.Count == 0) return;

        var minX = Monitors.Min(m => m.Bounds.X);
        var minY = Monitors.Min(m => m.Bounds.Y);
        var maxX = Monitors.Max(m => m.Bounds.X + m.Bounds.Width);
        var maxY = Monitors.Max(m => m.Bounds.Y + m.Bounds.Height);
        var scale = Math.Min(540.0 / Math.Max(1, maxX - minX), 220.0 / Math.Max(1, maxY - minY));

        foreach (var monitor in Monitors.OrderByDescending(m => m.IsPrimary).ThenBy(m => m.Id))
        {
            MonitorPreviewItems.Add(new MonitorPreviewItem
            {
                Label = $"{monitor.Id}  {monitor.Bounds.Width}x{monitor.Bounds.Height}  {monitor.ScalePercent}%",
                X = (monitor.Bounds.X - minX) * scale + 8,
                Y = (monitor.Bounds.Y - minY) * scale + 8,
                Width = Math.Max(40, monitor.Bounds.Width * scale),
                Height = Math.Max(30, monitor.Bounds.Height * scale),
                IsPrimary = monitor.IsPrimary
            });
        }
    }

    // ── Diagnostics ────────────────────────────────────────────────────────────

    private void CopyDiagnostics()
    {
        var builder = new StringBuilder();
        builder.AppendLine("EveDeck diagnostics");
        builder.AppendLine($"Config: {_configService.ConfigPath}");
        builder.AppendLine($"UsePhysicalPixels: {UsePhysicalPixels}");
        builder.AppendLine();
        builder.AppendLine("Monitors:");
        foreach (var monitor in Monitors)
            builder.AppendLine($"- {monitor.Summary} primary={monitor.IsPrimary}");
        builder.AppendLine();
        builder.AppendLine("Windows:");
        foreach (var window in Windows)
            builder.AppendLine($"- {window.Title} pid={window.ProcessId} hwnd={window.HandleHex} rect={window.Rect} monitor={window.MonitorId}");
        builder.AppendLine();
        builder.AppendLine($"Active profile: {SelectedProfile?.Name}");
        if (SelectedProfile is not null)
        {
            foreach (var slot in SelectedProfile.Slots)
                builder.AppendLine($"- slot {slot.SlotNumber}: {slot.X},{slot.Y} {slot.Width}x{slot.Height} borderless={slot.Borderless}");
        }
        builder.AppendLine();
        builder.AppendLine("Slot assignments:");
        foreach (var assignment in Assignments)
        {
            builder.AppendLine($"- slot {assignment.SlotNumber} ({assignment.Label}): {string.Join(", ", assignment.AssignedWindows.Select(e => e.Title))}");
        }
        builder.AppendLine();
        builder.AppendLine("Recent errors:");
        foreach (var error in Logs.Where(l => l.Level == "Error").Take(20))
            builder.AppendLine(error.Display);

        Clipboard.SetText(builder.ToString());
        Log.Info("Copied diagnostics to clipboard.");
    }
}
