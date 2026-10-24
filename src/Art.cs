using System;
using Godot;

namespace Boomstick;

/// <summary>
/// Procedural sprite art: enemy atlases (idle, walk, attack, pain, death frames), projectiles,
/// hit effects and pickups. Everything is drawn once at startup with <see cref="Pix"/> and
/// cached in static fields, so a level restart does not redraw it.
/// </summary>
public static class Art
{
    // Enemy atlas frame indices.
    public const int FIdle = 0, FWalkA = 1, FWalkB = 2, FAttackWind = 3, FAttackFire = 4, FPain = 5,
        FDie1 = 6, FDie2 = 7, FCorpse = 8, EnemyFrames = 9;

    private static readonly Color Outline = new(0.06f, 0.03f, 0.03f);
    private static readonly Color Blood = new(0.55f, 0.03f, 0.02f);
    private static readonly Color BloodDark = new(0.32f, 0.02f, 0.02f);

    private struct Pose
    {
        public float Leg;    // -1..1 walk swing
        public float Arm;    // 0..1 attack raise
        public bool Fire;    // attack release frame (muzzle/fireball/claws forward)
        public float Mouth;  // 0..1
    }

    private static ImageTexture[] _enemy = new ImageTexture[3];

    public static ImageTexture Enemy(EnemyKind kind)
    {
        int i = (int)kind;
        return _enemy[i] ??= BuildEnemy(kind);
    }

    public static Vector2I EnemyFrameSize(EnemyKind kind) => kind switch
    {
        EnemyKind.Fiend => new Vector2I(40, 40),
        EnemyKind.Imp => new Vector2I(32, 48),
        _ => new Vector2I(56, 64),
    };

    private static ImageTexture BuildEnemy(EnemyKind kind)
    {
        var fs = EnemyFrameSize(kind);
        int fw = fs.X, fh = fs.Y;
        var atlas = new Pix(fw * EnemyFrames, fh);
        var poses = new Pose[]
        {
            new() { },                               // idle
            new() { Leg = -1f },                     // walk A
            new() { Leg = 1f },                      // walk B
            new() { Arm = 1f, Mouth = 0.6f },        // attack windup
            new() { Arm = 0.5f, Fire = true, Mouth = 1f }, // attack release
            new() { Arm = 0.3f, Mouth = 1f },        // pain
        };
        for (int f = 0; f < EnemyFrames; f++)
        {
            atlas.OX = f * fw;
            if (f <= FPain)
            {
                DrawEnemy(atlas, kind, poses[f], fw, fh);
                atlas.Outline(0, 0, fw, fh, Outline);
                if (f == FPain)
                    atlas.Tint(0, 0, fw, fh, new Color(1.1f, 0.75f, 0.75f), new Color(0.25f, 0.05f, 0.05f));
            }
            else
            {
                // Death frames: squash a pain pose towards the floor and add blood.
                var tmp = new Pix(fw, fh);
                DrawEnemy(tmp, kind, poses[FPain], fw, fh);
                float sy = f == FDie1 ? 0.7f : f == FDie2 ? 0.4f : 0.18f;
                float sx = f == FDie1 ? 1.05f : f == FDie2 ? 1.15f : 1.35f;
                BlitSquashed(atlas, tmp, fw, fh, sx, sy);
                atlas.Tint(0, 0, fw, fh, new Color(0.9f, 0.7f, 0.7f), new Color(0.08f, 0f, 0f));
                int pool = f == FDie1 ? fw / 5 : f == FDie2 ? fw / 3 : fw * 2 / 5;
                atlas.Ellipse(fw / 2f, fh - 2.5f, pool, 2.5f, Blood, 0.2f);
                for (int k = 0; k < 6 + f * 2; k++)
                {
                    float rx = ProcTex.Rand(k, f, 501 + (int)kind) * fw;
                    float ry = fh - 2 - ProcTex.Rand(k, f, 502) * fh * (1f - sy) * 0.6f;
                    atlas.Ellipse(rx, ry, 1.2f, 1f, k % 2 == 0 ? Blood : BloodDark, 0f);
                }
                if (f == FCorpse)
                {
                    // A few bones on the pile.
                    atlas.Line(fw * 0.3f, fh - 4, fw * 0.45f, fh - 6, 0.8f, new Color(0.85f, 0.82f, 0.7f));
                    atlas.Line(fw * 0.6f, fh - 5, fw * 0.72f, fh - 3, 0.8f, new Color(0.85f, 0.82f, 0.7f));
                }
                atlas.Outline(0, 0, fw, fh, Outline);
            }
        }
        atlas.OX = 0;
        return atlas.ToTexture();
    }

    private static void BlitSquashed(Pix dst, Pix src, int fw, int fh, float sx, float sy)
    {
        for (int y = 0; y < fh; y++)
        for (int x = 0; x < fw; x++)
        {
            // Map the destination pixel back to the source, anchored at the bottom centre.
            float srcX = (x + 0.5f - fw / 2f) / sx + fw / 2f;
            float srcY = fh - (fh - (y + 0.5f)) / sy;
            if (srcX < 0 || srcY < 0 || srcX >= fw || srcY >= fh) continue;
            var c = src.Get((int)srcX, (int)srcY);
            if (c.A >= 0.5f) dst.Set(x, y, c);
        }
    }

    private static void DrawEnemy(Pix p, EnemyKind kind, Pose pose, int w, int h)
    {
        switch (kind)
        {
            case EnemyKind.Fiend: DrawFiend(p, pose, w, h); break;
            case EnemyKind.Imp: DrawImp(p, pose, w, h); break;
            default: DrawBrute(p, pose, w, h); break;
        }
    }

    /// <summary>Melee rusher: a squat pink beast that is mostly mouth.</summary>
    private static void DrawFiend(Pix p, Pose pose, int w, int h)
    {
        var skin = new Color(0.86f, 0.42f, 0.46f);
        var dark = new Color(0.55f, 0.22f, 0.26f);
        float cx = w / 2f;
        // Legs.
        float l = pose.Leg * 3f;
        p.Line(cx - 6, 28, cx - 8 + l, 38, 2.6f, dark);
        p.Line(cx + 6, 28, cx + 8 - l, 38, 2.6f, dark);
        p.Ellipse(cx - 8 + l, 38.5f, 3.5f, 1.5f, new Color(0.3f, 0.12f, 0.12f));
        p.Ellipse(cx + 8 - l, 38.5f, 3.5f, 1.5f, new Color(0.3f, 0.12f, 0.12f));
        // Arms with claws, raised when attacking.
        float ay = 22 - pose.Arm * 10f;
        float ax = pose.Fire ? 4f : 0f;
        p.Line(cx - 11, 20, cx - 16 + ax, ay, 2.2f, dark);
        p.Line(cx + 11, 20, cx + 16 - ax, ay, 2.2f, dark);
        for (int k = -1; k <= 1; k++)
        {
            p.Line(cx - 16 + ax, ay, cx - 18 + ax + k * 1.5f, ay - 4, 0.5f, new Color(0.95f, 0.92f, 0.8f));
            p.Line(cx + 16 - ax, ay, cx + 18 - ax + k * 1.5f, ay - 4, 0.5f, new Color(0.95f, 0.92f, 0.8f));
        }
        // Body.
        p.Ellipse(cx, 21, 13, 11, skin);
        p.Grain(0, 8, w, 26, 0.25f, 77);
        // Horns.
        p.Tri(cx - 9, 12, cx - 6, 12, cx - 10, 5, new Color(0.9f, 0.86f, 0.72f));
        p.Tri(cx + 6, 12, cx + 9, 12, cx + 10, 5, new Color(0.9f, 0.86f, 0.72f));
        // Eyes.
        p.Rect((int)cx - 7, 14, 3, 2, new Color(1f, 0.9f, 0.15f));
        p.Rect((int)cx + 4, 14, 3, 2, new Color(1f, 0.9f, 0.15f));
        p.Set((int)cx - 6, 14, Colors.Black);
        p.Set((int)cx + 5, 14, Colors.Black);
        // Mouth with teeth.
        int mh = 3 + (int)(pose.Mouth * 5f);
        p.Rect((int)cx - 9, 20, 18, mh, new Color(0.25f, 0.02f, 0.04f));
        for (int x = (int)cx - 9; x < cx + 9; x += 2)
        {
            p.Set(x, 20, new Color(0.97f, 0.95f, 0.88f));
            p.Set(x, 21, new Color(0.9f, 0.88f, 0.8f));
            p.Set(x + 1, 20 + mh - 1, new Color(0.97f, 0.95f, 0.88f));
        }
    }

    /// <summary>Ranged shooter: a lanky brown imp with shoulder spikes that throws fireballs.</summary>
    private static void DrawImp(Pix p, Pose pose, int w, int h)
    {
        var skin = new Color(0.55f, 0.36f, 0.22f);
        var dark = new Color(0.36f, 0.22f, 0.13f);
        float cx = w / 2f;
        float l = pose.Leg * 3f;
        // Legs.
        p.Line(cx - 3, 30, cx - 5 + l, 46, 1.8f, dark);
        p.Line(cx + 3, 30, cx + 5 - l, 46, 1.8f, dark);
        // Torso and head.
        p.Ellipse(cx, 24, 7.5f, 9f, skin);
        p.Ellipse(cx, 11, 5f, 5.5f, skin);
        p.Grain(0, 4, w, 30, 0.3f, 91);
        // Ribs.
        for (int k = 0; k < 3; k++)
            p.Line(cx - 4, 21 + k * 3, cx + 4, 21 + k * 3, 0.5f, dark);
        // Shoulder spikes.
        for (int k = 0; k < 3; k++)
        {
            p.Tri(cx - 7 - k * 1.5f, 18 + k, cx - 5 - k * 1.5f, 18 + k, cx - 9 - k * 2, 13 + k, new Color(0.9f, 0.88f, 0.78f));
            p.Tri(cx + 5 + k * 1.5f, 18 + k, cx + 7 + k * 1.5f, 18 + k, cx + 9 + k * 2, 13 + k, new Color(0.9f, 0.88f, 0.78f));
        }
        // Glowing eyes and mouth.
        p.Rect((int)cx - 3, 10, 2, 2, new Color(1f, 0.55f, 0.05f));
        p.Rect((int)cx + 1, 10, 2, 2, new Color(1f, 0.55f, 0.05f));
        p.Rect((int)cx - 2, 14, 4, 1 + (int)(pose.Mouth * 2), new Color(0.2f, 0.02f, 0.02f));
        // Arms: the right arm (screen left) throws.
        p.Line(cx + 6, 19, cx + 8, 30, 1.4f, dark);
        float hx = cx - 9, hy = 30 - pose.Arm * 26f;
        if (pose.Fire) { hx = cx - 4; hy = 18; }
        p.Line(cx - 6, 19, hx, hy, 1.4f, dark);
        if (pose.Arm > 0.7f || pose.Fire)
        {
            float r = pose.Fire ? 3.5f : 4.5f;
            p.Ellipse(hx, hy - 1, r, r, new Color(1f, 0.55f, 0.1f), 0.1f);
            p.Ellipse(hx, hy - 1, r * 0.5f, r * 0.5f, new Color(1f, 0.95f, 0.6f), 0f);
        }
    }

    /// <summary>Tough enemy: a hulking green brute with bone plates and an arm cannon.</summary>
    private static void DrawBrute(Pix p, Pose pose, int w, int h)
    {
        var skin = new Color(0.36f, 0.46f, 0.3f);
        var dark = new Color(0.22f, 0.3f, 0.18f);
        var bone = new Color(0.84f, 0.8f, 0.66f);
        var metal = new Color(0.5f, 0.52f, 0.56f);
        float cx = w / 2f;
        float l = pose.Leg * 3.5f;
        // Legs.
        p.Line(cx - 8, 42, cx - 10 + l, 61, 4f, dark);
        p.Line(cx + 8, 42, cx + 10 - l, 61, 4f, dark);
        p.Ellipse(cx - 10 + l, 62, 5f, 2f, new Color(0.15f, 0.15f, 0.12f));
        p.Ellipse(cx + 10 - l, 62, 5f, 2f, new Color(0.15f, 0.15f, 0.12f));
        // Torso.
        p.Ellipse(cx, 31, 19, 15, skin);
        p.Grain(0, 14, w, 34, 0.3f, 113);
        // Bone plates.
        p.Ellipse(cx - 8, 27, 5, 4, bone);
        p.Ellipse(cx + 8, 27, 5, 4, bone);
        p.Ellipse(cx, 37, 6, 3, bone);
        // Head.
        p.Ellipse(cx, 15, 6.5f, 5.5f, skin);
        p.Rect((int)cx - 4, 14, 3, 2, new Color(1f, 0.15f, 0.1f));
        p.Rect((int)cx + 1, 14, 3, 2, new Color(1f, 0.15f, 0.1f));
        p.Rect((int)cx - 3, 18, 6, 1 + (int)(pose.Mouth * 2), new Color(0.1f, 0.05f, 0.05f));
        // Left fist (screen right).
        float fy = 40 - pose.Arm * 6f;
        p.Line(cx + 17, 26, cx + 21, fy, 3f, dark);
        p.Ellipse(cx + 21, fy + 2, 4, 4, skin);
        // Arm cannon (screen left), raised when attacking.
        float cy = 40 - pose.Arm * 12f;
        p.Line(cx - 17, 26, cx - 21, cy, 3f, dark);
        p.Rect((int)(cx - 26), (int)cy - 2, 10, 9, metal);
        p.Rect((int)(cx - 25), (int)cy + 5, 8, 3, new Color(0.25f, 0.25f, 0.28f));
        p.Grain((int)(cx - 26), (int)cy - 2, 10, 11, 0.3f, 117);
        if (pose.Fire)
        {
            p.Ellipse(cx - 21, cy + 10, 5, 4, new Color(0.4f, 1f, 0.3f), 0.1f);
            p.Ellipse(cx - 21, cy + 10, 2.5f, 2, new Color(0.9f, 1f, 0.8f), 0f);
        }
    }

    // ---- Projectiles and effects ----------------------------------------------------------

    private static ImageTexture _fireball, _plasma, _rocket, _puff, _blood, _explosion;

    /// <summary>Two-frame fireball atlas (16x16 frames).</summary>
    public static ImageTexture Fireball() => _fireball ??= Ball(new Color(1f, 0.45f, 0.08f), new Color(1f, 0.95f, 0.55f), 31);

    /// <summary>Two-frame green plasma atlas (16x16 frames).</summary>
    public static ImageTexture Plasma() => _plasma ??= Ball(new Color(0.3f, 0.95f, 0.25f), new Color(0.9f, 1f, 0.85f), 37);

    private static ImageTexture Ball(Color outer, Color inner, int seed)
    {
        var p = new Pix(32, 16);
        for (int f = 0; f < 2; f++)
        {
            p.OX = f * 16;
            float r = f == 0 ? 6.5f : 5.5f;
            p.Ellipse(8, 8, r, r, outer, 0.2f);
            p.Ellipse(8, 8, r * 0.55f, r * 0.55f, inner, 0f);
            for (int k = 0; k < 5; k++)
            {
                float a = ProcTex.Rand(k, f, seed) * Mathf.Tau;
                p.Ellipse(8 + MathF.Cos(a) * r, 8 + MathF.Sin(a) * r, 1.5f, 1.5f, outer, 0f);
            }
        }
        return p.ToTexture();
    }

    /// <summary>Two-frame rocket atlas (16x16): the rocket nose seen head-on with a flickering exhaust.</summary>
    public static ImageTexture Rocket() => _rocket ??= BuildRocket();

    private static ImageTexture BuildRocket()
    {
        var p = new Pix(32, 16);
        for (int f = 0; f < 2; f++)
        {
            p.OX = f * 16;
            p.Ellipse(8, 8, f == 0 ? 7 : 6, f == 0 ? 7 : 6, new Color(1f, 0.6f, 0.1f), 0.1f);
            p.Ellipse(8, 8, 4, 4, new Color(0.55f, 0.55f, 0.5f), 0.4f);
            p.Ellipse(8, 8, 1.5f, 1.5f, new Color(0.85f, 0.2f, 0.1f), 0f);
        }
        return p.ToTexture();
    }

    /// <summary>Four-frame grey bullet puff (16x16 frames).</summary>
    public static ImageTexture Puff() => _puff ??= Burst(16, new Color(0.75f, 0.72f, 0.65f), new Color(1f, 0.95f, 0.6f), 41);

    /// <summary>Four-frame blood spurt (16x16 frames).</summary>
    public static ImageTexture BloodFx() => _blood ??= Burst(16, new Color(0.7f, 0.04f, 0.03f), new Color(0.9f, 0.1f, 0.05f), 43);

    /// <summary>Four-frame explosion (32x32 frames).</summary>
    public static ImageTexture Explosion() => _explosion ??= Burst(32, new Color(1f, 0.45f, 0.08f), new Color(1f, 0.95f, 0.6f), 47);

    private static ImageTexture Burst(int s, Color outer, Color core, int seed)
    {
        var p = new Pix(s * 4, s);
        for (int f = 0; f < 4; f++)
        {
            p.OX = f * s;
            float t = (f + 1) / 4f;
            float r = s * (0.2f + 0.25f * t);
            Color o = f >= 2 ? outer.Lerp(new Color(0.3f, 0.28f, 0.26f), (f - 1) / 3f) : outer;
            int blobs = 5 + f * 2;
            for (int k = 0; k < blobs; k++)
            {
                float a = ProcTex.Rand(k, f, seed) * Mathf.Tau;
                float d = ProcTex.Rand(k, f, seed + 1) * r;
                float br = s * (0.1f + 0.08f * ProcTex.Rand(k, f, seed + 2)) * (1.2f - t * 0.4f);
                p.Ellipse(s / 2f + MathF.Cos(a) * d, s / 2f + MathF.Sin(a) * d, br, br, o, 0.3f);
            }
            if (f < 2)
                p.Ellipse(s / 2f, s / 2f, r * 0.5f, r * 0.5f, core, 0f);
        }
        return p.ToTexture();
    }

    // ---- Pickups ----------------------------------------------------------------------------

    private static readonly ImageTexture[] _pickups = new ImageTexture[(int)PickupKind.Count];

    public static ImageTexture Pickup(PickupKind kind) => _pickups[(int)kind] ??= BuildPickup(kind);

    public static readonly Vector2I PickupFrameSize = new(32, 24);
    private static ImageTexture _pickupAtlas;

    /// <summary>Every pickup side by side (frame = PickupKind), for the batched pickup MultiMesh.</summary>
    public static ImageTexture PickupAtlas()
    {
        if (_pickupAtlas != null) return _pickupAtlas;
        int n = (int)PickupKind.Count, w = PickupFrameSize.X, h = PickupFrameSize.Y;
        var atlas = Image.CreateEmpty(w * n, h, false, Image.Format.Rgba8);
        for (int i = 0; i < n; i++)
        {
            var img = Pickup((PickupKind)i).GetImage();
            img.Convert(Image.Format.Rgba8);
            atlas.BlitRect(img, new Rect2I(0, 0, w, h), new Vector2I(i * w, 0));
        }
        return _pickupAtlas = ImageTexture.CreateFromImage(atlas);
    }

    private static ImageTexture BuildPickup(PickupKind kind)
    {
        var p = new Pix(32, 24);
        var white = new Color(0.92f, 0.92f, 0.9f);
        var red = new Color(0.85f, 0.08f, 0.06f);
        switch (kind)
        {
            case PickupKind.Stim:
                p.Rect(11, 12, 10, 12, white);
                p.Rect(15, 14, 2, 8, red);
                p.Rect(12, 17, 8, 2, red);
                break;
            case PickupKind.Medkit:
                p.Rect(5, 8, 22, 16, white);
                p.Rect(14, 4, 4, 4, new Color(0.4f, 0.4f, 0.4f));
                p.Rect(14, 10, 4, 12, red);
                p.Rect(10, 14, 12, 4, red);
                break;
            case PickupKind.Armor:
            case PickupKind.MegaArmor:
            {
                var c = kind == PickupKind.Armor ? new Color(0.2f, 0.75f, 0.25f) : new Color(0.2f, 0.4f, 0.95f);
                p.Rect(8, 6, 16, 17, c);
                p.Rect(4, 6, 5, 7, c);
                p.Rect(23, 6, 5, 7, c);
                p.Rect(13, 6, 6, 4, default);
                p.Rect(9, 14, 14, 2, Pix.Mul(c, 0.6f));
                p.Rect(15, 10, 2, 13, Pix.Mul(c, 0.6f));
                break;
            }
            case PickupKind.Clip:
                p.Rect(9, 12, 14, 12, new Color(0.45f, 0.4f, 0.25f));
                for (int k = 0; k < 4; k++)
                {
                    p.Rect(10 + k * 3, 6, 2, 6, new Color(0.85f, 0.65f, 0.2f));
                    p.Rect(10 + k * 3, 6, 2, 2, new Color(0.7f, 0.5f, 0.3f));
                }
                break;
            case PickupKind.Shells:
                for (int k = 0; k < 4; k++)
                {
                    p.Rect(7 + k * 5, 10, 4, 14, red);
                    p.Rect(7 + k * 5, 20, 4, 4, new Color(0.85f, 0.7f, 0.25f));
                }
                break;
            case PickupKind.Rockets:
                p.Rect(13, 4, 6, 20, new Color(0.5f, 0.52f, 0.45f));
                p.Tri(13, 4, 19, 4, 16, 0, new Color(0.8f, 0.2f, 0.1f));
                p.Rect(11, 19, 10, 5, new Color(0.35f, 0.36f, 0.3f));
                break;
            case PickupKind.Shotgun:
                p.Rect(1, 12, 22, 3, new Color(0.3f, 0.3f, 0.33f));
                p.Rect(1, 15, 20, 2, new Color(0.22f, 0.22f, 0.25f));
                p.Rect(10, 15, 8, 3, new Color(0.45f, 0.28f, 0.12f));
                p.Rect(22, 13, 9, 6, new Color(0.45f, 0.28f, 0.12f));
                p.Rect(25, 18, 6, 4, new Color(0.38f, 0.23f, 0.1f));
                break;
            case PickupKind.Launcher:
                p.Rect(1, 9, 28, 7, new Color(0.35f, 0.4f, 0.35f));
                p.Rect(1, 10, 3, 5, new Color(0.15f, 0.15f, 0.15f));
                p.Rect(14, 16, 4, 6, new Color(0.25f, 0.25f, 0.25f));
                p.Rect(22, 16, 3, 4, new Color(0.25f, 0.25f, 0.25f));
                p.Rect(6, 8, 18, 1, new Color(0.55f, 0.6f, 0.55f));
                break;
        }
        p.Grain(0, 0, 32, 24, 0.2f, 200 + (int)kind);
        p.Outline(0, 0, 32, 24, Outline);
        return p.ToTexture();
    }
}
