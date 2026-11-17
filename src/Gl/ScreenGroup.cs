namespace WinGLSLScrSaver;


internal class ScreenGroup
{
    public List<Rectangle> Screens = new();
    public Rectangle Bbox;

    public static List<ScreenGroup> ComputeGroups(Rectangle[] screenRects)
    {
        var remaining = new List<Rectangle>(screenRects);
        var groups = new List<ScreenGroup>();

        while (remaining.Count > 0)
        {
            var g = new ScreenGroup { Screens = { remaining[0] }, Bbox = remaining[0] };
            remaining.RemoveAt(0);

            bool grew = true;
            while (grew)
            {
                grew = false;
                for (int i = remaining.Count - 1; i >= 0; i--)
                {
                    if (FlushAdjacent(remaining[i], g.Bbox))
                    {
                        g.Screens.Add(remaining[i]);
                        g.Bbox = Rectangle.Union(g.Bbox, remaining[i]);
                        remaining.RemoveAt(i);
                        grew = true;
                    }
                }
            }
            groups.Add(g);
        }
        return groups;
    }

    // Monitors that tile a rectangle nearly perfectly (1% tolerance absorbs
    // coordinate rounding from drivers/DPI/bezel compensation) are merged
    // into one group - the shared canvas then spans them seamlessly.
    private static bool FlushAdjacent(Rectangle a, Rectangle b)
    {
        double tolX = Math.Min(a.Width, b.Width) * 0.01;
        double tolY = Math.Min(a.Height, b.Height) * 0.01;

        bool sideBySide =
            (Math.Abs(a.Right - b.Left) <= tolX || Math.Abs(b.Right - a.Left) <= tolX) &&
            Math.Abs(a.Top - b.Top) <= tolY && Math.Abs(a.Bottom - b.Bottom) <= tolY;
        if (sideBySide) return true;

        bool stacked =
            (Math.Abs(a.Bottom - b.Top) <= tolY || Math.Abs(b.Bottom - a.Top) <= tolY) &&
            Math.Abs(a.Left - b.Left) <= tolX && Math.Abs(a.Right - b.Right) <= tolX;
        return stacked;
    }
}
