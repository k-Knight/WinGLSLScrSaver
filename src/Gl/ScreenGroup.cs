namespace WinGLSLScrSaver;

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Numerics;

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

    private static bool FlushAdjacent(Rectangle a, Rectangle b)
    {
        if (a.Right == b.Left || b.Right == a.Left)          // side by side
            return a.Top == b.Top && a.Bottom == b.Bottom;
        if (a.Bottom == b.Top || b.Bottom == a.Top)          // stacked
            return a.Left == b.Left && a.Right == b.Right;
        return false;
    }
}

internal struct VPosUv
{
    public Vector4 Data;
    public VPosUv(Vector2 pos, Vector2 uv) { Data = new Vector4(pos.X, pos.Y, uv.X, uv.Y); }
}
