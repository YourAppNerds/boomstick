using System;
using Godot;

namespace Boomstick;

/// <summary>
/// Stress-test entity: a billboard Sprite3D that wanders around the map with simple C# logic
/// in its own _Process (one script callback per entity, like typical game code). It checks the
/// level grid instead of using physics, so the cost measured is mostly script + rendering.
/// </summary>
public partial class Dummy : Sprite3D
{
    private static Texture2D _sharedTexture;
    private static readonly Random Rng = new(1234);

    private Vector3 _dir;
    private float _speed;
    private float _turnTimer;
    private float _phase;
    private float _time;

    public override void _Ready()
    {
        _sharedTexture ??= ProcTex.Dummy();
        Texture = _sharedTexture;
        Billboard = BaseMaterial3D.BillboardModeEnum.Enabled;
        TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest;
        AlphaCut = AlphaCutMode.Discard;
        Shaded = false;
        PixelSize = 0.04f;
        CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
        _speed = 1.5f + (float)Rng.NextDouble() * 2.5f;
        _phase = (float)Rng.NextDouble() * Mathf.Tau;
        PickDirection();
    }

    private void PickDirection()
    {
        float a = (float)Rng.NextDouble() * Mathf.Tau;
        _dir = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a));
        _turnTimer = 1f + (float)Rng.NextDouble() * 3f;
    }

    public override void _Process(double delta)
    {
        long t0 = Perf.Now;
        float dt = (float)delta;
        _time += dt;
        _turnTimer -= dt;
        if (_turnTimer <= 0) PickDirection();

        var level = Level.Current;
        var pos = Position;
        var next = pos + _dir * (_speed * dt);
        if (level != null && level.IsSolidAt(next + _dir * 0.5f))
            PickDirection();
        else
            pos = next;
        pos.Y = 0.7f + Mathf.Abs(Mathf.Sin(_time * 7f + _phase)) * 0.12f;
        Position = pos;
        Perf.AddProcess(t0);
    }
}
