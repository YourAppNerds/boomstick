using System;
using Godot;

namespace Boomstick;

/// <summary>
/// Procedural, chunky pixel-art textures generated at startup so the game needs no image assets.
/// All textures tile and use a small quantized palette.
/// </summary>
public static class ProcTex
{
    public const int Size = 64;

    private static uint Hash(int x, int y, int seed)
    {
        unchecked
        {
            uint h = (uint)(x * 374761393 + y * 668265263 + seed * 2147483647);
            h = (h ^ (h >> 13)) * 1274126177u;
            return h ^ (h >> 16);
        }
    }

    /// <summary>Deterministic value in [0, 1).</summary>
    public static float Rand(int x, int y, int seed) => (Hash(x, y, seed) & 0xFFFF) / 65536f;

    private static byte Q(float v)
    {
        // Quantize to 16 levels for a crunchy, limited palette.
        v = Mathf.Clamp(v, 0f, 1f);
        return (byte)(Mathf.Round(v * 15f) * 17f);
    }

    public static ImageTexture Make(int w, int h, Func<int, int, Color> pixel, bool mipmaps = true)
    {
        var data = new byte[w * h * 4];
        int i = 0;
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            Color c = pixel(x, y);
            data[i++] = Q(c.R);
            data[i++] = Q(c.G);
            data[i++] = Q(c.B);
            data[i++] = (byte)(c.A >= 0.5f ? 255 : 0);
        }
        var img = Image.CreateFromData(w, h, false, Image.Format.Rgba8, data);
        if (mipmaps)
            img.GenerateMipmaps();
        return ImageTexture.CreateFromImage(img);
    }

    /// <summary>
    /// Generic block pattern: rows of height <paramref name="rowH"/>, blocks of width
    /// <paramref name="blockW"/>, shifted by <paramref name="rowShift"/> per row, with
    /// mortar lines, a bevel and per-block shade variation.
    /// </summary>
    private static Color Blocks(int x, int y, int rowH, int blockW, int rowShift, Color baseC, Color mortar,
        int seed, float variance, float grain)
    {
        int row = y / rowH;
        int shift = rowShift >= 0 ? row * rowShift : (int)(Rand(row, 0, seed) * blockW);
        int bx = ((x + shift) % Size + Size) % Size;
        int col = bx / blockW;
        int lx = bx % blockW, ly = y % rowH;
        if (lx == 0 || ly == 0)
            return mortar;
        float shade = 1f + (Rand(col, row, seed + 1) - 0.5f) * variance;
        shade += (Rand(x, y, seed + 2) - 0.5f) * grain;
        if (lx == 1 || ly == 1) shade += 0.12f;                  // top/left highlight
        if (lx == blockW - 1 || ly == rowH - 1) shade -= 0.15f;  // bottom/right shadow
        return new Color(baseC.R * shade, baseC.G * shade, baseC.B * shade);
    }

    public static ImageTexture Stone() => Make(Size, Size, (x, y) =>
        Blocks(x, y, 16, 32, -1, new Color(0.45f, 0.44f, 0.42f), new Color(0.15f, 0.14f, 0.13f), 11, 0.35f, 0.25f));

    public static ImageTexture Brick() => Make(Size, Size, (x, y) =>
        Blocks(x, y, 8, 16, 8, new Color(0.55f, 0.24f, 0.16f), new Color(0.2f, 0.17f, 0.14f), 23, 0.3f, 0.2f));

    public static ImageTexture Metal() => Make(Size, Size, (x, y) =>
    {
        Color c = Blocks(x, y, 32, 32, 0, new Color(0.35f, 0.4f, 0.45f), new Color(0.1f, 0.1f, 0.12f), 37, 0.15f, 0.08f);
        int lx = x % 32, ly = y % 32;
        bool rivet = (lx == 4 || lx == 28) && (ly == 4 || ly == 28);
        if (rivet) return new Color(0.75f, 0.78f, 0.8f);
        if (ly >= 14 && ly <= 17 && lx > 2 && lx < 30) // hazard stripe across each panel
            return ((x + y) / 4 % 2 == 0) ? new Color(0.8f, 0.65f, 0.1f) : new Color(0.12f, 0.12f, 0.12f);
        return c;
    });

    public static ImageTexture Floor() => Make(Size, Size, (x, y) =>
        Blocks(x, y, 32, 32, 16, new Color(0.32f, 0.27f, 0.22f), new Color(0.12f, 0.1f, 0.08f), 41, 0.25f, 0.35f));

    public static ImageTexture Tiles() => Make(Size, Size, (x, y) =>
    {
        bool dark = ((x / 16) + (y / 16)) % 2 == 0;
        Color b = dark ? new Color(0.22f, 0.3f, 0.26f) : new Color(0.38f, 0.45f, 0.4f);
        return Blocks(x, y, 16, 16, 0, b, new Color(0.08f, 0.1f, 0.09f), 53, 0.1f, 0.15f);
    });

    public static ImageTexture Ceiling() => Make(Size, Size, (x, y) =>
        Blocks(x, y, 16, 16, 0, new Color(0.25f, 0.25f, 0.27f), new Color(0.1f, 0.1f, 0.1f), 61, 0.2f, 0.2f));

    public static ImageTexture Lamp() => Make(Size, Size, (x, y) =>
    {
        int lx = x % 32, ly = y % 32;
        if (lx < 3 || ly < 3 || lx > 28 || ly > 28) return new Color(0.3f, 0.3f, 0.3f);
        float n = Rand(x, y, 71) * 0.1f;
        return new Color(1f - n, 0.95f - n, 0.75f - n);
    });

    /// <summary>Glowing green exit pad with chevrons.</summary>
    public static ImageTexture ExitFloor() => Make(Size, Size, (x, y) =>
    {
        int lx = x % 32, ly = y % 32;
        if (lx < 2 || ly < 2 || lx > 29 || ly > 29) return new Color(0.15f, 0.3f, 0.15f);
        // Chevron pointing "up" the texture.
        int d = Math.Abs(lx - 16);
        bool chevron = ly - 6 >= d / 1 - 2 && ly - 6 <= d + 4 && ly < 26;
        float n = Rand(x, y, 91) * 0.15f;
        return chevron ? new Color(0.75f - n, 1f - n, 0.6f - n) : new Color(0.1f + n, 0.55f - n, 0.15f);
    });

    /// <summary>32x32 imp-like blob with transparent background for billboard dummies.</summary>
    public static ImageTexture Dummy() => Make(32, 32, (x, y) =>
    {
        float cx = x - 15.5f, cy = y - 17f;
        bool horn = (y >= 3 && y < 9) && (Mathf.Abs(x - 8 - (9 - y) * 0.5f) < 1.5f || Mathf.Abs(x - 23 + (9 - y) * 0.5f) < 1.5f);
        bool body = (cx * cx) / (12f * 12f) + (cy * cy) / (14f * 14f) <= 1f && y > 4;
        if (horn) return new Color(0.9f, 0.85f, 0.7f);
        if (!body) return new Color(0, 0, 0, 0);
        bool eye = (y >= 11 && y <= 13) && ((x >= 9 && x <= 12) || (x >= 19 && x <= 22));
        if (eye) return new Color(1f, 0.9f, 0.1f);
        bool mouth = y >= 19 && y <= 21 && x >= 10 && x <= 21;
        if (mouth) return (x % 3 == 0) ? new Color(0.95f, 0.95f, 0.9f) : new Color(0.15f, 0.02f, 0.02f);
        float shade = 1f - (cx * cx + cy * cy) / 400f + (Rand(x, y, 83) - 0.5f) * 0.2f;
        return new Color(0.65f * shade, 0.28f * shade, 0.18f * shade);
    }, mipmaps: false);
}
