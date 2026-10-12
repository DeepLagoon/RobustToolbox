using System;
using System.Collections.Generic;
using Robust.Shared.Maths;
using Xilium.CefGlue;

namespace Robust.Client.WebView.Cef;

/// <summary>Bounds upload work without turning distant small paints into one large upload.</summary>
internal sealed class DirtyRegions
{
    private const int MaxRegions = 16;
    internal readonly List<UIBox2i> Rectangles = new(MaxRegions);
    internal bool FullUpload { get; private set; }

    internal void Add(int width, int height, CefRectangle rect)
    {
        if (FullUpload)
            return;

        var region = new UIBox2i(Math.Max(0, rect.X), Math.Max(0, rect.Y),
            (int)Math.Min(width, (long)rect.X + rect.Width),
            (int)Math.Min(height, (long)rect.Y + rect.Height));
        if (region.Right <= region.Left || region.Bottom <= region.Top)
            return;

        // Merge only when this does not increase the number of pixels copied.
        // In particular, two corners of a large view stay separate.
        for (var i = 0; i < Rectangles.Count; i++)
        {
            var previous = Rectangles[i];
            var merged = new UIBox2i(Math.Min(previous.Left, region.Left), Math.Min(previous.Top, region.Top),
                Math.Max(previous.Right, region.Right), Math.Max(previous.Bottom, region.Bottom));
            if (Area(merged) > Area(previous) + Area(region))
                continue;

            region = merged;
            Rectangles.RemoveAt(i);
            i = -1;
        }

        Rectangles.Add(region);
        long area = 0;
        foreach (var dirty in Rectangles)
            area += Area(dirty);
        if (Rectangles.Count > MaxRegions || area >= (long)width * height / 2)
        {
            FullUpload = true;
            Rectangles.Clear();
        }
    }

    internal void Clear()
    {
        FullUpload = false;
        Rectangles.Clear();
    }

    private static long Area(UIBox2i region) => (long)region.Width * region.Height;
}
