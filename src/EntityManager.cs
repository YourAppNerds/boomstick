using System;
using System.Collections.Generic;
using Godot;

namespace Boomstick;

/// <summary>
/// Updates every enemy, projectile, hit effect and pickup from one _Process call. Entities are
/// plain C# objects; each owns a Sprite3D that the manager only writes to (position, frame,
/// modulate) when something changed. On the web build a script callback costs ~11 us per node,
/// so one manager is far cheaper than one _Process per entity (see logs/step7d1-report.md).
///
/// AI: enemies sleep until they see the player (grid DDA check, then a physics raycast,
/// rate-limited per frame) or hear a shot (path distance in the BFS distance field). Awake
/// enemies walk down the distance field, or straight at the player when they can see him,
/// and attack with claws or projectiles.
///
/// Rendering: by default enemies are drawn batched, one MultiMeshInstance3D per enemy type
/// with a fixed-Y billboard shader; the atlas frame and light level go in INSTANCE_CUSTOM and
/// the whole instance buffer is uploaded with one call per type per frame. That is 3 draw calls
/// for any number of enemies. Projectiles are batched the same way (one MultiMesh per kind).
/// With <see cref="Batched"/> off each enemy and projectile is its own Sprite3D (one draw call
/// each in the compatibility renderer), kept for comparison.
/// </summary>
public partial class EntityManager : Node3D
{
    public enum EState { Idle, Chase, Attack, Pain, Dying, Dead }

    /// <summary>Draw enemies with one MultiMesh per type (true) or one Sprite3D each (false).</summary>
    public static bool Batched = true;

    private sealed class Batch
    {
        public MultiMeshInstance3D Node;
        public MultiMesh Mesh;
        public float[] Buffer = Array.Empty<float>();
        public int Capacity;
        public int Visible = -1;
        public int Count;
    }

    private const int FloatsPerInstance = 16; // 12 transform + 4 custom
    private readonly Batch[] _batches = new Batch[3];

    private readonly Batch[] _projBatches = new Batch[3];

    // Fixed-Y billboard (enemies), same maths as BaseMaterial3D's BILLBOARD_FIXED_Y.
    private const string FixedYBillboard = @"
	MODELVIEW_MATRIX = VIEW_MATRIX * mat4(
		vec4(normalize(cross(vec3(0.0, 1.0, 0.0), INV_VIEW_MATRIX[2].xyz)), 0.0),
		vec4(0.0, 1.0, 0.0, 0.0),
		vec4(normalize(cross(INV_VIEW_MATRIX[0].xyz, vec3(0.0, 1.0, 0.0))), 0.0),
		MODEL_MATRIX[3]);";

    // Full billboard (projectiles), same maths as BaseMaterial3D's BILLBOARD_ENABLED.
    private const string FullBillboard = @"
	MODELVIEW_MATRIX = VIEW_MATRIX * mat4(INV_VIEW_MATRIX[0], INV_VIEW_MATRIX[1], INV_VIEW_MATRIX[2], MODEL_MATRIX[3]);";

    private const string BillboardShader = @"
shader_type spatial;
render_mode unshaded, cull_disabled;
uniform sampler2D atlas : source_color, filter_nearest;
uniform float frames = 9.0;
varying vec3 tint;
void vertex() {
	BILLBOARD
	MODELVIEW_NORMAL_MATRIX = mat3(MODELVIEW_MATRIX);
	// INSTANCE_CUSTOM: x = atlas frame, y = light level, z = green/blue factor (pain flash).
	UV.x = (UV.x + INSTANCE_CUSTOM.x) / frames;
	tint = vec3(INSTANCE_CUSTOM.y, INSTANCE_CUSTOM.y * INSTANCE_CUSTOM.z, INSTANCE_CUSTOM.y * INSTANCE_CUSTOM.z);
}
void fragment() {
	vec4 t = texture(atlas, UV);
	ALBEDO = t.rgb * tint;
	ALPHA = t.a;
	ALPHA_SCISSOR_THRESHOLD = 0.5;
}
";

    private static Shader _fixedYShader, _fullShader;

    private Batch MakeBatch(string name, Texture2D atlas, int frames, Vector2I framePx, float pixelSize, bool fixedY)
    {
        Shader shader = fixedY
            ? _fixedYShader ??= new Shader { Code = BillboardShader.Replace("BILLBOARD", FixedYBillboard) }
            : _fullShader ??= new Shader { Code = BillboardShader.Replace("BILLBOARD", FullBillboard) };
        var mat = new ShaderMaterial { Shader = shader };
        mat.SetShaderParameter("atlas", atlas);
        mat.SetShaderParameter("frames", (float)frames);
        var quad = new QuadMesh
        {
            Size = new Vector2(framePx.X, framePx.Y) * pixelSize,
            // Enemies stand on their origin; projectiles are centred on it.
            CenterOffset = fixedY ? new Vector3(0, framePx.Y * pixelSize / 2f, 0) : Vector3.Zero,
            Material = mat,
        };
        var mm = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseCustomData = true,
            Mesh = quad,
        };
        var node = new MultiMeshInstance3D
        {
            Name = name,
            Multimesh = mm,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            // Instances move every frame; one level-sized box avoids recomputing bounds.
            CustomAabb = new Aabb(new Vector3(-4, -1, -4), new Vector3(_level.Width * Level.Cell + 8, Level.Height + 4, _level.Depth * Level.Cell + 8)),
        };
        AddChild(node);
        return new Batch { Node = node, Mesh = mm };
    }

    private Batch GetBatch(EnemyKind kind)
    {
        int k = (int)kind;
        var def = EnemyDef.For(kind);
        return _batches[k] ??= MakeBatch($"Batch_{def.Name}", Art.Enemy(kind), Art.EnemyFrames,
            Art.EnemyFrameSize(kind), def.PixelSize, true);
    }

    private Batch GetProjBatch(ProjKind kind)
    {
        int k = (int)kind;
        if (_projBatches[k] != null) return _projBatches[k];
        var tex = kind switch { ProjKind.Plasma => Art.Plasma(), ProjKind.Rocket => Art.Rocket(), _ => Art.Fireball() };
        return _projBatches[k] = MakeBatch($"Batch_{kind}", tex, 2, new Vector2I(16, 16), 0.03f, false);
    }

    private static void Write(Batch b, Vector3 pos, float frame, float light, float gb)
    {
        if (b.Count >= b.Capacity)
        {
            b.Capacity = Math.Max(16, b.Capacity * 2);
            b.Mesh.InstanceCount = b.Capacity; // resets the data; the whole buffer is uploaded anyway
            var nb = new float[b.Capacity * FloatsPerInstance];
            Array.Copy(b.Buffer, nb, b.Buffer.Length);
            b.Buffer = nb;
            b.Visible = -1;
        }
        int o = b.Count++ * FloatsPerInstance;
        var buf = b.Buffer;
        buf[o] = 1; buf[o + 1] = 0; buf[o + 2] = 0; buf[o + 3] = pos.X;
        buf[o + 4] = 0; buf[o + 5] = 1; buf[o + 6] = 0; buf[o + 7] = pos.Y;
        buf[o + 8] = 0; buf[o + 9] = 0; buf[o + 10] = 1; buf[o + 11] = pos.Z;
        buf[o + 12] = frame;
        buf[o + 13] = light;
        buf[o + 14] = gb;
        buf[o + 15] = 0;
    }

    private static void Upload(Batch[] batches)
    {
        foreach (var b in batches)
        {
            if (b == null) continue;
            // Skip the upload when nothing is or was visible (e.g. no projectiles in flight).
            if (b.Capacity > 0 && (b.Count > 0 || b.Visible != 0)) b.Mesh.Buffer = b.Buffer;
            if (b.Count != b.Visible)
            {
                b.Visible = b.Count;
                b.Mesh.VisibleInstanceCount = b.Count;
            }
        }
    }

    /// <summary>
    /// Writes every enemy and projectile into its type's instance buffer and uploads each buffer
    /// with one call (MultiMesh.Buffer).
    /// </summary>
    private void FlushBatches()
    {
        foreach (var b in _batches)
            if (b != null) b.Count = 0;
        foreach (var e in _enemies)
            Write(_batches[(int)e.Def.Kind], e.Pos, e.Frame, e.Light, e.Flash > 0 ? 0.8f : 1f);
        Upload(_batches);

        foreach (var b in _projBatches)
            if (b != null) b.Count = 0;
        if (_projActive > 0)
            foreach (var p in _proj)
                if (p.Active) Write(_projBatches[(int)p.Kind], p.Pos, p.Frame, 1f, 1f);
        Upload(_projBatches);
    }

    public sealed class Enemy
    {
        public EnemyDef Def;
        public Sprite3D Node;
        public Vector3 Pos;
        public int Health;
        public EState State;
        public float StateTime;
        public float Cooldown;
        public float LosTimer;
        public bool HasLos;
        public bool AttackDone;
        public int Frame = -1;
        public Vector2I Cell = new(-1, -1);
        public float Light = -1;
        public float Flash;
        public float WalkTime;
        public bool Counted;      // counts toward the level kill total
        public bool Wander;       // stress "wander" mode: dummy-like logic, no AI
        public Vector3 WanderDir;
        public float WanderTimer;
        public float StrafeDir = 1f;
        public int Next = -1;     // spatial bucket linked list
    }

    private sealed class Projectile
    {
        public ProjKind Kind;
        public Sprite3D Node;
        public Vector3 Pos, Vel;
        public int Damage;
        public bool FromPlayer;
        public float Life;
        public bool Active;
        public int Frame;
    }

    private sealed class Effect
    {
        public Sprite3D Node;
        public float Time, Duration;
        public bool Active;
        public int Frames;
        public int Frame;
    }

    private sealed class Pickup
    {
        public PickupKind Kind;
        public Sprite3D Node;
        public Vector3 Pos;
        public bool Taken;
    }

    private const int MaxProjectiles = 192;
    private const int MaxEffects = 64;
    private const int RaysPerFrame = 24;
    private const float EyeHeight = 1.3f;

    private readonly List<Enemy> _enemies = new();
    private readonly Projectile[] _proj = new Projectile[MaxProjectiles];
    private readonly Effect[] _fx = new Effect[MaxEffects];
    private readonly List<Pickup> _pickups = new();
    private int _projActive;
    private int _fxNext;

    private Level _level;
    private int[] _dist, _queue, _bucket;
    private Vector2I _fieldCell = new(-1000, -1000);
    private float _fieldAge;

    private PhysicsRayQueryParameters3D _ray;
    private PhysicsDirectSpaceState3D _space; // fetched once per frame, only if a ray is cast
    private OmniLight3D _blastLight;
    private float _blastTime;
    private float _statTimer;
    private int _raysThisFrame;
    private int _raysTotal;

    public int EnemyCount => _enemies.Count;
    public int AliveCount { get; private set; }
    public int ProjectileCount => _projActive;

    public override void _Ready()
    {
        _level = Game.Level;
        int cells = _level.Width * _level.Depth;
        _dist = new int[cells];
        _queue = new int[cells];
        _bucket = new int[cells];
        _ray = new PhysicsRayQueryParameters3D { CollisionMask = 1 };
        _blastLight = new OmniLight3D
        {
            LightColor = new Color(1f, 0.6f, 0.25f), OmniRange = 9f, LightEnergy = 0f, ShadowEnabled = false, Visible = false,
        };
        AddChild(_blastLight);

        for (int i = 0; i < MaxProjectiles; i++)
            _proj[i] = new Projectile();
        for (int i = 0; i < MaxEffects; i++)
        {
            var node = MakeSprite(Art.Puff(), 4, 0.04f, BaseMaterial3D.BillboardModeEnum.Enabled);
            node.Visible = false;
            AddChild(node);
            _fx[i] = new Effect { Node = node };
        }
    }

    private static Sprite3D MakeSprite(Texture2D tex, int frames, float pixelSize, BaseMaterial3D.BillboardModeEnum billboard)
    {
        return new Sprite3D
        {
            Texture = tex,
            Hframes = frames,
            Billboard = billboard,
            TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest,
            AlphaCut = SpriteBase3D.AlphaCutMode.Discard,
            Shaded = false,
            PixelSize = pixelSize,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
    }

    // ---- Spawning -----------------------------------------------------------------------------

    public Enemy SpawnEnemy(EnemyKind kind, Vector3 pos, bool counted, bool awake = false, bool wander = false)
    {
        var def = EnemyDef.For(kind);
        Sprite3D node = null;
        if (Batched)
            GetBatch(kind);
        else
        {
            var fs = Art.EnemyFrameSize(kind);
            node = MakeSprite(Art.Enemy(kind), Art.EnemyFrames, def.PixelSize, BaseMaterial3D.BillboardModeEnum.FixedY);
            node.Offset = new Vector2(0, fs.Y / 2f);
            node.Position = pos;
            AddChild(node);
        }
        var e = new Enemy
        {
            Def = def, Node = node, Pos = pos, Health = def.Health, State = awake ? EState.Chase : EState.Idle,
            Counted = counted, Wander = wander, LosTimer = Game.RandF(0f, 0.3f),
            StrafeDir = Game.Rng.Next(2) == 0 ? -1f : 1f,
        };
        if (wander) PickWander(e);
        _enemies.Add(e);
        if (counted) Game.TotalEnemies++;
        SetFrame(e, Art.FIdle);
        UpdateLight(e);
        return e;
    }

    public void SpawnPickup(PickupKind kind, Vector3 pos)
    {
        var node = MakeSprite(Art.Pickup(kind), 1, 0.03f, BaseMaterial3D.BillboardModeEnum.FixedY);
        node.Offset = new Vector2(0, 12);
        node.Position = pos;
        float l = _level.LightAt(Level.CellOf(pos));
        node.Modulate = new Color(l, l, l);
        AddChild(node);
        _pickups.Add(new Pickup { Kind = kind, Node = node, Pos = pos });
    }

    /// <summary>Removes every enemy and in-flight projectile (stress test reset).</summary>
    public void ClearEnemies(bool onlyUncounted)
    {
        for (int i = _enemies.Count - 1; i >= 0; i--)
        {
            var e = _enemies[i];
            if (onlyUncounted && e.Counted) continue;
            e.Node?.QueueFree();
            _enemies.RemoveAt(i);
            if (e.Counted) Game.TotalEnemies--;
        }
        foreach (var p in _proj)
            if (p.Active) KillProjectile(p);
    }

    // ---- Main update --------------------------------------------------------------------------

    public override void _Process(double delta)
    {
        long t0 = Perf.Now;
        float dt = Mathf.Min((float)delta, 0.05f);
        var player = Game.Player;
        _raysThisFrame = 0;
        _space = null;

        if (!Game.Frozen && player != null)
        {
            UpdateField(player, dt);
            BuildBuckets();
            int alive = 0;
            var pPos = player.GlobalPosition;
            for (int i = 0; i < _enemies.Count; i++)
            {
                var e = _enemies[i];
                UpdateEnemy(e, player, pPos, dt);
                if (e.State is not (EState.Dying or EState.Dead)) alive++;
            }
            AliveCount = alive;
            UpdateProjectiles(player, dt);
            if (Batched) FlushBatches();
            UpdatePickups(player);
        }
        UpdateEffects(dt);

        _statTimer -= dt;
        if (_statTimer <= 0)
        {
            _statTimer = 0.25f;
            DebugOverlay.SetStat("entities", $"{_enemies.Count} enemies ({AliveCount} alive), {_projActive} projectiles, {_raysTotal} LOS rays");
            _raysTotal = 0;
        }
        Perf.AddProcess(t0);
    }

    private void UpdateField(Player player, float dt)
    {
        _fieldAge += dt;
        var cell = Level.CellOf(player.GlobalPosition);
        if (cell != _fieldCell && _fieldAge > 0.15f)
        {
            _level.DistanceField(cell, _dist, _queue);
            _fieldCell = cell;
            _fieldAge = 0;
        }
    }

    /// <summary>Path distance (in cells) from the player to a cell, int.MaxValue if unreachable.</summary>
    public int PathDistance(Vector2I c) =>
        c.X < 0 || c.Y < 0 || c.X >= _level.Width || c.Y >= _level.Depth ? int.MaxValue : _dist[c.Y * _level.Width + c.X];

    private void BuildBuckets()
    {
        Array.Fill(_bucket, -1);
        for (int i = 0; i < _enemies.Count; i++)
        {
            var e = _enemies[i];
            e.Next = -1;
            if (e.State is EState.Dying or EState.Dead) continue;
            var c = Level.CellOf(e.Pos);
            if (_level.IsSolidCell(c.X, c.Y)) continue;
            int idx = c.Y * _level.Width + c.X;
            e.Next = _bucket[idx];
            _bucket[idx] = i;
        }
    }

    private void SetFrame(Enemy e, int frame)
    {
        if (e.Frame == frame) return;
        e.Frame = frame;
        if (e.Node != null) e.Node.Frame = frame;
    }

    private void UpdateLight(Enemy e)
    {
        var c = Level.CellOf(e.Pos);
        if (c == e.Cell && e.Flash <= 0) return;
        e.Cell = c;
        float l = _level.LightAt(c);
        if (e.Flash > 0) l = Mathf.Min(1.4f, l + 0.6f);
        if (Mathf.Abs(l - e.Light) < 0.01f) return;
        e.Light = l;
        if (e.Node != null) e.Node.Modulate = new Color(l, l * (e.Flash > 0 ? 0.8f : 1f), l * (e.Flash > 0 ? 0.8f : 1f));
    }

    /// <summary>
    /// Line of sight from an enemy to the player: grid DDA first, then a physics raycast against
    /// the wall collision. Limited to a few rays per frame; results are cached per enemy.
    /// </summary>
    private bool CheckLos(Enemy e, Vector3 pPos)
    {
        var from = e.Pos + new Vector3(0, EyeHeight, 0);
        var to = pPos + new Vector3(0, Player.EyeHeight - 0.1f, 0);
        if (from.DistanceSquaredTo(to) > e.Def.SightRange * e.Def.SightRange) return false;
        if (!_level.GridLineClear(from, to)) return false;
        _raysThisFrame++;
        _raysTotal++;
        _ray.From = from;
        _ray.To = to;
        _space ??= GetWorld3D().DirectSpaceState;
        var hit = _space.IntersectRay(_ray);
        return hit.Count == 0;
    }

    private void Wake(Enemy e, bool sound)
    {
        if (e.State != EState.Idle) return;
        e.State = EState.Chase;
        e.Cooldown = Game.RandF(0.3f, 1.0f);
        if (sound) Sfx.PlayAt(Snd.Alert, e.Pos, e.Def.SoundPitch * Game.RandF(0.9f, 1.1f));
    }

    /// <summary>The player made a loud noise: wake idle enemies within a path distance.</summary>
    public void Noise(float cells)
    {
        int woke = 0;
        foreach (var e in _enemies)
        {
            if (e.State != EState.Idle) continue;
            if (PathDistance(Level.CellOf(e.Pos)) <= cells)
                Wake(e, woke++ < 2);
        }
    }

    private void UpdateEnemy(Enemy e, Player player, Vector3 pPos, float dt)
    {
        e.StateTime += dt;
        if (e.Flash > 0)
        {
            e.Flash -= dt;
            if (e.Flash <= 0) e.Light = -1; // force a modulate refresh
        }

        switch (e.State)
        {
            case EState.Dead:
                return;
            case EState.Dying:
            {
                int f = e.StateTime < 0.12f ? Art.FDie1 : e.StateTime < 0.26f ? Art.FDie2 : Art.FCorpse;
                SetFrame(e, f);
                UpdateLight(e);
                if (f == Art.FCorpse) e.State = EState.Dead;
                return;
            }
        }

        if (e.Wander)
        {
            UpdateWander(e, dt);
            return;
        }

        var toP = new Vector3(pPos.X - e.Pos.X, 0, pPos.Z - e.Pos.Z);
        float dist = toP.Length();
        e.Cooldown -= dt;

        // Line of sight, refreshed a few times per second within the per-frame ray budget.
        e.LosTimer -= dt;
        if (e.LosTimer <= 0 && _raysThisFrame < RaysPerFrame)
        {
            e.LosTimer = Game.RandF(0.2f, 0.35f);
            e.HasLos = !player.Dead && CheckLos(e, pPos);
        }

        switch (e.State)
        {
            case EState.Idle:
                SetFrame(e, Art.FIdle);
                if (e.HasLos) Wake(e, true);
                break;

            case EState.Pain:
                SetFrame(e, Art.FPain);
                if (e.StateTime > 0.22f) { e.State = EState.Chase; e.StateTime = 0; }
                break;

            case EState.Attack:
                UpdateAttack(e, player, pPos, dist);
                break;

            case EState.Chase:
            {
                bool canMelee = dist < e.Def.MeleeRange + 0.4f && e.HasLos;
                bool canShoot = e.Def.Projectile.HasValue && e.HasLos && dist < e.Def.SightRange && dist > 2.5f;
                if (!player.Dead && e.Cooldown <= 0 && (canMelee || canShoot))
                {
                    e.State = EState.Attack;
                    e.StateTime = 0;
                    e.AttackDone = false;
                    if (canMelee) Sfx.PlayAt(Snd.Swipe, e.Pos, 0.9f);
                    break;
                }
                Move(e, toP, dist, dt);
                e.WalkTime += dt * e.Def.Speed;
                SetFrame(e, (int)(e.WalkTime / 1.4f) % 2 == 0 ? Art.FWalkA : Art.FWalkB);
                break;
            }
        }

        if (e.Node != null) e.Node.Position = e.Pos;
        UpdateLight(e);
    }

    private void UpdateAttack(Enemy e, Player player, Vector3 pPos, float dist)
    {
        var def = e.Def;
        if (!e.AttackDone)
        {
            SetFrame(e, Art.FAttackWind);
            if (e.StateTime < def.WindupTime) return;
            e.AttackDone = true;
            SetFrame(e, Art.FAttackFire);
            if (dist < def.MeleeRange + 0.6f)
            {
                if (e.HasLos && !player.Dead)
                    player.TakeDamage(Game.RandRange(def.MeleeMin, def.MeleeMax), e.Pos);
            }
            else if (def.Projectile.HasValue)
            {
                var from = e.Pos + new Vector3(0, def.Height * 0.6f, 0);
                var target = pPos + new Vector3(0, 1.1f, 0);
                var dir = (target - from).Normalized();
                int n = def.Volley;
                for (int k = 0; k < n; k++)
                {
                    float yaw = n > 1 ? (k - (n - 1) * 0.5f) * 0.12f : 0f;
                    var d = dir.Rotated(Vector3.Up, yaw);
                    SpawnProjectile(def.Projectile.Value, from + d * (def.Radius + 0.2f), d * def.ProjSpeed,
                        Game.RandRange(def.ProjMin, def.ProjMax), false);
                }
                Sfx.PlayAt(def.Projectile == ProjKind.Plasma ? Snd.Plasma : Snd.Fireball, e.Pos);
            }
            return;
        }
        if (e.StateTime >= def.WindupTime + def.RecoverTime)
        {
            e.State = EState.Chase;
            e.StateTime = 0;
            e.Cooldown = Game.RandF(def.CooldownMin, def.CooldownMax);
        }
    }

    private void Move(Enemy e, Vector3 toP, float dist, float dt)
    {
        var def = e.Def;
        Vector3 dir;
        if (e.HasLos && dist < 30f)
        {
            dir = toP / Mathf.Max(dist, 0.001f);
            if (def.Projectile.HasValue && dist < def.KeepDistance)
                dir = new Vector3(-dir.Z, 0, dir.X) * e.StrafeDir * 0.6f; // circle-strafe instead of closing in
            else if (dist < def.MeleeRange * 0.8f)
                dir = Vector3.Zero;
        }
        else
        {
            dir = FieldDirection(e);
        }

        // Separation from nearby enemies (same and neighbouring cells).
        var c = Level.CellOf(e.Pos);
        Vector3 push = Vector3.Zero;
        int checks = 0;
        for (int dz = -1; dz <= 1 && checks < 10; dz++)
        for (int dx = -1; dx <= 1 && checks < 10; dx++)
        {
            int cx = c.X + dx, cz = c.Y + dz;
            if (cx < 0 || cz < 0 || cx >= _level.Width || cz >= _level.Depth) continue;
            for (int j = _bucket[cz * _level.Width + cx]; j >= 0 && checks < 10; j = _enemies[j].Next)
            {
                var o = _enemies[j];
                if (o == e) continue;
                checks++;
                float ox = e.Pos.X - o.Pos.X, oz = e.Pos.Z - o.Pos.Z;
                float min = def.Radius + o.Def.Radius;
                float d2 = ox * ox + oz * oz;
                if (d2 < min * min && d2 > 0.0001f)
                {
                    float d = Mathf.Sqrt(d2);
                    push += new Vector3(ox / d, 0, oz / d) * (min - d);
                }
            }
        }

        var step = dir * (def.Speed * dt) + push * Mathf.Min(1f, dt * 8f);
        // Keep out of the player's body.
        if (dist < def.Radius + 0.4f && dist > 0.001f)
            step -= toP / dist * (def.Radius + 0.4f - dist);
        TryMove(e, step, def.Radius);
    }

    private void TryMove(Enemy e, Vector3 step, float r)
    {
        var p = e.Pos;
        var nx = new Vector3(p.X + step.X, 0, p.Z);
        if (_level.CanOccupy(nx, r)) p = nx;
        var nz = new Vector3(p.X, 0, p.Z + step.Z);
        if (_level.CanOccupy(nz, r)) p = nz;
        e.Pos = p;
    }

    /// <summary>Unit direction towards the neighbouring cell closest to the player.</summary>
    private Vector3 FieldDirection(Enemy e)
    {
        var c = Level.CellOf(e.Pos);
        int best = PathDistance(c);
        if (best == int.MaxValue) return Vector3.Zero;
        Vector2I target = c;
        for (int dz = -1; dz <= 1; dz++)
        for (int dx = -1; dx <= 1; dx++)
        {
            if (dx == 0 && dz == 0) continue;
            var n = new Vector2I(c.X + dx, c.Y + dz);
            if (dx != 0 && dz != 0 && (_level.IsSolidCell(c.X + dx, c.Y) || _level.IsSolidCell(c.X, c.Y + dz))) continue;
            int d = PathDistance(n);
            if (d < best) { best = d; target = n; }
        }
        var to = Level.CellCenter(target) - new Vector3(e.Pos.X, 0, e.Pos.Z);
        float len = to.Length();
        return len > 0.01f ? to / len : Vector3.Zero;
    }

    private static void PickWander(Enemy e)
    {
        float a = Game.RandF(0, Mathf.Tau);
        e.WanderDir = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a));
        e.WanderTimer = Game.RandF(1f, 4f);
    }

    /// <summary>Stress "wander" mode: the same logic as the per-node Dummy, run from the manager.</summary>
    private void UpdateWander(Enemy e, float dt)
    {
        e.WanderTimer -= dt;
        if (e.WanderTimer <= 0) PickWander(e);
        var next = e.Pos + e.WanderDir * (e.Def.Speed * 0.6f * dt);
        if (_level.IsSolidAt(next + e.WanderDir * 0.5f)) PickWander(e);
        else e.Pos = next;
        e.WalkTime += dt * e.Def.Speed;
        SetFrame(e, (int)(e.WalkTime / 1.4f) % 2 == 0 ? Art.FWalkA : Art.FWalkB);
        if (e.Node != null) e.Node.Position = e.Pos;
        UpdateLight(e);
    }

    // ---- Damage -------------------------------------------------------------------------------

    /// <summary>Damages an enemy. Returns true if it died from this hit.</summary>
    public bool DamageEnemy(Enemy e, int amount)
    {
        if (e.State is EState.Dying or EState.Dead) return false;
        e.Health -= amount;
        e.Flash = 0.08f;
        e.Light = -1;
        if (e.Health <= 0)
        {
            e.State = EState.Dying;
            e.StateTime = 0;
            e.Flash = 0;
            if (e.Counted) Game.Kills++;
            Sfx.PlayAt(Snd.Death, e.Pos, e.Def.SoundPitch * Game.RandF(0.9f, 1.1f));
            return true;
        }
        if (e.State == EState.Idle) Wake(e, false);
        if (Game.Rng.NextDouble() < e.Def.PainChance && e.State != EState.Attack)
        {
            e.State = EState.Pain;
            e.StateTime = 0;
            Sfx.PlayAt(Snd.Pain, e.Pos, e.Def.SoundPitch * Game.RandF(0.9f, 1.15f));
        }
        return false;
    }

    /// <summary>
    /// Finds the first living enemy hit by a ray (vertical cylinder test), closer than maxDist.
    /// </summary>
    public Enemy RayEnemies(Vector3 origin, Vector3 dir, float maxDist, out float hitT)
    {
        Enemy best = null;
        hitT = maxDist;
        float dl = Mathf.Sqrt(dir.X * dir.X + dir.Z * dir.Z);
        if (dl < 1e-5f) return null;
        foreach (var e in _enemies)
        {
            if (e.State is EState.Dying or EState.Dead) continue;
            // Closest approach in the XZ plane, then intersect with the circle.
            float ox = e.Pos.X - origin.X, oz = e.Pos.Z - origin.Z;
            float ux = dir.X / dl, uz = dir.Z / dl;
            float along = ox * ux + oz * uz;
            if (along < 0) continue;
            float perp2 = ox * ox + oz * oz - along * along;
            float r = e.Def.Radius;
            if (perp2 > r * r) continue;
            float tXZ = along - Mathf.Sqrt(r * r - perp2);
            float t = Mathf.Max(0, tXZ) / dl; // convert to distance along the full 3D direction
            if (t >= hitT) continue;
            float y = origin.Y + dir.Y * t;
            if (y < e.Pos.Y || y > e.Pos.Y + e.Def.Height) continue;
            hitT = t;
            best = e;
        }
        return best;
    }

    /// <summary>Damages enemies and the player around a point (rocket explosion).</summary>
    public void Explode(Vector3 at, float radius, int damage, bool fromPlayer)
    {
        SpawnEffect(Art.Explosion(), at, 0.05f, 0.4f);
        Sfx.PlayAt(Snd.Explosion, at);
        _blastLight.Position = at;
        _blastLight.Visible = true;
        _blastTime = 0.35f;
        foreach (var e in _enemies)
        {
            if (e.State is EState.Dying or EState.Dead) continue;
            var c = e.Pos + new Vector3(0, e.Def.Height * 0.5f, 0);
            float d = c.DistanceTo(at) - e.Def.Radius;
            if (d >= radius) continue;
            if (!_level.GridLineClear(at, c)) continue;
            DamageEnemy(e, (int)(damage * (1f - Mathf.Max(0, d) / radius)));
        }
        var player = Game.Player;
        if (player != null && !player.Dead)
        {
            var pc = player.GlobalPosition + new Vector3(0, 0.9f, 0);
            float d = pc.DistanceTo(at) - 0.35f;
            if (d < radius && _level.GridLineClear(at, pc))
            {
                float k = 1f - Mathf.Max(0, d) / radius;
                player.TakeDamage((int)(damage * k * (fromPlayer ? 0.5f : 1f)), at);
                player.Knockback((pc - at).Normalized() * 9f * k);
            }
        }
    }

    // ---- Projectiles --------------------------------------------------------------------------

    public void SpawnProjectile(ProjKind kind, Vector3 pos, Vector3 vel, int damage, bool fromPlayer)
    {
        Projectile p = null;
        foreach (var q in _proj)
            if (!q.Active) { p = q; break; }
        if (p == null) return; // pool exhausted (only happens in big stress runs)
        if (Batched)
            GetProjBatch(kind);
        else if (p.Node == null)
        {
            p.Node = MakeSprite(Art.Fireball(), 2, 0.03f, BaseMaterial3D.BillboardModeEnum.Enabled);
            AddChild(p.Node);
        }
        if (!Batched && (p.Kind != kind || !p.Node.Visible))
        {
            p.Node.Texture = kind switch
            {
                ProjKind.Plasma => Art.Plasma(),
                ProjKind.Rocket => Art.Rocket(),
                _ => Art.Fireball(),
            };
        }
        p.Kind = kind;
        p.Pos = pos;
        p.Vel = vel;
        p.Damage = damage;
        p.FromPlayer = fromPlayer;
        p.Life = 6f;
        p.Active = true;
        if (p.Node != null)
        {
            p.Node.Position = pos;
            p.Node.Visible = true;
        }
        _projActive++;
    }

    private void KillProjectile(Projectile p)
    {
        p.Active = false;
        if (p.Node != null) p.Node.Visible = false;
        _projActive--;
    }

    private void UpdateProjectiles(Player player, float dt)
    {
        if (_projActive == 0) return;
        var pPos = player.GlobalPosition;
        foreach (var p in _proj)
        {
            if (!p.Active) continue;
            p.Life -= dt;
            var next = p.Pos + p.Vel * dt;
            bool hitWall = p.Life <= 0 || _level.IsSolidAt(next) || next.Y < 0.05f || next.Y > Level.Height - 0.05f;
            Enemy hitEnemy = null;
            bool hitPlayer = false;
            if (p.FromPlayer)
            {
                float len = (next - p.Pos).Length();
                hitEnemy = RayEnemies(p.Pos, (next - p.Pos) / Mathf.Max(len, 1e-5f), len + 0.2f, out _);
            }
            else if (!player.Dead)
            {
                float dx = next.X - pPos.X, dz = next.Z - pPos.Z;
                hitPlayer = dx * dx + dz * dz < 0.45f * 0.45f && next.Y > pPos.Y && next.Y < pPos.Y + 1.8f;
            }

            if (hitWall || hitEnemy != null || hitPlayer)
            {
                var at = hitWall ? p.Pos : next;
                if (p.Kind == ProjKind.Rocket)
                {
                    if (hitEnemy != null) DamageEnemy(hitEnemy, p.Damage);
                    Explode(at, 3.5f, 110, p.FromPlayer);
                }
                else
                {
                    if (hitPlayer) player.TakeDamage(p.Damage, p.Pos);
                    if (hitEnemy != null) DamageEnemy(hitEnemy, p.Damage);
                    SpawnEffect(p.Kind == ProjKind.Plasma ? Art.Plasma() : Art.Explosion(), at, 0.025f, 0.25f, p.Kind == ProjKind.Plasma ? 2 : 4);
                    Sfx.PlayAt(Snd.ProjHit, at, p.Kind == ProjKind.Plasma ? 1.5f : 1f, -4f);
                }
                KillProjectile(p);
                continue;
            }

            p.Pos = next;
            int frame = (int)(p.Life * 12f) & 1;
            if (p.Node != null)
            {
                p.Node.Position = next;
                if (frame != p.Frame) p.Node.Frame = frame;
            }
            p.Frame = frame;
        }
    }

    // ---- Effects ------------------------------------------------------------------------------

    public void SpawnEffect(Texture2D atlas, Vector3 at, float pixelSize, float duration, int frames = 4)
    {
        var fx = _fx[_fxNext];
        _fxNext = (_fxNext + 1) % MaxEffects;
        fx.Node.Texture = atlas;
        fx.Node.Hframes = frames;
        fx.Node.PixelSize = pixelSize;
        fx.Node.Frame = 0;
        fx.Node.Position = at;
        fx.Node.Visible = true;
        fx.Frame = 0;
        fx.Frames = frames;
        fx.Time = 0;
        fx.Duration = duration;
        fx.Active = true;
    }

    private void UpdateEffects(float dt)
    {
        foreach (var fx in _fx)
        {
            if (!fx.Active) continue;
            fx.Time += dt;
            if (fx.Time >= fx.Duration)
            {
                fx.Active = false;
                fx.Node.Visible = false;
                continue;
            }
            int f = Mathf.Min(fx.Frames - 1, (int)(fx.Time / fx.Duration * fx.Frames));
            if (f != fx.Frame)
            {
                fx.Frame = f;
                fx.Node.Frame = f;
            }
        }
        if (_blastTime > 0)
        {
            _blastTime -= dt;
            _blastLight.LightEnergy = Mathf.Max(0, _blastTime / 0.35f) * 4f;
            if (_blastTime <= 0) _blastLight.Visible = false;
        }
    }

    // ---- Pickups ------------------------------------------------------------------------------

    private void UpdatePickups(Player player)
    {
        if (player.Dead) return;
        var pp = player.GlobalPosition;
        foreach (var k in _pickups)
        {
            if (k.Taken) continue;
            float dx = k.Pos.X - pp.X, dz = k.Pos.Z - pp.Z;
            if (dx * dx + dz * dz > 1.0f) continue;
            if (!player.TryPickup(k.Kind)) continue;
            k.Taken = true;
            k.Node.Visible = false;
        }
    }
}
