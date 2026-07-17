using System.Windows.Media;

namespace EveDeck.Utilities;

// Shared shape builders for the on-screen overlays. Kept in one place so the active-frame highlight
// (ActiveFrameOverlay) and the incoming-damage hit glow (LabelSurfaceWindow.AlertGlowElement) draw
// identical corner brackets.
public static class OverlayGeometry
{
    // Four L-shaped brackets marking the corners of the rect at (x, y, w, h). Each corner is one
    // open figure of two line segments; the returned geometry is frozen for cheap reuse as a
    // Path.Data / stroke source. `arm` is how far each bracket extends along both edges.
    public static Geometry CornerBrackets(double x, double y, double w, double h, double arm)
    {
        arm = System.Math.Min(arm, System.Math.Min(w, h) / 2.0);
        double r = x + w, b = y + h;
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            // top-left
            g.BeginFigure(new System.Windows.Point(x, y + arm), false, false);
            g.LineTo(new System.Windows.Point(x, y), true, false);
            g.LineTo(new System.Windows.Point(x + arm, y), true, false);
            // top-right
            g.BeginFigure(new System.Windows.Point(r - arm, y), false, false);
            g.LineTo(new System.Windows.Point(r, y), true, false);
            g.LineTo(new System.Windows.Point(r, y + arm), true, false);
            // bottom-right
            g.BeginFigure(new System.Windows.Point(r, b - arm), false, false);
            g.LineTo(new System.Windows.Point(r, b), true, false);
            g.LineTo(new System.Windows.Point(r - arm, b), true, false);
            // bottom-left
            g.BeginFigure(new System.Windows.Point(x + arm, b), false, false);
            g.LineTo(new System.Windows.Point(x, b), true, false);
            g.LineTo(new System.Windows.Point(x, b - arm), true, false);
        }
        geo.Freeze();
        return geo;
    }
}
