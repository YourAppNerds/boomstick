using Godot;

namespace Boomstick;

/// <summary>
/// The player's arsenal: owned weapons, ammo, firing (hitscan pellets or rockets), switching and
/// the HUD animation state (frame, recoil, raise/lower). A plain C# object updated from
/// <see cref="Player._Process"/>, so it adds no script callbacks of its own.
/// </summary>
public sealed class Weapons
{
    public readonly bool[] Owned = { true, false, false };
    public readonly int[] Ammo = { 50, 0, 0 };

    public WeaponId Current { get; private set; } = WeaponId.Pistol;
    private WeaponId _pending = WeaponId.Pistol;

    /// <summary>0 = weapon fully raised, 1 = fully lowered (switching).</summary>
    public float Lower { get; private set; }
    public int Frame { get; private set; }
    public float Kick { get; private set; }
    public float FlashTime { get; private set; }

    private float _cooldown;
    private int _seqIndex = -1;
    private float _seqTimer;
    private bool _wasFiring;

    private readonly Player _player;
    private readonly PhysicsRayQueryParameters3D _ray = new() { CollisionMask = 1 };

    public Weapons(Player player) => _player = player;

    public WeaponDef Def => WeaponDef.For(Current);

    public int CurrentAmmo => Ammo[(int)Def.Ammo];

    public void Select(WeaponId w)
    {
        if (!Owned[(int)w] || w == _pending) return;
        _pending = w;
    }

    public void Cycle(int dir)
    {
        int i = (int)_pending;
        for (int k = 0; k < 3; k++)
        {
            i = (i + dir + 3) % 3;
            if (Owned[i]) { Select((WeaponId)i); return; }
        }
    }

    /// <summary>Gives a weapon; switches to it if it is new. Returns true if anything changed.</summary>
    public bool GiveWeapon(WeaponId w, int ammo)
    {
        bool isNew = !Owned[(int)w];
        Owned[(int)w] = true;
        bool gotAmmo = GiveAmmo(WeaponDef.For(w).Ammo, ammo);
        if (isNew) Select(w);
        return isNew || gotAmmo;
    }

    public bool GiveAmmo(AmmoType t, int amount)
    {
        int i = (int)t;
        if (Ammo[i] >= WeaponDef.MaxAmmo[i]) return false;
        Ammo[i] = Mathf.Min(WeaponDef.MaxAmmo[i], Ammo[i] + amount);
        return true;
    }

    public void Update(float dt, bool fireHeld)
    {
        _cooldown -= dt;
        Kick = Mathf.MoveToward(Kick, 0, dt * 120f);
        FlashTime -= dt;

        // Lower the current weapon, swap, raise the new one.
        if (_pending != Current)
        {
            Lower = Mathf.MoveToward(Lower, 1f, dt * 6f);
            if (Lower >= 1f)
            {
                Current = _pending;
                Frame = 0;
                _seqIndex = -1;
            }
        }
        else
            Lower = Mathf.MoveToward(Lower, 0f, dt * 6f);

        // Advance the firing frame sequence.
        if (_seqIndex >= 0)
        {
            _seqTimer -= dt;
            if (_seqTimer <= 0)
            {
                _seqIndex++;
                var seq = Def.FireFrames;
                if (_seqIndex >= seq.Length) { _seqIndex = -1; Frame = 0; }
                else
                {
                    Frame = seq[_seqIndex];
                    _seqTimer = Def.FrameTime;
                    if (Current == WeaponId.Shotgun && _seqIndex == 2) Sfx.Play(Snd.Pump, -4f);
                }
            }
        }

        if (fireHeld && _cooldown <= 0 && Lower < 0.05f && _pending == Current)
        {
            if (CurrentAmmo <= 0)
            {
                if (!_wasFiring) Sfx.Play(Snd.Click);
                _cooldown = 0.3f;
                AutoSwitch();
            }
            else
                Fire();
        }
        _wasFiring = fireHeld;
    }

    private void AutoSwitch()
    {
        for (int i = 2; i >= 0; i--)
            if (Owned[i] && Ammo[(int)WeaponDef.For((WeaponId)i).Ammo] > 0) { Select((WeaponId)i); return; }
    }

    private void Fire()
    {
        var def = Def;
        Ammo[(int)def.Ammo]--;
        _cooldown = def.Cooldown;
        Kick = def.KickBack;
        FlashTime = 0.06f;
        _seqIndex = 0;
        Frame = def.FireFrames[0];
        _seqTimer = def.FrameTime;
        Sfx.Play(def.Sound, 0f, Game.RandF(0.95f, 1.05f));
        _player.OnWeaponFired();

        var cam = _player.Camera;
        var origin = cam.GlobalPosition;
        var basis = cam.GlobalBasis;
        var forward = -basis.Z;
        var ents = Game.Entities;

        if (def.Projectile)
        {
            var start = origin + forward * 0.6f - basis.Y * 0.2f;
            ents.SpawnProjectile(ProjKind.Rocket, start, forward * 24f, Game.RandRange(def.DamageMin, def.DamageMax), true);
            return;
        }

        var space = _player.GetWorld3D().DirectSpaceState;
        for (int i = 0; i < def.Pellets; i++)
        {
            var dir = (forward + basis.X * Game.RandF(-def.SpreadH, def.SpreadH) + basis.Y * Game.RandF(-def.SpreadV, def.SpreadV)).Normalized();
            const float range = 100f;
            _ray.From = origin;
            _ray.To = origin + dir * range;
            var hit = space.IntersectRay(_ray);
            float wallT = range;
            Vector3 wallPoint = default, wallNormal = default;
            if (hit.Count > 0)
            {
                wallPoint = hit["position"].AsVector3();
                wallNormal = hit["normal"].AsVector3();
                wallT = origin.DistanceTo(wallPoint);
            }
            var enemy = ents.RayEnemies(origin, dir, wallT, out float t);
            if (enemy != null)
            {
                ents.DamageEnemy(enemy, Game.RandRange(def.DamageMin, def.DamageMax));
                ents.SpawnEffect(Art.BloodFx(), origin + dir * (t - 0.05f), 0.03f, 0.25f);
            }
            else if (hit.Count > 0)
                ents.SpawnEffect(Art.Puff(), wallPoint + wallNormal * 0.05f, 0.025f, 0.25f);
        }
    }
}
