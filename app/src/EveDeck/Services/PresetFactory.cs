using EveDeck.Models;

namespace EveDeck.Services;

public static class PresetFactory
{
    // (width, height, includeTaskbarVariant) — curated to the handful of resolutions people actually
    // run multibox setups on. Anything dropped here must be added to BuildDeprecatedNames so it's
    // cleaned out of existing users' saved profiles.
    private static readonly (int W, int H, bool Taskbar)[] Resolutions =
    {
        (1920, 1080,  true),
        (2560, 1440,  true),
        (3200, 1800,  false),
        (3840, 2160,  true),
    };

    // Resolutions the Center Master family template offers in its dropdown (must mirror Resolutions).
    private static readonly (int W, int H)[] CenterMasterResolutions =
    {
        (1920, 1080), (2560, 1440), (3200, 1800), (3840, 2160),
    };

    // Client counts the Grid family template offers in its dropdown.
    private static readonly int[] GridCounts = { 2, 3, 4, 5, 6, 7, 8, 9, 10, 12, 15 };

    // Client counts the Center Master family template offers in its dropdown.
    private static readonly int[] CenterMasterCounts = { 4, 5, 6, 7, 8, 9, 10, 12, 15 };

    // Client counts the Whammy Board family template offers in its dropdown.
    private static readonly int[] WhammyCounts = { 4, 5, 6, 7, 8, 9, 10, 12, 15 };

    // Client counts the Side Stack family template offers in its dropdown (master + 2..8 stacked tiles).
    private static readonly int[] SideStackCounts = { 3, 4, 5, 6, 7, 8, 9 };

    // Edges the Side Stack family can stack its tiles on.
    private static readonly string[] SideStackSides = { "Left", "Right", "Top", "Bottom" };

    // Client counts the Twin Stack family template offers in its dropdown. Restricted to counts whose
    // alt count (n-1) is even so both columns are always perfectly symmetric — no tie-break needed for
    // which side gets an extra tile.
    private static readonly int[] TwinStackCounts = { 5, 7, 9 };

    // Dropdown option lists for the UI (Grid/Center Master resolution + account-count pickers).
    public static IReadOnlyList<DisplayModeOption> GridResolutionOptions { get; } =
        Array.ConvertAll(Resolutions, r => new DisplayModeOption($"{r.W}×{r.H}", r.W, r.H));

    public static IReadOnlyList<DisplayModeOption> CenterMasterResolutionOptions { get; } =
        Array.ConvertAll(CenterMasterResolutions, r => new DisplayModeOption($"{r.W}×{r.H}", r.W, r.H));

    public static IReadOnlyList<DisplayModeOption> WhammyResolutionOptions { get; } =
        Array.ConvertAll(CenterMasterResolutions, r => new DisplayModeOption($"{r.W}×{r.H}", r.W, r.H));

    public static IReadOnlyList<DisplayModeOption> SideStackResolutionOptions { get; } =
        Array.ConvertAll(CenterMasterResolutions, r => new DisplayModeOption($"{r.W}×{r.H}", r.W, r.H));

    public static IReadOnlyList<DisplayModeOption> TwinStackResolutionOptions { get; } =
        Array.ConvertAll(CenterMasterResolutions, r => new DisplayModeOption($"{r.W}×{r.H}", r.W, r.H));

    public static IReadOnlyList<int> GridCountOptions => GridCounts;
    public static IReadOnlyList<int> CenterMasterCountOptions => CenterMasterCounts;
    public static IReadOnlyList<int> WhammyCountOptions => WhammyCounts;
    public static IReadOnlyList<int> SideStackCountOptions => SideStackCounts;
    public static IReadOnlyList<string> SideStackSideOptions => SideStackSides;
    public static IReadOnlyList<int> TwinStackCountOptions => TwinStackCounts;

    // Declared last: BuildDeprecatedNames() reads Resolutions/CenterMasterResolutions above, and static
    // field initializers run in declaration order, so this must come after them or those fields are
    // still null when it runs (crashed on startup before this fix).
    private static readonly HashSet<string> DeprecatedBuiltInNames = BuildDeprecatedNames();

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

        list.Add(CreateGridTemplate());
        list.Add(Overlap());

        // Center Master family: a square ring of corner/edge tiles + one larger master floating centered
        // on top. n=5 is the classic 2×2 corners + master; higher counts add perimeter tiles and grow the
        // master to fill the hollow center (e.g. 9 = eight tiles in a 3×3 ring with the 9th centered).
        list.Add(CreateCenterMasterTemplate());

        // Whammy Board family: like Center Master but with NO side columns - alt tiles split into a top
        // row and a bottom row (Press Your Luck style) with the master floating centered between them.
        list.Add(CreateWhammyTemplate());

        // Side Stack family: a vertical stack of small tiles down one edge (Left/Right dropdown) with
        // the master filling the rest of the monitor.
        list.Add(CreateSideStackTemplate());

        // Twin Stack family: like Side Stack but bookended - alt tiles split evenly into a stacked
        // column on BOTH edges, with the master filling the gap between them.
        list.Add(CreateTwinStackTemplate());

        return list;
    }

    // A single "Grid" profile whose resolution and account count are picked via dropdowns in the UI
    // (rather than one named preset per resolution×count combo) and whose slots regenerate on demand.
    private static LayoutProfile CreateGridTemplate()
    {
        var profile = new LayoutProfile
        {
            Name = "Grid",
            IsBuiltIn = true,
            Category = "Grid",
            IsFamilyTemplate = true,
            TemplateWidth = 2560,
            TemplateHeight = 1440,
            TemplateCount = 4,
        };
        PopulateGridSlots(profile);
        return profile;
    }

    private static LayoutProfile CreateCenterMasterTemplate()
    {
        var profile = new LayoutProfile
        {
            Name = "Center Master",
            IsBuiltIn = true,
            Category = "Center Master",
            IsFamilyTemplate = true,
            TemplateWidth = 2560,
            TemplateHeight = 1440,
            TemplateCount = 5,
        };
        PopulateCenterMasterSlots(profile);
        return profile;
    }

    private static LayoutProfile CreateWhammyTemplate()
    {
        var profile = new LayoutProfile
        {
            Name = "Whammy Board",
            IsBuiltIn = true,
            Category = "Whammy Board",
            IsFamilyTemplate = true,
            TemplateWidth = 2560,
            TemplateHeight = 1440,
            TemplateCount = 5,
        };
        PopulateWhammySlots(profile);
        return profile;
    }

    private static LayoutProfile CreateSideStackTemplate()
    {
        var profile = new LayoutProfile
        {
            Name = "Side Stack",
            IsBuiltIn = true,
            Category = "Side Stack",
            IsFamilyTemplate = true,
            TemplateWidth = 2560,
            TemplateHeight = 1440,
            TemplateCount = 5,
            TemplateSide = "Left",
        };
        PopulateSideStackSlots(profile);
        return profile;
    }

    private static LayoutProfile CreateTwinStackTemplate()
    {
        var profile = new LayoutProfile
        {
            Name = "Twin Stack",
            IsBuiltIn = true,
            Category = "Twin Stack",
            IsFamilyTemplate = true,
            TemplateWidth = 2560,
            TemplateHeight = 1440,
            TemplateCount = 5,
        };
        PopulateTwinStackSlots(profile);
        return profile;
    }

    // A starter custom profile: `count` slots arranged as a simple grid at the given resolution
    // (0,0-based, scaled to the target monitor at apply time). Used by the New Profile flow before
    // the on-monitor editor opens so the user has sensibly-placed slots to drag around.
    public static LayoutProfile CreateCustomProfile(string name, int width, int height, int count)
    {
        count = Math.Clamp(count, 1, 15);
        var profile = new LayoutProfile { Name = name, Category = "Custom" };
        if (count == 1)
        {
            profile.Slots.Add(Slot(1, 0, 0, width, height));
            return profile;
        }
        var temp = new LayoutProfile { TemplateWidth = width, TemplateHeight = height, TemplateCount = count };
        PopulateGridSlots(temp);
        foreach (var slot in temp.Slots)
            profile.Slots.Add(slot);
        return profile;
    }

    // Clamp a family template's TemplateWidth/Height/Count to the curated dropdown options (in case a
    // saved profile predates a change to those options), then rebuild its Slots from scratch.
    public static void RegenerateFamilySlots(LayoutProfile profile)
    {
        if (!profile.IsFamilyTemplate) return;

        if (profile.Category == "Grid")
        {
            var (w, h) = NearestResolution(Array.ConvertAll(Resolutions, r => (r.W, r.H)), profile.TemplateWidth);
            profile.TemplateWidth = w;
            profile.TemplateHeight = h;
            profile.TemplateCount = NearestCount(GridCounts, profile.TemplateCount);
            PopulateGridSlots(profile);
        }
        else if (profile.Category == "Center Master")
        {
            var (w, h) = NearestResolution(CenterMasterResolutions, profile.TemplateWidth);
            profile.TemplateWidth = w;
            profile.TemplateHeight = h;
            profile.TemplateCount = NearestCount(CenterMasterCounts, profile.TemplateCount);
            PopulateCenterMasterSlots(profile);
        }
        else if (profile.Category == "Whammy Board")
        {
            var (w, h) = NearestResolution(CenterMasterResolutions, profile.TemplateWidth);
            profile.TemplateWidth = w;
            profile.TemplateHeight = h;
            profile.TemplateCount = NearestCount(WhammyCounts, profile.TemplateCount);
            PopulateWhammySlots(profile);
        }
        else if (profile.Category == "Side Stack")
        {
            var (w, h) = NearestResolution(CenterMasterResolutions, profile.TemplateWidth);
            profile.TemplateWidth = w;
            profile.TemplateHeight = h;
            profile.TemplateCount = NearestCount(SideStackCounts, profile.TemplateCount);
            if (Array.IndexOf(SideStackSides, profile.TemplateSide) < 0) profile.TemplateSide = "Left";
            PopulateSideStackSlots(profile);
        }
        else if (profile.Category == "Twin Stack")
        {
            var (w, h) = NearestResolution(CenterMasterResolutions, profile.TemplateWidth);
            profile.TemplateWidth = w;
            profile.TemplateHeight = h;
            profile.TemplateCount = NearestCount(TwinStackCounts, profile.TemplateCount);
            PopulateTwinStackSlots(profile);
        }
    }

    private static void PopulateGridSlots(LayoutProfile profile)
    {
        var width = profile.TemplateWidth;
        var height = profile.TemplateHeight;
        var count = profile.TemplateCount;
        var (cols, rows) = GridDimensions(count);

        profile.Slots.Clear();
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
    }

    // A square ring of tiles surrounding one larger master floating centered on top. Generalises the
    // original 5-Char design (2×2 corners + a 1.5-cell master) to any count: the ring is split into four
    // sides (top/bottom/left/right) whose tile counts sum to n-1, and each side's tiles evenly partition
    // that side's *entire* pixel length — so every perimeter side is fully covered with no dead cells,
    // unlike the old design which dropped whole grid cells (visible gaps) whenever the count didn't divide
    // the ring evenly. At the exact-fit counts (5 -> 2x2, 9 -> 3x3, ...) this produces byte-identical
    // slots to before, since every side gets its maximum tile count. The master is slot n and is always
    // the largest rect, so CenterSlotNumber auto-detects it. On a 16:9 monitor every rect stays 16:9.
    private static void PopulateCenterMasterSlots(LayoutProfile profile)
    {
        var n = profile.TemplateCount;
        var sw = profile.TemplateWidth;
        var sh = profile.TemplateHeight;

        var tiles = n - 1;
        var s = 2;
        while (4 * s - 4 < tiles) s++;          // smallest ring whose perimeter cell count holds every tile

        var cellW = sw / s;
        var cellH = sh / s;

        // Max tiles each side could hold if the ring were fully populated (corners belong to top/bottom).
        var topMax = s;
        var bottomMax = s;
        var sideMax = s - 2;                    // left/right middle cells; 0 when s == 2 (no side column)

        var topN = topMax;
        var bottomN = bottomMax;
        var leftN = sideMax;
        var rightN = sideMax;

        // Shrink side tile-counts (never dropping a side to 0 tiles if it started non-zero) until they sum
        // to `tiles`. Always shrinking the current largest side keeps counts balanced and, per construction,
        // never needs to push an existing side below 1 (proven by the s-selection loop above).
        var deficit = (topMax + bottomMax + sideMax + sideMax) - tiles;
        while (deficit > 0)
        {
            var pick = 0; // 0=top,1=bottom,2=left,3=right
            var best = -1;
            if (topN > 1 && topN > best) { best = topN; pick = 0; }
            if (bottomN > 1 && bottomN > best) { best = bottomN; pick = 1; }
            if (leftN > 1 && leftN > best) { best = leftN; pick = 2; }
            if (rightN > 1 && rightN > best) { best = rightN; pick = 3; }
            switch (pick)
            {
                case 0: topN--; break;
                case 1: bottomN--; break;
                case 2: leftN--; break;
                case 3: rightN--; break;
            }
            deficit--;
        }

        profile.Slots.Clear();
        var slotNum = 1;

        var bottomY = (s - 1) * cellH;
        var bottomH = sh - bottomY;              // absorb rounding remainder in the last row
        var rightX = (s - 1) * cellW;
        var rightW = sw - rightX;                // absorb rounding remainder in the last column
        var stripY = cellH;
        var stripH = (s - 2) * cellH;             // vertical extent of the left/right middle strip

        foreach (var (offset, size) in EvenSplit(sw, topN))
        {
            var label = topN == 1 ? "Top" : offset == 0 ? "Top Left" : offset + size >= sw ? "Top Right" : "Top";
            profile.Slots.Add(Slot(slotNum++, offset, 0, size, cellH, label));
        }
        var leftIdx = 0;
        foreach (var (offset, size) in EvenSplit(stripH, leftN))
            profile.Slots.Add(Slot(slotNum++, 0, stripY + offset, cellW, size, leftN == 1 ? "Left" : $"Left {++leftIdx}"));
        var rightIdx = 0;
        foreach (var (offset, size) in EvenSplit(stripH, rightN))
            profile.Slots.Add(Slot(slotNum++, rightX, stripY + offset, rightW, size, rightN == 1 ? "Right" : $"Right {++rightIdx}"));
        foreach (var (offset, size) in EvenSplit(sw, bottomN))
        {
            var label = bottomN == 1 ? "Bottom" : offset == 0 ? "Bottom Left" : offset + size >= sw ? "Bottom Right" : "Bottom";
            profile.Slots.Add(Slot(slotNum++, offset, bottomY, size, bottomH, label));
        }

        // Master spans the hollow center plus overlap into the surrounding ring tiles. The 2x2 ring
        // (s==2) has no interior cell at all, so this is the only thing standing between "no master"
        // and "master covers everything". 2026-07-08: sized down from 1.5 cells (75% of each dimension,
        // matching the legacy 5-Char master exactly) to 1.2 cells (60%) so it covers less of each corner
        // tile -- but only when the ring is still symmetric (both top and bottom hold their full 2
        // tiles, i.e. the 5-client case). When one side has shrunk to a single full-width/height tile
        // (the 4-client case), that lone tile's area is already half the whole screen, so a 60% master
        // would end up SMALLER than it; keep the original 75% there instead. Larger rings (s>2) already
        // have a real hollow and keep their original (s-1)-cell sizing for the same reason.
        var symmetric = topN == topMax && bottomN == bottomMax;
        var masterCellsX10 = s != 2 ? 10 * (s - 1) : symmetric ? 12 : 15;
        var masterW = masterCellsX10 * cellW / 10;
        var masterH = masterCellsX10 * cellH / 10;
        profile.Slots.Add(Slot(n, (sw - masterW) / 2, (sh - masterH) / 2, masterW, masterH, "Master"));
    }

    // Press Your Luck board: a full-width row of tiles across the top and bottom (extra tile goes top
    // when odd) with the master filling the ENTIRE band between them, edge to edge - like the show's
    // big center screen filling the hole in the tile ring. Rows always span the whole width via
    // EvenSplit. rowH = sh/(rowN+2) makes the fuller row's tiles EXACTLY the master's aspect ratio
    // (tile (sw/rowN) x rowH vs master sw x (rowN*rowH)), so live previews fill their tiles with no
    // black bars (odd counts leave the sparser row slightly wider than the master aspect). Master is
    // slot n and always the largest rect so CenterSlotNumber auto-detects it.
    private static void PopulateWhammySlots(LayoutProfile profile)
    {
        var n = profile.TemplateCount;
        var sw = profile.TemplateWidth;
        var sh = profile.TemplateHeight;

        var tiles = n - 1;
        var topN = (tiles + 1) / 2;
        var bottomN = tiles / 2;
        var rowH = sh / (Math.Max(topN, bottomN) + 2);

        profile.Slots.Clear();
        var slotNum = 1;

        foreach (var (offset, size) in EvenSplit(sw, topN))
        {
            var label = topN == 1 ? "Top" : offset == 0 ? "Top Left" : offset + size >= sw ? "Top Right" : "Top";
            profile.Slots.Add(Slot(slotNum++, offset, 0, size, rowH, label));
        }
        foreach (var (offset, size) in EvenSplit(sw, bottomN))
        {
            var label = bottomN == 1 ? "Bottom" : offset == 0 ? "Bottom Left" : offset + size >= sw ? "Bottom Right" : "Bottom";
            profile.Slots.Add(Slot(slotNum++, offset, sh - rowH, size, rowH, label));
        }

        profile.Slots.Add(Slot(n, 0, rowH, sw, sh - 2 * rowH, "Master"));
    }

    // A stack of k = n-1 tiles down one edge (TemplateSide: Left/Right stack a column, Top/Bottom stack
    // a row) with the master filling ALL the remaining space, edge to edge. Left/Right: tileW = sw/(k+1)
    // makes every tile EXACTLY the master's aspect ratio (tile (sw/(k+1)) x (sh/k) vs master
    // (sw*k/(k+1)) x sh). Top/Bottom is the same math transposed: tileH = sh/(k+1), tile (sw/k) x
    // (sh/(k+1)) vs master sw x (sh*k/(k+1)). Clients run at the master rect's size, so aspect-matched
    // tiles mean the live previews fill their tiles with no black bars. The stack always spans the full
    // edge via EvenSplit. Master is slot n and always the largest rect.
    private static void PopulateSideStackSlots(LayoutProfile profile)
    {
        var n = profile.TemplateCount;
        var sw = profile.TemplateWidth;
        var sh = profile.TemplateHeight;
        var side = profile.TemplateSide;
        var vertical = side is "Left" or "Right";

        var tiles = n - 1;
        profile.Slots.Clear();
        var slotNum = 1;

        if (vertical)
        {
            var right = side == "Right";
            var tileW = sw / (tiles + 1);
            var tileX = right ? sw - tileW : 0;
            var sideName = right ? "Right" : "Left";

            foreach (var (offset, size) in EvenSplit(sh, tiles))
            {
                profile.Slots.Add(Slot(slotNum, tileX, offset, tileW, size, $"{sideName} {slotNum}"));
                slotNum++;
            }

            // Master fills ALL remaining space - full height, edge to edge against the tile column.
            profile.Slots.Add(Slot(n, right ? 0 : tileW, 0, sw - tileW, sh, "Master"));
        }
        else
        {
            var bottom = side == "Bottom";
            var tileH = sh / (tiles + 1);
            var tileY = bottom ? sh - tileH : 0;
            var sideName = bottom ? "Bottom" : "Top";

            foreach (var (offset, size) in EvenSplit(sw, tiles))
            {
                profile.Slots.Add(Slot(slotNum, offset, tileY, size, tileH, $"{sideName} {slotNum}"));
                slotNum++;
            }

            // Master fills ALL remaining space - full width, edge to edge against the tile row.
            profile.Slots.Add(Slot(n, 0, bottom ? 0 : tileH, sw, sh - tileH, "Master"));
        }
    }

    // Twin Stack: k = n-1 tiles split evenly into TWO stacked columns (one down the left
    // edge, one down the right edge), with the master filling the gap between them. Only offered for
    // counts where k is even (TwinStackCounts) so both columns always hold exactly k/2 tiles - no
    // tie-break for an odd leftover tile needed. tileW = sw/(perSide+2) makes every tile EXACTLY the
    // same aspect ratio as the master (tile tileW x (sh/perSide) vs master (sw-2*tileW) x sh), same
    // reasoning as PopulateSideStackSlots: clients run at the master rect's size, so aspect-matched
    // tiles mean the live previews fill their tiles with no black bars. Master is slot n and always
    // the largest rect.
    private static void PopulateTwinStackSlots(LayoutProfile profile)
    {
        var n = profile.TemplateCount;
        var sw = profile.TemplateWidth;
        var sh = profile.TemplateHeight;

        var tiles = n - 1;
        var perSide = tiles / 2;
        var tileW = sw / (perSide + 2);

        profile.Slots.Clear();
        var slotNum = 1;

        foreach (var (offset, size) in EvenSplit(sh, perSide))
        {
            profile.Slots.Add(Slot(slotNum, 0, offset, tileW, size, $"Left {slotNum}"));
            slotNum++;
        }
        foreach (var (offset, size) in EvenSplit(sh, perSide))
        {
            profile.Slots.Add(Slot(slotNum, sw - tileW, offset, tileW, size, $"Right {slotNum - perSide}"));
            slotNum++;
        }

        // Master fills the gap between the two columns - full height, edge to edge against both.
        profile.Slots.Add(Slot(n, tileW, 0, sw - 2 * tileW, sh, "Master"));
    }

    // Evenly partitions `total` pixels into `count` contiguous, non-overlapping spans (last absorbs the
    // rounding remainder) so a side of the ring is always fully covered with no dead space between tiles.
    internal static List<(int offset, int size)> EvenSplit(int total, int count)
    {
        var result = new List<(int offset, int size)>();
        if (count <= 0) return result;
        var baseSize = total / count;
        var offset = 0;
        for (var i = 0; i < count; i++)
        {
            var size = (i == count - 1) ? total - offset : baseSize;
            result.Add((offset, size));
            offset += size;
        }
        return result;
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
                if (builtIn.IsFamilyTemplate)
                {
                    // Preserve the user's chosen resolution/count across app updates instead of stomping
                    // it back to the default template — only regenerate slots from whatever they last set.
                    existing.IsBuiltIn = true;
                    existing.IsFamilyTemplate = true;
                    if (existing.Category != builtIn.Category) { existing.Category = builtIn.Category; changed = true; }
                    if (existing.TemplateWidth == 0 || existing.TemplateHeight == 0 || existing.TemplateCount == 0)
                    {
                        existing.TemplateWidth = builtIn.TemplateWidth;
                        existing.TemplateHeight = builtIn.TemplateHeight;
                        existing.TemplateCount = builtIn.TemplateCount;
                    }

                    var (beforeW, beforeH, beforeC, beforeS) = (existing.TemplateWidth, existing.TemplateHeight, existing.TemplateCount, existing.TemplateSide);
                    // RegenerateFamilySlots rebuilds Slots from scratch (fresh LayoutSlot objects), which
                    // wiped every minimap-set HomeSeat (corner assignment) on every single app launch --
                    // this runs unconditionally here, not just when the user actually changes the
                    // resolution/count dropdown. Slot numbering for a given category+count is stable
                    // (PopulateGridSlots/PopulateCenterMasterSlots/etc. always number position N the same
                    // way), so snapshot HomeSeat by position and restore it onto the regenerated slots.
                    var homeSeatByPosition = existing.Slots.Where(s => s.HomeSeat.HasValue)
                        .ToDictionary(s => s.SlotNumber, s => s.HomeSeat);
                    RegenerateFamilySlots(existing);
                    foreach (var slot in existing.Slots)
                        if (homeSeatByPosition.TryGetValue(slot.SlotNumber, out var homeSeat))
                            slot.HomeSeat = homeSeat;
                    if (existing.TemplateWidth != beforeW || existing.TemplateHeight != beforeH || existing.TemplateCount != beforeC || existing.TemplateSide != beforeS)
                        changed = true;
                    continue;
                }

                changed |= RepairBuiltInProfile(existing, builtIn);
                continue;
            }

            profiles.Add(builtIn);
            changed = true;
        }

        return changed;
    }

    // Pick the built-in family + resolution + account count that best fits a given client count on a
    // monitor of the given size. Built-in profiles scale to the target monitor at apply time, so an exact
    // resolution match isn't required — we choose the nearest-by-width option to minimise aspect distortion.
    // Returns null for 0 or 1 clients (1-Char is a fixed per-resolution profile, not a family template).
    public static (string Category, int Width, int Height, int Count)? ResolveFamilySelection(int clientCount, int monitorWidth, int monitorHeight)
    {
        if (clientCount <= 1) return null;

        // 4–15 : the Center Master family (big centered master + ring of tiles, fast hotkey swapping).
        if (clientCount >= 4 && clientCount <= 15)
        {
            var (w, h) = NearestResolution(CenterMasterResolutions, monitorWidth);
            return ("Center Master", w, h, NearestCount(CenterMasterCounts, clientCount));
        }

        // 2–3 and >15 : the Grid family (clamped to the 2..15 counts that exist).
        var clamped = Math.Clamp(clientCount, 2, 15);
        var (gw, gh) = NearestResolution(Array.ConvertAll(Resolutions, r => (r.W, r.H)), monitorWidth);
        return ("Grid", gw, gh, NearestCount(GridCounts, clamped));
    }

    // Human-readable description of the best fit, for status/summary text (not a profile lookup key).
    public static string? BestProfileName(int clientCount, int monitorWidth, int monitorHeight)
    {
        if (clientCount <= 0) return null;

        if (clientCount == 1)
        {
            var (w, h) = NearestResolution(Array.ConvertAll(Resolutions, r => (r.W, r.H)), monitorWidth);
            return $"1-Char {w}x{h}";
        }

        var sel = ResolveFamilySelection(clientCount, monitorWidth, monitorHeight)!.Value;
        return $"{sel.Category} ({sel.Count} accounts, {sel.Width}x{sel.Height})";
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

    // Nearest value in a curated count set; ties favor the larger count so nobody loses a seat.
    private static int NearestCount(int[] counts, int target)
    {
        var best = counts[0];
        var bestDelta = int.MaxValue;
        foreach (var c in counts)
        {
            var delta = Math.Abs(c - target);
            if (delta < bestDelta || (delta == bestDelta && c > best)) { bestDelta = delta; best = c; }
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

        // 2026-07-01: curated Resolutions/GridCounts/CenterMasterCounts down to a smaller matrix —
        // drop the resolutions and grid counts no longer generated.
        var droppedResolutions = new[] { (1760, 990), (1920, 1200), (2816, 1584), (3072, 1728), (3328, 1872) };
        foreach (var (w, h) in droppedResolutions)
        {
            set.Add($"Stacked {w}x{h}");
            set.Add($"1-Char {w}x{h}");
            for (var n = 2; n <= 15; n++)
                set.Add($"{n}-Char Grid {w}x{h}");
        }

        // 2026-07-02: Grid and Center Master collapsed from one named profile per resolution×count combo
        // into a single "Grid" / "Center Master" family template with resolution+count dropdowns — every
        // previously-generated combo name is now dead and must be pruned from existing users' settings.
        foreach (var (w, h) in allResolutions)
        {
            for (var n = 2; n <= 15; n++)
                set.Add($"{n}-Char Grid {w}x{h}");
            for (var n = 4; n <= 15; n++)
                set.Add($"{n}-Char Center Master {w}x{h}");
        }

        return set;
    }
}
