using EveWindowCommander.Models;

namespace EveWindowCommander.Services;

public static class PresetFactory
{
    private static readonly HashSet<string> DeprecatedBuiltInNames = BuildDeprecatedNames();

    // (width, height, includeTaskbarVariant) — capped at 4K
    private static readonly (int W, int H, bool Taskbar)[] Resolutions =
    {
        (1760,  990,  false),
        (1920, 1080,  true),
        (1920, 1200,  false),
        (2560, 1440,  true),
        (2816, 1584,  false),
        (3072, 1728,  false),
        (3200, 1800,  false),
        (3328, 1872,  false),
        (3840, 2160,  true),
    };

    public static IReadOnlyList<LayoutProfile> CreateBuiltInProfiles()
    {
        var list = new List<LayoutProfile>();

        foreach (var (w, h, taskbar) in Resolutions)
        {
            list.Add(Stacked($"Stacked {w}x{h}", w, h));
            if (taskbar)
                list.Add(Stacked($"Stacked {w}x{h - 40} (avoid taskbar)", w, h - 40));
        }

        foreach (var (w, h, _) in Resolutions)
            list.Add(Solo($"1-Char {w}x{h}", w, h));

        for (var n = 2; n <= 15; n++)
            foreach (var (w, h, _) in Resolutions)
                list.Add(NCharGrid($"{n}-Char Grid {w}x{h}", n, w, h));

        list.Add(Overlap());

        // Center Master family: a square ring of corner/edge tiles + one larger master floating centred
        // on top. n=5 is the classic 2×2 corners + master; higher counts add perimeter tiles and grow the
        // master to fill the hollow centre (e.g. 9 = eight tiles in a 3×3 ring with the 9th centred).
        foreach (var (w, h) in CenterMasterResolutions)
            for (var n = 4; n <= 15; n++)
                list.Add(CenterMasterGrid($"{n}-Char Center Master {w}x{h}", n, w, h));

        return list;
    }

    public static bool EnsureBuiltInProfiles(IList<LayoutProfile> profiles)
    {
        var changed = false;
        for (var i = profiles.Count - 1; i >= 0; i--)
        {
            if (DeprecatedBuiltInNames.Contains(profiles[i].Name))
            {
                profiles.RemoveAt(i);
                changed = true;
            }
        }

        foreach (var builtIn in CreateBuiltInProfiles())
        {
            var existing = profiles.FirstOrDefault(p => p.Name.Equals(builtIn.Name, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                changed |= RepairBuiltInProfile(existing, builtIn);
                continue;
            }

            profiles.Add(builtIn);
            changed = true;
        }

        return changed;
    }

    // Resolutions that ship a 5-Char Center Master preset (must mirror CreateBuiltInProfiles).
    private static readonly (int W, int H)[] CenterMasterResolutions =
    {
        (2560, 1440), (3200, 1800), (3840, 2160),
    };

    // Pick the built-in profile that best fits a given client count on a monitor of the given size.
    // Built-in profiles scale to the target monitor at apply time, so an exact resolution match isn't
    // required — we choose the nearest-by-width preset to minimise aspect distortion. Returns null
    // only if no suitable family exists (it never should for counts >= 1).
    public static string? BestProfileName(int clientCount, int monitorWidth, int monitorHeight)
    {
        if (clientCount <= 0) return null;

        if (clientCount == 1)
        {
            var (w, h) = NearestResolution(Array.ConvertAll(Resolutions, r => (r.W, r.H)), monitorWidth);
            return $"1-Char {w}x{h}";
        }

        // 4–15 : the Center Master family (big centred master + ring of tiles, fast hotkey swapping).
        if (clientCount >= 4 && clientCount <= 15)
        {
            var (w, h) = NearestResolution(CenterMasterResolutions, monitorWidth);
            return $"{clientCount}-Char Center Master {w}x{h}";
        }

        // 2–3 and >15 : the N-Char grid family (clamped to the 2..15 presets that exist).
        var count = Math.Clamp(clientCount, 2, 15);
        var (gw, gh) = NearestResolution(Array.ConvertAll(Resolutions, r => (r.W, r.H)), monitorWidth);
        return $"{count}-Char Grid {gw}x{gh}";
    }

    private static (int W, int H) NearestResolution((int W, int H)[] options, int targetWidth)
    {
        var best = options[0];
        var bestDelta = int.MaxValue;
        foreach (var opt in options)
        {
            var delta = Math.Abs(opt.W - targetWidth);
            if (delta < bestDelta) { bestDelta = delta; best = opt; }
        }
        return best;
    }

    public static bool IsBuiltInName(string name)
        => CreateBuiltInProfiles().Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public static LayoutProfile? CreateBuiltInProfile(string name)
        => CreateBuiltInProfiles().FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static bool RepairBuiltInProfile(LayoutProfile existing, LayoutProfile builtIn)
    {
        var changed = false;
        existing.IsBuiltIn = true;
        if (existing.Category != builtIn.Category) { existing.Category = builtIn.Category; changed = true; }
        if (existing.Slots.Count != builtIn.Slots.Count)
        {
            existing.Slots.Clear();
            foreach (var slot in builtIn.Slots)
                existing.Slots.Add(CloneSlot(slot));
            return true;
        }

        for (var i = 0; i < builtIn.Slots.Count; i++)
        {
            var target = existing.Slots[i];
            var source = builtIn.Slots[i];
            if (target.SlotNumber != source.SlotNumber || target.X != source.X || target.Y != source.Y
                || target.Width != source.Width || target.Height != source.Height || target.Borderless != source.Borderless
                || !target.Label.Equals(source.Label, StringComparison.Ordinal))
            {
                target.SlotNumber = source.SlotNumber;
                target.Label = source.Label;
                target.MonitorId = source.MonitorId;
                target.X = source.X;
                target.Y = source.Y;
                target.Width = source.Width;
                target.Height = source.Height;
                target.Borderless = source.Borderless;
                changed = true;
            }
        }

        return changed;
    }

    private static LayoutProfile Stacked(string name, int width, int height)
    {
        var profile = new LayoutProfile { Name = name, IsBuiltIn = true, Category = "Stacked" };
        for (var i = 1; i <= 8; i++)
            profile.Slots.Add(Slot(i, 0, 0, width, height));
        return profile;
    }

    private static LayoutProfile Solo(string name, int width, int height)
    {
        var profile = new LayoutProfile { Name = name, IsBuiltIn = true, Category = "1-Char" };
        profile.Slots.Add(Slot(1, 0, 0, width, height));
        return profile;
    }

    private static LayoutProfile NCharGrid(string name, int count, int width, int height)
    {
        var (cols, rows) = GridDimensions(count);
        var profile = new LayoutProfile { Name = name, IsBuiltIn = true, Category = $"{count}-Char Grid" };
        var baseW = width / cols;
        var baseH = height / rows;
        var slotNum = 1;
        for (var r = 0; r < rows && slotNum <= count; r++)
        {
            var y = r * baseH;
            var h = (r == rows - 1) ? height - y : baseH;
            // Slots remaining for this and future rows; this row gets up to cols.
            var slotsInRow = Math.Min(cols, count - slotNum + 1);
            // If this is a partial last row, spread its slots evenly across the full width.
            var rowBaseW = slotsInRow < cols ? width / slotsInRow : baseW;
            for (var c = 0; c < slotsInRow; c++)
            {
                var x = c * rowBaseW;
                var w = (c == slotsInRow - 1) ? width - x : rowBaseW;
                profile.Slots.Add(Slot(slotNum, x, y, w, h));
                slotNum++;
            }
        }
        return profile;
    }

    // Optimal grid dimensions for 2–15 simultaneous characters on a landscape monitor.
    private static (int cols, int rows) GridDimensions(int count) => count switch
    {
        2  => (2, 1),
        3  => (3, 1),
        4  => (2, 2),
        5  => (3, 2),
        6  => (3, 2),
        7  => (4, 2),
        8  => (4, 2),
        9  => (3, 3),
        10 => (5, 2),
        11 => (4, 3),
        12 => (4, 3),
        13 => (5, 3),
        14 => (5, 3),
        _  => (5, 3), // 15
    };

    // A square ring of (s×s) equal tiles surrounding one larger master floating centred on top.
    // Generalises the original 5-Char design (2×2 corners + a 1.5-cell master) to any count: each extra
    // client adds a perimeter tile and the master grows to fill the hollow centre. Tiles are slots 1..n-1
    // (reading order: top row L→R, side cells, bottom row L→R); the master is slot n. The master is the
    // largest rect, so CenterSlotNumber auto-detects it. On a 16:9 monitor every rect stays 16:9.
    private static LayoutProfile CenterMasterGrid(string name, int n, int sw, int sh)
    {
        var tiles = n - 1;
        var s = 2;
        while (4 * s - 4 < tiles) s++;          // smallest ring whose perimeter holds every tile

        var cellW = sw / s;
        var cellH = sh / s;

        var perimeter = PerimeterCells(s);                       // reading-order (col,row), length 4s-4
        var keep = EvenlySpacedIndices(perimeter.Count, tiles);  // spread any gaps when tiles < perimeter

        var profile = new LayoutProfile { Name = name, IsBuiltIn = true, Category = "Center Master" };

        var slotNum = 1;
        foreach (var i in keep)
        {
            var (col, row) = perimeter[i];
            var x = col * cellW;
            var y = row * cellH;
            var wpx = (col == s - 1) ? sw - x : cellW;           // last col/row absorbs rounding remainder
            var hpx = (row == s - 1) ? sh - y : cellH;
            profile.Slots.Add(Slot(slotNum, x, y, wpx, hpx, RingLabel(col, row, s)));
            slotNum++;
        }

        // Master spans the hollow centre plus a half-cell overlap on each side: 1.5 cells for the 2×2 ring
        // (matches the legacy 5-Char master exactly), otherwise (s-1) cells so it scales with the ring.
        // Numerator is doubled to keep integer math (1.5 cells -> 3/2).
        var masterCellsX2 = (s == 2) ? 3 : 2 * (s - 1);
        var masterW = masterCellsX2 * cellW / 2;
        var masterH = masterCellsX2 * cellH / 2;
        profile.Slots.Add(Slot(n, (sw - masterW) / 2, (sh - masterH) / 2, masterW, masterH, "Master"));
        return profile;
    }

    // Cells on the border of an s×s grid, in reading order (top row L→R, then each middle row's left &
    // right cells, then bottom row L→R). Length is 4s-4 for s>=2 (matches the legacy TL,TR,BL,BR order at s=2).
    private static List<(int col, int row)> PerimeterCells(int s)
    {
        var cells = new List<(int, int)>();
        for (var r = 0; r < s; r++)
            for (var c = 0; c < s; c++)
                if (r == 0 || r == s - 1 || c == 0 || c == s - 1)
                    cells.Add((c, r));
        return cells;
    }

    // Pick `take` indices spread as evenly as possible across [0, total); strictly increasing and in range.
    private static List<int> EvenlySpacedIndices(int total, int take)
    {
        var result = new List<int>();
        if (take <= 0 || total <= 0) return result;
        if (take >= total) { for (var i = 0; i < total; i++) result.Add(i); return result; }

        var last = -1;
        for (var i = 0; i < take; i++)
        {
            var idx = (int)Math.Round((double)i * total / take);
            if (idx <= last) idx = last + 1;
            if (idx > total - 1) idx = total - 1;
            last = idx;
            result.Add(idx);
        }
        return result;
    }

    private static string RingLabel(int col, int row, int s)
    {
        var top = row == 0;
        var bottom = row == s - 1;
        var left = col == 0;
        var right = col == s - 1;
        if (top && left) return "Top Left";
        if (top && right) return "Top Right";
        if (bottom && left) return "Bottom Left";
        if (bottom && right) return "Bottom Right";
        if (top) return "Top";
        if (bottom) return "Bottom";
        if (left) return "Left";
        return "Right";
    }

    private static LayoutProfile Overlap()
    {
        var profile = new LayoutProfile { Name = "Overlap 2560x1408", IsBuiltIn = true, Category = "Overlap" };
        var positions = new[] { (0, 0), (1280, 0), (0, 688), (1280, 688) };
        for (var i = 1; i <= 8; i++)
        {
            var pos = positions[(i - 1) % 4];
            profile.Slots.Add(Slot(i, pos.Item1, pos.Item2, 1280, 720));
        }
        return profile;
    }

    private static LayoutSlot Slot(int slotNumber, int x, int y, int width, int height, string? label = null)
    {
        return new LayoutSlot
        {
            SlotNumber = slotNumber,
            Label = label ?? $"Slot {slotNumber}",
            X = x,
            Y = y,
            Width = width,
            Height = height,
            Borderless = true
        };
    }

    private static LayoutSlot CloneSlot(LayoutSlot slot)
    {
        return new LayoutSlot
        {
            SlotNumber = slot.SlotNumber,
            Label = slot.Label,
            MonitorId = slot.MonitorId,
            X = slot.X,
            Y = slot.Y,
            Width = slot.Width,
            Height = slot.Height,
            Borderless = slot.Borderless
        };
    }

    private static HashSet<string> BuildDeprecatedNames()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Pre-matrix hand-crafted names
            "2560x1440 2x2 - four 1280x720 slots",
            "3200x1800 VSR 2x2 - four 1600x900 slots",
            "2560x1408 2x2 - four 1280x704 slots",
            "Stacked full width/height",
            "Stacked full width/hieght (avoid Taskbar)",
            "2x2 1920x1080 - four 960x540",
            "2x2 2560x1440 - four 1280x720",
            "2x2 2560x1408 - four 1280x704",
            "VSR 2x2 3200x1800 - four 1600x900",
            "VSR 2x2 3840x2160 - four 1920x1080",
            "3 chars 2560x1440 - main plus two side",
            "3 chars VSR 3200x1800 - main plus two side",
            "5 chars 2560x1440 - main plus four tiles",
            "5 chars VSR 3200x1800 - main plus four tiles",
            "5 chars 2560x1440 - main and right stack",
            "2560x1408 1280x720 overlap - bottom row Y=688",
        };

        // All 13 resolutions from the old matrix (including the 4 removed >4K ones)
        var allResolutions = new[]
        {
            (1760, 990), (1920, 1080), (1920, 1200), (2560, 1440),
            (2816, 1584), (3072, 1728), (3200, 1800), (3328, 1872),
            (3840, 2160), (4352, 2448), (5120, 2880), (5760, 3240), (7680, 4320),
        };

        foreach (var (w, h) in allResolutions)
        {
            set.Add($"2x2 {w}x{h}");
            set.Add($"3-Char {w}x{h}");
            set.Add($"5-Char Quads {w}x{h}");
            set.Add($"5-Char Stack {w}x{h}");
        }

        // Stacked profiles for the removed >4K resolutions
        foreach (var (w, h) in new[] { (4352, 2448), (5120, 2880), (5760, 3240), (7680, 4320) })
            set.Add($"Stacked {w}x{h}");

        return set;
    }
}
