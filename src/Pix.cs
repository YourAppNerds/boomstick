using System;
using Godot;

namespace Boomstick;

/// <summary>
/// Small software canvas for drawing chunky pixel art in C#: filled shapes, lines, outlines and
/// a 5x7 bitmap font. The result becomes an Image/ImageTexture with nearest filtering. Several
/// frames can be drawn side by side into one canvas to build a sprite atlas.
/// </summary>
public sealed class Pix
{
    public readonly int W, H;
    private readonly Color[] _px;

    /// <summary>Horizontal offset added to every draw call, used to draw atlas frames.</summary>
    public int OX;
    public int OY;

    public Pix(int w, int h)
    {
        W = w;
        H = h;
        _px = new Color[w * h];
    }

    public Color Get(int x, int y) => x < 0 || y < 0 || x >= W || y >= H ? default : _px[y * W + x];

    public void Set(int x, int y, Color c)
    {
        x += OX;
        y += OY;
        if (x < 0 || y < 0 || x >= W || y >= H) return;
        _px[y * W + x] = c;
    }

    private void SetRaw(int x, int y, Color c)
    {
        if (x < 0 || y < 0 || x >= W || y >= H) return;
        _px[y * W + x] = c;
    }

    public void Rect(int x, int y, int w, int h, Color c)
    {
        for (int j = y; j < y + h; j++)
        for (int i = x; i < x + w; i++)
            Set(i, j, c);
    }

    /// <summary>Filled ellipse with a simple top-left light: the edge away from the light is darker.</summary>
    public void Ellipse(float cx, float cy, float rx, float ry, Color c, float shade = 0.35f)
    {
        int x0 = (int)MathF.Floor(cx - rx), x1 = (int)MathF.Ceiling(cx + rx);
        int y0 = (int)MathF.Floor(cy - ry), y1 = (int)MathF.Ceiling(cy + ry);
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
        {
            float dx = (x + 0.5f - cx) / rx, dy = (y + 0.5f - cy) / ry;
            float d = dx * dx + dy * dy;
            if (d > 1f) continue;
            // Light comes from the upper left.
            float k = 1f + shade * (-(dx + dy) * 0.5f) - shade * 0.5f * d;
            Set(x, y, Mul(c, k));
        }
    }

    /// <summary>Thick line between two points (round brush).</summary>
    public void Line(float x0, float y0, float x1, float y1, float r, Color c)
    {
        float len = MathF.Max(MathF.Abs(x1 - x0), MathF.Abs(y1 - y0));
        int steps = Math.Max(1, (int)(len * 2));
        for (int s = 0; s <= steps; s++)
        {
            float t = s / (float)steps;
            float x = x0 + (x1 - x0) * t, y = y0 + (y1 - y0) * t;
            if (r <= 0.5f)
                Set((int)MathF.Floor(x), (int)MathF.Floor(y), c);
            else
                Ellipse(x, y, r, r, c, 0.15f);
        }
    }

    /// <summary>Filled triangle.</summary>
    public void Tri(float ax, float ay, float bx, float by, float cx, float cy, Color c)
    {
        int x0 = (int)MathF.Floor(MathF.Min(ax, MathF.Min(bx, cx))), x1 = (int)MathF.Ceiling(MathF.Max(ax, MathF.Max(bx, cx)));
        int y0 = (int)MathF.Floor(MathF.Min(ay, MathF.Min(by, cy))), y1 = (int)MathF.Ceiling(MathF.Max(ay, MathF.Max(by, cy)));
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
        {
            float px = x + 0.5f, py = y + 0.5f;
            float d1 = (px - bx) * (ay - by) - (ax - bx) * (py - by);
            float d2 = (px - cx) * (by - cy) - (bx - cx) * (py - cy);
            float d3 = (px - ax) * (cy - ay) - (cx - ax) * (py - ay);
            bool neg = d1 < 0 || d2 < 0 || d3 < 0, pos = d1 > 0 || d2 > 0 || d3 > 0;
            if (!(neg && pos)) Set(x, y, c);
        }
    }

    /// <summary>Sprinkles random darker/lighter pixels over opaque pixels in a region (texture grain).</summary>
    public void Grain(int x, int y, int w, int h, float amount, int seed)
    {
        for (int j = y; j < y + h; j++)
        for (int i = x; i < x + w; i++)
        {
            int ax = i + OX, ay = j + OY;
            var c = Get(ax, ay);
            if (c.A < 0.5f) continue;
            float n = (ProcTex.Rand(ax, ay, seed) - 0.5f) * amount;
            SetRaw(ax, ay, Mul(c, 1f + n));
        }
    }

    /// <summary>Adds a one-pixel outline around opaque pixels inside a region (the current frame).</summary>
    public void Outline(int x, int y, int w, int h, Color c)
    {
        var mark = new bool[w * h];
        for (int j = 0; j < h; j++)
        for (int i = 0; i < w; i++)
        {
            int ax = x + i + OX, ay = y + j + OY;
            if (Get(ax, ay).A >= 0.5f) continue;
            bool n = (i > 0 && Get(ax - 1, ay).A >= 0.5f) || (i < w - 1 && Get(ax + 1, ay).A >= 0.5f) ||
                     (j > 0 && Get(ax, ay - 1).A >= 0.5f) || (j < h - 1 && Get(ax, ay + 1).A >= 0.5f);
            mark[j * w + i] = n;
        }
        for (int j = 0; j < h; j++)
        for (int i = 0; i < w; i++)
            if (mark[j * w + i]) SetRaw(x + i + OX, y + j + OY, c);
    }

    /// <summary>Multiplies the colour of every opaque pixel in a region (used for pain/flash tints).</summary>
    public void Tint(int x, int y, int w, int h, Color mul, Color add)
    {
        for (int j = y; j < y + h; j++)
        for (int i = x; i < x + w; i++)
        {
            int ax = i + OX, ay = j + OY;
            var c = Get(ax, ay);
            if (c.A < 0.5f) continue;
            SetRaw(ax, ay, new Color(c.R * mul.R + add.R, c.G * mul.G + add.G, c.B * mul.B + add.B));
        }
    }

    public static Color Mul(Color c, float k) => new(c.R * k, c.G * k, c.B * k, c.A);

    public Image ToImage()
    {
        var data = new byte[W * H * 4];
        int i = 0;
        foreach (var c in _px)
        {
            data[i++] = (byte)(Mathf.Clamp(c.R, 0f, 1f) * 255f);
            data[i++] = (byte)(Mathf.Clamp(c.G, 0f, 1f) * 255f);
            data[i++] = (byte)(Mathf.Clamp(c.B, 0f, 1f) * 255f);
            data[i++] = (byte)(c.A >= 0.5f ? 255 : 0);
        }
        return Image.CreateFromData(W, H, false, Image.Format.Rgba8, data);
    }

    public ImageTexture ToTexture() => ImageTexture.CreateFromImage(ToImage());

    // ---- 5x7 bitmap font -------------------------------------------------------------------

    private const string Glyphs = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789%/:.!-+'?";

    // Each glyph is 7 rows of 5 pixels, top row first, rows separated by '|' ('#' = set).
    private static readonly string[] GlyphRows =
    {
        ".###.|#...#|#...#|#####|#...#|#...#|#...#", // A
        "####.|#...#|#...#|####.|#...#|#...#|####.", // B
        ".###.|#...#|#....|#....|#....|#...#|.###.", // C
        "####.|#...#|#...#|#...#|#...#|#...#|####.", // D
        "#####|#....|#....|####.|#....|#....|#####", // E
        "#####|#....|#....|####.|#....|#....|#....", // F
        ".###.|#...#|#....|#.###|#...#|#...#|.####", // G
        "#...#|#...#|#...#|#####|#...#|#...#|#...#", // H
        ".###.|..#..|..#..|..#..|..#..|..#..|.###.", // I
        "..###|...#.|...#.|...#.|...#.|#..#.|.##..", // J
        "#...#|#..#.|#.#..|##...|#.#..|#..#.|#...#", // K
        "#....|#....|#....|#....|#....|#....|#####", // L
        "#...#|##.##|#.#.#|#.#.#|#...#|#...#|#...#", // M
        "#...#|##..#|#.#.#|#..##|#...#|#...#|#...#", // N
        ".###.|#...#|#...#|#...#|#...#|#...#|.###.", // O
        "####.|#...#|#...#|####.|#....|#....|#....", // P
        ".###.|#...#|#...#|#...#|#.#.#|#..#.|.##.#", // Q
        "####.|#...#|#...#|####.|#.#..|#..#.|#...#", // R
        ".####|#....|#....|.###.|....#|....#|####.", // S
        "#####|..#..|..#..|..#..|..#..|..#..|..#..", // T
        "#...#|#...#|#...#|#...#|#...#|#...#|.###.", // U
        "#...#|#...#|#...#|#...#|#...#|.#.#.|..#..", // V
        "#...#|#...#|#...#|#.#.#|#.#.#|#.#.#|.#.#.", // W
        "#...#|#...#|.#.#.|..#..|.#.#.|#...#|#...#", // X
        "#...#|#...#|.#.#.|..#..|..#..|..#..|..#..", // Y
        "#####|....#|...#.|..#..|.#...|#....|#####", // Z
        ".###.|#...#|#..##|#.#.#|##..#|#...#|.###.", // 0
        "..#..|.##..|..#..|..#..|..#..|..#..|.###.", // 1
        ".###.|#...#|....#|..##.|.#...|#....|#####", // 2
        ".###.|#...#|....#|..##.|....#|#...#|.###.", // 3
        "...#.|..##.|.#.#.|#..#.|#####|...#.|...#.", // 4
        "#####|#....|####.|....#|....#|#...#|.###.", // 5
        "..##.|.#...|#....|####.|#...#|#...#|.###.", // 6
        "#####|....#|...#.|..#..|.#...|.#...|.#...", // 7
        ".###.|#...#|#...#|.###.|#...#|#...#|.###.", // 8
        ".###.|#...#|#...#|.####|....#|...#.|.##..", // 9
        "##...|##..#|...#.|..#..|.#...|#..##|...##", // %
        "....#|....#|...#.|..#..|.#...|#....|#....", // /
        ".....|.##..|.##..|.....|.##..|.##..|.....", // :
        ".....|.....|.....|.....|.....|.##..|.##..", // .
        "..#..|..#..|..#..|..#..|..#..|.....|..#..", // !
        ".....|.....|.....|.###.|.....|.....|.....", // -
        ".....|..#..|..#..|#####|..#..|..#..|.....", // +
        "..#..|..#..|.....|.....|.....|.....|.....", // '
        ".###.|#...#|....#|...#.|..#..|.....|..#..", // ?
    };

    private static bool GlyphBit(char ch, int x, int y)
    {
        int gi = Glyphs.IndexOf(char.ToUpperInvariant(ch));
        if (gi < 0) return false;
        return GlyphRows[gi][y * 6 + x] == '#';
    }

    public static int TextWidth(string s) => s.Length * 6 - 1;

    /// <summary>Draws text with a vertical gradient (top to bottom colour) and a dark drop shadow.</summary>
    public void Text(int x, int y, string s, Color top, Color bottom, bool shadow = true)
    {
        for (int k = 0; k < s.Length; k++)
        {
            char ch = s[k];
            for (int gy = 0; gy < 7; gy++)
            for (int gx = 0; gx < 5; gx++)
            {
                if (!GlyphBit(ch, gx, gy)) continue;
                int px = x + k * 6 + gx, py = y + gy;
                if (shadow) Set(px + 1, py + 1, new Color(0.05f, 0.02f, 0.02f));
            }
        }
        for (int k = 0; k < s.Length; k++)
        {
            char ch = s[k];
            for (int gy = 0; gy < 7; gy++)
            for (int gx = 0; gx < 5; gx++)
            {
                if (!GlyphBit(ch, gx, gy)) continue;
                Set(x + k * 6 + gx, y + gy, top.Lerp(bottom, gy / 6f));
            }
        }
    }

    /// <summary>Renders a text string to a standalone texture (used by HUD labels and messages).</summary>
    public static ImageTexture TextTexture(string s, Color top, Color bottom)
    {
        var p = new Pix(Math.Max(1, TextWidth(s) + 1), 8);
        p.Text(0, 0, s, top, bottom);
        return p.ToTexture();
    }
}
