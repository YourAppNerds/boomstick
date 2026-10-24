using Godot;

namespace Boomstick;

/// <summary>
/// First-person player: WASD + mouse look, sprint, jump, subtle head bob, health and armor,
/// weapons (<see cref="Weapons"/>), pickups, death and reaching the exit.
/// Click captures the mouse (on web this runs inside the browser's click handler because the
/// engine flushes input events during mouse button callbacks, so pointer lock is allowed).
/// Esc releases it.
/// </summary>
public partial class Player : CharacterBody3D
{
    [Export] public float WalkSpeed = 6f;
    [Export] public float SprintSpeed = 10f;
    [Export] public float GroundAccel = 60f;
    [Export] public float AirAccel = 10f;
    [Export] public float JumpVelocity = 4.5f;
    [Export] public float MouseSensitivity = 0.0022f;
    [Export] public float BobAmount = 0.035f;

    public const float EyeHeight = 1.55f;

    public int Health { get; private set; } = 100;
    public int Armor { get; private set; }
    /// <summary>Fraction of damage absorbed by armor: 1/3 for green, 1/2 for mega armor.</summary>
    public float ArmorAbsorb { get; private set; } = 1f / 3f;
    public bool Dead { get; private set; }
    public Weapons Weapons { get; private set; }

    /// <summary>Test hook: hold the trigger without input (see Main.ApplyDevArgs).</summary>
    public bool AutoFire;

    private Node3D _head;
    private Camera3D _camera;
    private OmniLight3D _muzzle;
    private float _pitch;
    private float _bobPhase;
    private float _gravity;
    private float _deathTime;
    private ulong _capturedAt;
    private Vector3 _knock;

    public Camera3D Camera => _camera;

    /// <summary>Bob phase and strength for the HUD weapon sway.</summary>
    public float BobPhase => _bobPhase;
    public float MoveFactor { get; private set; }

    public override void _Ready()
    {
        Game.Player = this;
        // Layer 2: rays that only look for walls (mask 1) pass through the player.
        CollisionLayer = 2;
        CollisionMask = 1;
        AddChild(new CollisionShape3D
        {
            Shape = new CapsuleShape3D { Radius = 0.35f, Height = 1.7f },
            Position = new Vector3(0, 0.85f, 0),
        });
        _head = new Node3D { Name = "Head", Position = new Vector3(0, EyeHeight, 0) };
        AddChild(_head);
        _camera = new Camera3D { Name = "Camera", Fov = 80f, Near = 0.05f, Far = 120f, Current = true };
        _head.AddChild(_camera);
        _muzzle = new OmniLight3D
        {
            Position = new Vector3(0, -0.2f, -0.8f), LightColor = new Color(1f, 0.8f, 0.45f), LightEnergy = 3f,
            OmniRange = 8f, ShadowEnabled = false, Visible = false,
        };
        _camera.AddChild(_muzzle);
        _gravity = (float)ProjectSettings.GetSetting("physics/3d/default_gravity").AsDouble();
        Weapons = new Weapons(this);
    }

    public override void _ExitTree()
    {
        if (Game.Player == this) Game.Player = null;
    }

    public override void _Input(InputEvent e)
    {
        long t0 = Perf.Now;
        if (e is InputEventMouseButton { Pressed: true } mb && Input.MouseMode != Input.MouseModeEnum.Captured)
        {
            Input.MouseMode = Input.MouseModeEnum.Captured;
            _capturedAt = Time.GetTicksMsec();
            GetViewport().SetInputAsHandled();
        }
        else if (e is InputEventKey { Pressed: true, Echo: false, PhysicalKeycode: Key.Escape })
        {
            Input.MouseMode = Input.MouseModeEnum.Visible;
        }
        else if (e is InputEventMouseMotion motion && Input.MouseMode == Input.MouseModeEnum.Captured && !Dead)
        {
            RotateY(-motion.ScreenRelative.X * MouseSensitivity);
            _pitch = Mathf.Clamp(_pitch - motion.ScreenRelative.Y * MouseSensitivity, -1.5f, 1.5f);
            _head.Rotation = new Vector3(_pitch, 0, 0);
        }
        else if (e is InputEventMouseButton { Pressed: true } wheel && !Dead)
        {
            if (wheel.ButtonIndex == MouseButton.WheelUp) Weapons.Cycle(-1);
            else if (wheel.ButtonIndex == MouseButton.WheelDown) Weapons.Cycle(1);
        }
        else if (!Dead)
        {
            if (e.IsActionPressed("weapon_1")) Weapons.Select(WeaponId.Pistol);
            else if (e.IsActionPressed("weapon_2")) Weapons.Select(WeaponId.Shotgun);
            else if (e.IsActionPressed("weapon_3")) Weapons.Select(WeaponId.Launcher);
        }
        Perf.AddProcess(t0);
    }

    public override void _PhysicsProcess(double delta)
    {
        long t0 = Perf.Now;
        Perf.CountPhysicsStep();
        float dt = (float)delta;
        var v = Velocity;
        bool canAct = !Dead && !Game.Frozen;

        if (!IsOnFloor())
            v.Y -= _gravity * dt;
        else if (canAct && Input.IsActionJustPressed("jump"))
            v.Y = JumpVelocity;

        Vector2 input = canAct ? Input.GetVector("move_left", "move_right", "move_forward", "move_back") : Vector2.Zero;
        Vector3 dir = Transform.Basis * new Vector3(input.X, 0, input.Y);
        dir.Y = 0;
        if (dir.LengthSquared() > 1f) dir = dir.Normalized();

        float speed = Input.IsActionPressed("sprint") ? SprintSpeed : WalkSpeed;
        var horiz = new Vector3(v.X, 0, v.Z);
        horiz = horiz.MoveToward(dir * speed, (IsOnFloor() ? GroundAccel : AirAccel) * dt);
        v.X = horiz.X + _knock.X;
        v.Z = horiz.Z + _knock.Z;
        if (_knock.Y > 0) v.Y = Mathf.Max(v.Y, _knock.Y);
        _knock = Vector3.Zero;

        Velocity = v;
        MoveAndSlide();

        if (canAct && Game.Level != null && Game.Level.IsExitCell(Level.CellOf(GlobalPosition)))
            Game.Main?.CompleteLevel();
        Perf.AddPhysics(t0);
    }

    public override void _Process(double delta)
    {
        long t0 = Perf.Now;
        float dt = (float)delta;
        float hspeed = new Vector2(Velocity.X, Velocity.Z).Length();
        Vector3 target = Vector3.Zero;
        MoveFactor = IsOnFloor() ? Mathf.Min(hspeed / WalkSpeed, 1.5f) : 0f;
        if (IsOnFloor() && hspeed > 0.5f)
        {
            _bobPhase += dt * hspeed * 1.6f;
            float k = MoveFactor;
            target = new Vector3(Mathf.Cos(_bobPhase) * BobAmount * 0.5f * k, Mathf.Abs(Mathf.Sin(_bobPhase)) * BobAmount * k, 0);
        }

        if (Dead)
        {
            // Sink to the floor and tilt.
            _deathTime += dt;
            float k = Mathf.Min(1f, _deathTime * 2.5f);
            _head.Position = new Vector3(0, Mathf.Lerp(EyeHeight, 0.35f, k), 0);
            _head.Rotation = new Vector3(_pitch * (1 - k), 0, k * 0.5f);
        }
        else
        {
            _camera.Position = _camera.Position.Lerp(target, Mathf.Min(1f, dt * 12f));
            bool captured = Input.MouseMode == Input.MouseModeEnum.Captured;
            bool fire = !Game.Frozen && (AutoFire ||
                (captured && Input.IsActionPressed("fire") && Time.GetTicksMsec() - _capturedAt > 250));
            Weapons.Update(dt, fire);
        }
        _muzzle.Visible = Weapons.FlashTime > 0;
        Perf.AddProcess(t0);
    }

    public void OnWeaponFired() => Game.Entities?.Noise(Weapons.Current == WeaponId.Pistol ? 12 : 18);

    public void Knockback(Vector3 impulse) => _knock += impulse;

    public void TakeDamage(int amount, Vector3 from)
    {
        if (Dead || amount <= 0 || Game.GodMode || Game.Frozen) return;
        int saved = Mathf.Min(Armor, (int)(amount * ArmorAbsorb));
        Armor -= saved;
        Health -= amount - saved;
        Game.Hud?.OnDamage(amount, from);
        if (Health <= 0)
        {
            Health = 0;
            Dead = true;
            Sfx.Play(Snd.PlayerDeath);
            Game.Hud?.ShowDeath();
        }
        else
            Sfx.Play(Snd.PlayerHurt, -2f, Game.RandF(0.9f, 1.1f));
    }

    private bool Heal(int amount, int max)
    {
        if (Health >= max) return false;
        Health = Mathf.Min(max, Health + amount);
        return true;
    }

    private bool GiveArmor(int amount, float absorb)
    {
        if (Armor >= amount) return false;
        Armor = amount;
        ArmorAbsorb = absorb;
        return true;
    }

    /// <summary>Applies a pickup. Returns false if the player can't use it (it stays on the floor).</summary>
    public bool TryPickup(PickupKind kind)
    {
        bool weapon = false;
        string msg;
        bool used;
        switch (kind)
        {
            case PickupKind.Stim: used = Heal(10, 100); msg = "PICKED UP A STIMPACK."; break;
            case PickupKind.Medkit: used = Heal(25, 100); msg = "PICKED UP A MEDIKIT."; break;
            case PickupKind.Armor: used = GiveArmor(100, 1f / 3f); msg = "PICKED UP THE ARMOR."; break;
            case PickupKind.MegaArmor: used = GiveArmor(200, 0.5f); msg = "PICKED UP THE MEGAARMOR!"; break;
            case PickupKind.Clip: used = Weapons.GiveAmmo(AmmoType.Bullets, 20); msg = "PICKED UP A CLIP."; break;
            case PickupKind.Shells: used = Weapons.GiveAmmo(AmmoType.Shells, 8); msg = "PICKED UP 8 SHOTGUN SHELLS."; break;
            case PickupKind.Rockets: used = Weapons.GiveAmmo(AmmoType.Rockets, 5); msg = "PICKED UP 5 ROCKETS."; break;
            case PickupKind.Shotgun: used = Weapons.GiveWeapon(WeaponId.Shotgun, 8); msg = "YOU GOT THE SHOTGUN!"; weapon = true; break;
            case PickupKind.Launcher: used = Weapons.GiveWeapon(WeaponId.Launcher, 5); msg = "YOU GOT THE ROCKET LAUNCHER!"; weapon = true; break;
            default: return false;
        }
        if (!used) return false;
        Sfx.Play(weapon ? Snd.WeaponPickup : Snd.Item);
        Game.Hud?.OnPickup(msg, weapon);
        return true;
    }

    public static PickupKind? PickupFromChar(char c) => c switch
    {
        'h' => PickupKind.Stim,
        'm' => PickupKind.Medkit,
        'a' => PickupKind.Armor,
        'A' => PickupKind.MegaArmor,
        'c' => PickupKind.Clip,
        's' => PickupKind.Shells,
        'r' => PickupKind.Rockets,
        'G' => PickupKind.Shotgun,
        'R' => PickupKind.Launcher,
        _ => null,
    };
}
