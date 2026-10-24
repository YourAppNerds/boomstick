using System;
using Godot;

namespace Boomstick;

/// <summary>
/// Procedural HUD art: first-person weapon frames, the status bar background, the player face
/// and the crosshair. Drawn once and cached.
/// </summary>
public static class HudArt
{
    public const int WeaponW = 120, WeaponH = 90;
    public const int BarW = 320, BarH = 26;
    public const int FaceW = 24, FaceH = 26;

    private static readonly Color Outline = new(0.05f, 0.04f, 0.04f);
    private static readonly Color Skin = new(0.86f, 0.62f, 0.46f);
    private static readonly Color SkinDark = new(0.66f, 0.44f, 0.32f);
    private static readonly Color Gun = new(0.32f, 0.33f, 0.36f);
    private static readonly Color GunDark = new(0.18f, 0.18f, 0.2f);
    private static readonly Color Wood = new(0.5f, 0.3f, 0.14f);

    private static ImageTexture[][] _weapons;

    /// <summary>Frames per weapon: [0] idle, [1] firing with muzzle flash, [2..] recoil/pump.</summary>
    public static ImageTexture[] WeaponFrames(WeaponId w)
    {
        _weapons ??= new[] { BuildPistol(), BuildShotgun(), BuildLauncher() };
        return _weapons[(int)w];
    }

    private static void Flash(Pix p, float x, float y, float r)
    {
        for (int k = 0; k < 8; k++)
        {
            float a = k * Mathf.Tau / 8f + 0.2f;
            float len = r * (k % 2 == 0 ? 1.4f : 0.9f);
            p.Line(x, y, x + MathF.Cos(a) * len, y + MathF.Sin(a) * len * 0.8f, 2f, new Color(1f, 0.7f, 0.15f));
        }
        p.Ellipse(x, y, r * 0.8f, r * 0.7f, new Color(1f, 0.85f, 0.3f), 0f);
        p.Ellipse(x, y, r * 0.45f, r * 0.4f, new Color(1f, 1f, 0.85f), 0f);
    }

    private static void Hand(Pix p, float x, float y, float s = 1f)
    {
        p.Ellipse(x, y, 9 * s, 8 * s, Skin);
        for (int k = 0; k < 4; k++)
            p.Line(x - 6 * s + k * 4 * s, y - 5 * s, x - 6 * s + k * 4 * s, y - 2 * s, 0.5f, SkinDark);
        // Sleeve.
        p.Rect((int)(x - 9 * s), (int)(y + 6 * s), (int)(18 * s), 30, new Color(0.25f, 0.3f, 0.2f));
    }

    private static ImageTexture Finish(Pix p)
    {
        p.Grain(0, 0, p.W, p.H, 0.12f, 300);
        p.Outline(0, 0, p.W, p.H, Outline);
        return p.ToTexture();
    }

    private static ImageTexture[] BuildPistol()
    {
        var frames = new ImageTexture[3];
        for (int f = 0; f < 3; f++)
        {
            var p = new Pix(WeaponW, WeaponH);
            int dy = f == 2 ? -6 : 0;
            if (f == 1) Flash(p, 60, 30 + dy, 12);
            p.Rect(53, 40 + dy, 14, 26, Gun);
            p.Rect(55, 40 + dy, 10, 3, GunDark);
            p.Rect(58, 37 + dy, 4, 3, GunDark);
            p.Rect(54, 64 + dy, 12, 30, GunDark);
            Hand(p, 60, 78 + dy);
            frames[f] = Finish(p);
        }
        return frames;
    }

    private static ImageTexture[] BuildShotgun()
    {
        var frames = new ImageTexture[4];
        for (int f = 0; f < 4; f++)
        {
            var p = new Pix(WeaponW, WeaponH);
            int dy = f == 1 ? 4 : 0;
            int pump = f == 2 ? 6 : f == 3 ? 12 : 0;
            int dx = f >= 2 ? -4 : 0;
            if (f == 1) Flash(p, 60 + dx, 16 + dy, 16);
            // Barrel and magazine tube.
            p.Rect(55 + dx, 22 + dy, 10, 50, Gun);
            p.Rect(57 + dx, 22 + dy, 6, 2, GunDark);
            p.Rect(56 + dx, 26 + dy, 2, 44, new Color(0.45f, 0.46f, 0.5f));
            // Pump forend with the left hand on it.
            p.Rect(50 + dx, 44 + dy + pump, 20, 14, Wood);
            for (int k = 0; k < 4; k++)
                p.Rect(51 + dx, 46 + dy + pump + k * 3, 18, 1, Pix.Mul(Wood, 0.7f));
            Hand(p, 47 + dx, 56 + dy + pump, 0.8f);
            // Receiver and right hand.
            p.Rect(51 + dx, 68 + dy, 18, 22, GunDark);
            Hand(p, 66 + dx, 84 + dy);
            frames[f] = Finish(p);
        }
        return frames;
    }

    private static ImageTexture[] BuildLauncher()
    {
        var frames = new ImageTexture[3];
        var tube = new Color(0.36f, 0.42f, 0.36f);
        for (int f = 0; f < 3; f++)
        {
            var p = new Pix(WeaponW, WeaponH);
            int dy = f == 2 ? 8 : f == 1 ? 4 : 0;
            if (f == 1) Flash(p, 60, 24 + dy, 18);
            p.Rect(44, 30 + dy, 32, 60, tube);
            p.Ellipse(60, 31 + dy, 16, 6, tube);
            p.Ellipse(60, 31 + dy, 11, 4, new Color(0.08f, 0.08f, 0.08f), 0f);
            p.Rect(44, 50 + dy, 32, 3, Pix.Mul(tube, 0.7f));
            p.Rect(44, 70 + dy, 32, 3, Pix.Mul(tube, 0.7f));
            p.Rect(76, 44 + dy, 6, 16, GunDark);
            Hand(p, 38, 76 + dy, 0.9f);
            Hand(p, 82, 80 + dy, 0.9f);
            frames[f] = Finish(p);
        }
        return frames;
    }

    private static ImageTexture _bar;

    /// <summary>
    /// Status bar background, 320x26 pixels: recessed sections for ammo, health, arms, face,
    /// armor, the ammo table and kills, with their captions.
    /// </summary>
    public static ImageTexture Bar() => _bar ??= BuildBar();

    // Section x ranges (bar pixels), shared with the HUD layout.
    public static readonly int[] Sections = { 0, 46, 102, 138, 168, 222, 284, 320 };

    private static ImageTexture BuildBar()
    {
        var p = new Pix(BarW, BarH);
        var stone = new Color(0.42f, 0.4f, 0.38f);
        for (int y = 0; y < BarH; y++)
        for (int x = 0; x < BarW; x++)
        {
            float n = (ProcTex.Rand(x / 2, y / 2, 401) - 0.5f) * 0.3f + (ProcTex.Rand(x, y, 402) - 0.5f) * 0.1f;
            p.Set(x, y, Pix.Mul(stone, 1f + n));
        }
        p.Rect(0, 0, BarW, 1, new Color(0.6f, 0.58f, 0.55f));
        for (int s = 0; s < Sections.Length - 1; s++)
        {
            int x0 = Sections[s] + 1, x1 = Sections[s + 1] - 1;
            // Recessed panel: dark inside, light bottom/right edge, dark top/left edge.
            p.Rect(x0, 2, x1 - x0, BarH - 4, new Color(0.2f, 0.19f, 0.18f));
            p.Rect(x0, 2, x1 - x0, 1, new Color(0.1f, 0.1f, 0.1f));
            p.Rect(x0, 2, 1, BarH - 4, new Color(0.1f, 0.1f, 0.1f));
            p.Rect(x0, BarH - 3, x1 - x0, 1, new Color(0.6f, 0.58f, 0.55f));
            p.Rect(x1 - 1, 2, 1, BarH - 4, new Color(0.6f, 0.58f, 0.55f));
        }
        var cap = new Color(0.85f, 0.8f, 0.7f);
        var capB = new Color(0.6f, 0.55f, 0.45f);
        Caption(p, 0, "AMMO", cap, capB);
        Caption(p, 1, "HEALTH", cap, capB);
        Caption(p, 2, "ARMS", cap, capB);
        Caption(p, 4, "ARMOR", cap, capB);
        Caption(p, 6, "KILLS", cap, capB);
        return p.ToTexture();
    }

    private static void Caption(Pix p, int section, string s, Color top, Color bottom)
    {
        int x0 = Sections[section], x1 = Sections[section + 1];
        p.Text((x0 + x1) / 2 - Pix.TextWidth(s) / 2, BarH - 11, s, top, bottom);
    }

    private static ImageTexture _crosshair;

    public static ImageTexture Crosshair() => _crosshair ??= BuildCrosshair();

    private static ImageTexture BuildCrosshair()
    {
        var p = new Pix(9, 9);
        var c = new Color(1f, 0.9f, 0.4f);
        for (int k = 0; k < 3; k++)
        {
            p.Set(4, k, c); p.Set(4, 8 - k, c);
            p.Set(k, 4, c); p.Set(8 - k, 4, c);
        }
        return p.ToTexture();
    }

    // ---- Face ---------------------------------------------------------------------------------

    public enum FaceMood { Normal, Ouch, Grin, Dead }

    private static readonly ImageTexture[] _faces = new ImageTexture[5 * 6 + 1];

    /// <summary>
    /// Face for a health tier (0 = healthy .. 4 = nearly dead), a look direction (-1, 0, 1) and
    /// a mood.
    /// </summary>
    public static ImageTexture Face(int tier, int look, FaceMood mood)
    {
        int idx = mood == FaceMood.Dead ? 30 : tier * 6 + (mood == FaceMood.Normal ? look + 1 : mood == FaceMood.Ouch ? 3 : 4);
        return _faces[idx] ??= BuildFace(tier, look, mood);
    }

    private static ImageTexture BuildFace(int tier, int look, FaceMood mood)
    {
        var p = new Pix(FaceW, FaceH);
        float pale = mood == FaceMood.Dead ? 0.6f : 1f - tier * 0.06f;
        var skin = Pix.Mul(Skin, pale);
        // Neck and head.
        p.Rect(8, 21, 8, 5, Pix.Mul(skin, 0.8f));
        p.Ellipse(12, 12.5f, 9, 10.5f, skin, 0.3f);
        // Hair.
        p.Ellipse(12, 4.5f, 8.5f, 3.5f, new Color(0.3f, 0.2f, 0.1f), 0.2f);
        p.Rect(3, 4, 2, 6, new Color(0.3f, 0.2f, 0.1f));
        p.Rect(19, 4, 2, 6, new Color(0.3f, 0.2f, 0.1f));
        var white = new Color(0.95f, 0.95f, 0.92f);
        var pupil = new Color(0.15f, 0.3f, 0.6f);
        if (mood == FaceMood.Dead)
        {
            p.Line(6, 10, 9, 13, 0.5f, Outline); p.Line(9, 10, 6, 13, 0.5f, Outline);
            p.Line(15, 10, 18, 13, 0.5f, Outline); p.Line(18, 10, 15, 13, 0.5f, Outline);
            p.Rect(9, 18, 6, 2, new Color(0.3f, 0.05f, 0.05f));
        }
        else
        {
            bool wide = mood == FaceMood.Ouch;
            int eh = wide ? 3 : 2;
            p.Rect(6, 11 - (wide ? 1 : 0), 4, eh, white);
            p.Rect(14, 11 - (wide ? 1 : 0), 4, eh, white);
            int px = Math.Clamp(look, -1, 1);
            p.Rect(7 + px + (px < 0 ? 0 : 1) - (px > 0 ? 0 : 0), 11, 2, 2, pupil);
            p.Rect(15 + px, 11, 2, 2, pupil);
            // Eyebrows: angry by default, raised when hurt.
            int by = wide ? 7 : 9;
            p.Line(5, by - (wide ? 0 : 1), 10, by, 0.5f, new Color(0.25f, 0.15f, 0.08f));
            p.Line(14, by, 19, by - (wide ? 0 : 1), 0.5f, new Color(0.25f, 0.15f, 0.08f));
            // Nose.
            p.Rect(11, 13, 2, 3, Pix.Mul(skin, 0.8f));
            // Mouth.
            if (mood == FaceMood.Ouch)
                p.Ellipse(12, 19, 2.5f, 2.2f, new Color(0.3f, 0.05f, 0.05f), 0f);
            else if (mood == FaceMood.Grin)
            {
                p.Rect(7, 17, 10, 3, new Color(0.3f, 0.05f, 0.05f));
                p.Rect(8, 17, 8, 1, white);
                p.Set(6, 16, new Color(0.3f, 0.05f, 0.05f)); p.Set(17, 16, new Color(0.3f, 0.05f, 0.05f));
            }
            else
                p.Rect(8, 18, 8, 1, new Color(0.35f, 0.1f, 0.08f));
        }
        // Blood and bruises grow with damage.
        int marks = mood == FaceMood.Dead ? 10 : tier * 2;
        for (int k = 0; k < marks; k++)
        {
            float x = 4 + ProcTex.Rand(k, tier, 601) * 16, y = 6 + ProcTex.Rand(k, tier, 602) * 14;
            p.Ellipse(x, y, 1.2f, 1.6f, new Color(0.6f, 0.05f, 0.04f), 0f);
            if (k % 3 == 0) p.Line(x, y, x, y + 3, 0.5f, new Color(0.5f, 0.04f, 0.03f));
        }
        p.Outline(0, 0, FaceW, FaceH, Outline);
        return p.ToTexture();
    }
}
