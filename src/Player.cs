using Godot;

namespace Boomstick;

/// <summary>
/// First-person player: WASD + mouse look, sprint, jump, subtle head bob.
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

    private Node3D _head;
    private Camera3D _camera;
    private float _pitch;
    private float _bobPhase;
    private float _gravity;

    public Camera3D Camera => _camera;

    public override void _Ready()
    {
        AddChild(new CollisionShape3D
        {
            Shape = new CapsuleShape3D { Radius = 0.35f, Height = 1.7f },
            Position = new Vector3(0, 0.85f, 0),
        });
        _head = new Node3D { Name = "Head", Position = new Vector3(0, EyeHeight, 0) };
        AddChild(_head);
        _camera = new Camera3D { Name = "Camera", Fov = 80f, Near = 0.05f, Far = 120f, Current = true };
        _head.AddChild(_camera);
        _gravity = (float)ProjectSettings.GetSetting("physics/3d/default_gravity").AsDouble();
    }

    public override void _Input(InputEvent e)
    {
        long t0 = Perf.Now;
        if (e is InputEventMouseButton { Pressed: true } && Input.MouseMode != Input.MouseModeEnum.Captured)
        {
            Input.MouseMode = Input.MouseModeEnum.Captured;
            GetViewport().SetInputAsHandled();
        }
        else if (e is InputEventKey { Pressed: true, Echo: false, PhysicalKeycode: Key.Escape })
        {
            Input.MouseMode = Input.MouseModeEnum.Visible;
        }
        else if (e is InputEventMouseMotion motion && Input.MouseMode == Input.MouseModeEnum.Captured)
        {
            RotateY(-motion.ScreenRelative.X * MouseSensitivity);
            _pitch = Mathf.Clamp(_pitch - motion.ScreenRelative.Y * MouseSensitivity, -1.5f, 1.5f);
            _head.Rotation = new Vector3(_pitch, 0, 0);
        }
        Perf.AddProcess(t0);
    }

    public override void _PhysicsProcess(double delta)
    {
        long t0 = Perf.Now;
        Perf.CountPhysicsStep();
        float dt = (float)delta;
        var v = Velocity;

        if (!IsOnFloor())
            v.Y -= _gravity * dt;
        else if (Input.IsActionJustPressed("jump"))
            v.Y = JumpVelocity;

        Vector2 input = Input.GetVector("move_left", "move_right", "move_forward", "move_back");
        Vector3 dir = Transform.Basis * new Vector3(input.X, 0, input.Y);
        dir.Y = 0;
        if (dir.LengthSquared() > 1f) dir = dir.Normalized();

        float speed = Input.IsActionPressed("sprint") ? SprintSpeed : WalkSpeed;
        var horiz = new Vector3(v.X, 0, v.Z);
        horiz = horiz.MoveToward(dir * speed, (IsOnFloor() ? GroundAccel : AirAccel) * dt);
        v.X = horiz.X;
        v.Z = horiz.Z;

        Velocity = v;
        MoveAndSlide();
        Perf.AddPhysics(t0);
    }

    public override void _Process(double delta)
    {
        long t0 = Perf.Now;
        float dt = (float)delta;
        float hspeed = new Vector2(Velocity.X, Velocity.Z).Length();
        Vector3 target = Vector3.Zero;
        if (IsOnFloor() && hspeed > 0.5f)
        {
            _bobPhase += dt * hspeed * 1.6f;
            float k = Mathf.Min(hspeed / WalkSpeed, 1.5f);
            target = new Vector3(Mathf.Cos(_bobPhase) * BobAmount * 0.5f * k, Mathf.Abs(Mathf.Sin(_bobPhase)) * BobAmount * k, 0);
        }
        _camera.Position = _camera.Position.Lerp(target, Mathf.Min(1f, dt * 12f));
        Perf.AddProcess(t0);
    }
}
