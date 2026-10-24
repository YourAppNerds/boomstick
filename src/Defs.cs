using Godot;

namespace Boomstick;

public enum EnemyKind { Fiend, Imp, Brute }

public enum ProjKind { Fireball, Plasma, Rocket }

public enum PickupKind { Stim, Medkit, Armor, MegaArmor, Clip, Shells, Rockets, Shotgun, Launcher, Count }

public enum WeaponId { Pistol, Shotgun, Launcher }

public enum AmmoType { Bullets, Shells, Rockets }

/// <summary>Static tuning data for one enemy type.</summary>
public sealed class EnemyDef
{
    public EnemyKind Kind;
    public string Name;
    public int Health;
    public float Speed;
    public float Radius;
    public float PixelSize;
    public float Height;          // used for hitscan and projectile hits
    public float SightRange;
    public float PainChance;
    public float SoundPitch;

    // Melee attack (all types can claw when the player is close).
    public float MeleeRange;
    public int MeleeMin, MeleeMax;

    // Ranged attack (null kind = melee only).
    public ProjKind? Projectile;
    public float ProjSpeed;
    public int ProjMin, ProjMax;
    public int Volley;            // projectiles per attack, fanned out
    public float KeepDistance;    // ranged enemies stop approaching inside this distance

    public float WindupTime;      // attack frame before the hit/shot
    public float RecoverTime;     // attack frame after it
    public float CooldownMin, CooldownMax;

    public static readonly EnemyDef[] All =
    {
        new()
        {
            Kind = EnemyKind.Fiend, Name = "fiend", Health = 70, Speed = 6.8f, Radius = 0.5f, PixelSize = 0.04f,
            Height = 1.5f, SightRange = 34f, PainChance = 0.6f, SoundPitch = 0.8f,
            MeleeRange = 1.7f, MeleeMin = 6, MeleeMax = 14,
            WindupTime = 0.35f, RecoverTime = 0.3f, CooldownMin = 0.6f, CooldownMax = 1.2f,
        },
        new()
        {
            Kind = EnemyKind.Imp, Name = "imp", Health = 50, Speed = 3.6f, Radius = 0.4f, PixelSize = 0.036f,
            Height = 1.7f, SightRange = 40f, PainChance = 0.7f, SoundPitch = 1.2f,
            MeleeRange = 1.5f, MeleeMin = 5, MeleeMax = 12,
            Projectile = ProjKind.Fireball, ProjSpeed = 11f, ProjMin = 6, ProjMax = 14, Volley = 1, KeepDistance = 6f,
            WindupTime = 0.45f, RecoverTime = 0.35f, CooldownMin = 1.2f, CooldownMax = 2.6f,
        },
        new()
        {
            Kind = EnemyKind.Brute, Name = "brute", Health = 320, Speed = 2.6f, Radius = 0.75f, PixelSize = 0.042f,
            Height = 2.5f, SightRange = 44f, PainChance = 0.15f, SoundPitch = 0.55f,
            MeleeRange = 2.2f, MeleeMin = 15, MeleeMax = 30,
            Projectile = ProjKind.Plasma, ProjSpeed = 14f, ProjMin = 8, ProjMax = 16, Volley = 3, KeepDistance = 4f,
            WindupTime = 0.55f, RecoverTime = 0.4f, CooldownMin = 1.4f, CooldownMax = 2.4f,
        },
    };

    public static EnemyDef For(EnemyKind k) => All[(int)k];

    public static EnemyKind? FromChar(char c) => c switch
    {
        'f' => EnemyKind.Fiend,
        'i' => EnemyKind.Imp,
        'O' => EnemyKind.Brute,
        _ => null,
    };
}

/// <summary>Static data for player weapons.</summary>
public sealed class WeaponDef
{
    public WeaponId Id;
    public string Name;
    public AmmoType Ammo;
    public float Cooldown;
    public int Pellets;
    public float SpreadH, SpreadV;
    public int DamageMin, DamageMax;
    public bool Projectile;
    public Snd Sound;
    public float KickBack;     // HUD recoil offset in pixels
    public int[] FireFrames;   // HUD frame sequence after a shot
    public float FrameTime;

    public static readonly WeaponDef[] All =
    {
        new()
        {
            Id = WeaponId.Pistol, Name = "PISTOL", Ammo = AmmoType.Bullets, Cooldown = 0.32f, Pellets = 1,
            SpreadH = 0.012f, SpreadV = 0.006f, DamageMin = 10, DamageMax = 16, Sound = Snd.Pistol, KickBack = 10f,
            FireFrames = new[] { 1, 2, 0 }, FrameTime = 0.07f,
        },
        new()
        {
            Id = WeaponId.Shotgun, Name = "SHOTGUN", Ammo = AmmoType.Shells, Cooldown = 0.9f, Pellets = 8,
            SpreadH = 0.085f, SpreadV = 0.04f, DamageMin = 6, DamageMax = 11, Sound = Snd.Shotgun, KickBack = 24f,
            FireFrames = new[] { 1, 0, 2, 3, 2, 0 }, FrameTime = 0.11f,
        },
        new()
        {
            Id = WeaponId.Launcher, Name = "ROCKET LAUNCHER", Ammo = AmmoType.Rockets, Cooldown = 0.75f, Pellets = 0,
            DamageMin = 40, DamageMax = 60, Projectile = true, Sound = Snd.RocketFire, KickBack = 30f,
            FireFrames = new[] { 1, 2, 2, 0 }, FrameTime = 0.09f,
        },
    };

    public static WeaponDef For(WeaponId w) => All[(int)w];

    public static readonly int[] MaxAmmo = { 200, 50, 50 };
    public static readonly string[] AmmoNames = { "BULL", "SHEL", "RCKT" };
}

/// <summary>Shared references and level state, reset when the scene is (re)loaded.</summary>
public static class Game
{
    public static Main Main;
    public static Level Level;
    public static Player Player;
    public static EntityManager Entities;
    public static Hud Hud;

    /// <summary>Player takes no damage (used by the scripted stress run).</summary>
    public static bool GodMode;

    /// <summary>Set when the level is complete; enemies stop and the player can't act.</summary>
    public static bool Frozen;

    /// <summary>
    /// Touch controls are in use (set by the first touch, cleared by a real mouse click). Kept
    /// across restarts, like <see cref="Started"/> (the start screen has been dismissed).
    /// </summary>
    public static bool TouchMode;
    public static bool Started;

    /// <summary>Touch input for the player, written by <see cref="TouchControls"/>.</summary>
    public static Vector2 TouchMove;
    public static bool TouchFire;
    public static bool TouchSprint;

    public static int Kills;
    public static int TotalEnemies;
    public static double LevelTime;

    // System.Random rather than RandomNumberGenerator: every Godot call crosses into native
    // code, which is slow on the web interpreter.
    public static System.Random Rng = new();

    public static void Reset()
    {
        Frozen = false;
        TouchMove = Vector2.Zero;
        TouchFire = false;
        TouchSprint = false;
        Kills = 0;
        TotalEnemies = 0;
        LevelTime = 0;
        Rng = new System.Random();
    }

    /// <summary>Random integer in [min, max] (inclusive).</summary>
    public static int RandRange(int min, int max) => Rng.Next(min, max + 1);

    public static float RandF(float min, float max) => min + (float)Rng.NextDouble() * (max - min);
}
