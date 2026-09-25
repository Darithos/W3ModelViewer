using Wc3ModelViewer.Core.Formats;

namespace MdxProbe;

/// <summary>
/// A synthetic sprite sheet whose every cell says which cell it is, for reading StarCraft II's
/// flipbook playback off a screenshot.
/// </summary>
internal static class FlipbookCalib
{
    /// <summary>
    /// <paramref name="cells"/> × <paramref name="cells"/> cells of <paramref name="cellPx"/> pixels.
    /// Cell k (row-major, top-left first): a fully saturated hue of k/N² over the cell, a grey
    /// square of sRGB value k/(N²−1) in the top-left quarter, k white dots on an 8×8 grid in the
    /// lower-right three quarters, and a 4-pixel transparent border so cell edges are visible.
    /// </summary>
    public static RgbaImage Atlas(int cells = 8, int cellPx = 128)
    {
        int size = cells * cellPx;
        var px = new byte[size * size * 4];
        int total = cells * cells;
        const int border = 4;
        for (int k = 0; k < total; k++)
        {
            int cx = (k % cells) * cellPx, cy = (k / cells) * cellPx;
            var (hr, hg, hb) = Hue(k / (float)total);
            byte grey = (byte)Math.Round(255.0 * k / (total - 1));
            for (int y = 0; y < cellPx; y++)
                for (int x = 0; x < cellPx; x++)
                {
                    bool inBorder = x < border || y < border || x >= cellPx - border || y >= cellPx - border;
                    byte r = hr, g = hg, b = hb, a = inBorder ? (byte)0 : (byte)255;
                    if (!inBorder && x < cellPx / 2 && y < cellPx / 2) { r = g = b = grey; }
                    else if (!inBorder && Dot(x, y, cellPx) is int d && d < k) { r = g = b = 255; }
                    int i = ((cy + y) * size + cx + x) * 4;
                    px[i] = b; px[i + 1] = g; px[i + 2] = r; px[i + 3] = a;
                }
        }
        return new RgbaImage { Width = size, Height = size, Pixels = px };

        // Which of the 64 dot slots this pixel falls in, or null. The dots fill an 8x8 grid over the
        // lower-right three quarters of the cell, each dot a filled square a third of its pitch.
        static int? Dot(int x, int y, int cellPx)
        {
            int ox = cellPx / 4, oy = cellPx / 4;              // grid origin
            if (x < ox || y < oy) return null;
            float pitch = (cellPx - ox) / 8f;
            int gx = (int)((x - ox) / pitch), gy = (int)((y - oy) / pitch);
            if (gx > 7 || gy > 7) return null;
            float fx = (x - ox) / pitch - gx, fy = (y - oy) / pitch - gy;
            return fx > 0.33f && fx < 0.67f && fy > 0.33f && fy < 0.67f ? gy * 8 + gx : null;
        }

        static (byte, byte, byte) Hue(float h)
        {
            float r = MathF.Abs(h * 6 - 3) - 1, g = 2 - MathF.Abs(h * 6 - 2), b = 2 - MathF.Abs(h * 6 - 4);
            return ((byte)(Math.Clamp(r, 0, 1) * 255), (byte)(Math.Clamp(g, 0, 1) * 255), (byte)(Math.Clamp(b, 0, 1) * 255));
        }
    }
}
