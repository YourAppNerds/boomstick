using System.Collections.Generic;
using Godot;

namespace Boomstick;

/// <summary>
/// On-screen controls for phones and tablets, active once a touch event arrives:
/// a floating move stick on the left (push it far to run), drag anywhere on the right to look,
/// a fire button (dragging it also aims), weapon buttons 1-3, and a restart button after death or
/// at the exit. A three-finger tap toggles the stats overlay. The layout is recomputed from the
/// visible rect every frame, so window resizes and rotation just work.
///
/// Touch positions are tracked per finger here instead of using InputEventScreenDrag.Relative:
/// on the web the engine computes Relative against the wrong finger when several touch at once
/// (see logs/step7d3-report.md).
/// </summary>
public partial class TouchControls : CanvasLayer
{
    private const float BarHeight = HudArt.BarH * 4;
    private const float StickRadius = 110f, KnobRadius = 46f;
    private const float FireRadius = 95f, WeaponRadius = 46f;
    private const float LookSensitivity = 0.0055f;
    private const float RunThreshold = 0.85f;

    private static readonly Color Fill = new(0f, 0f, 0f, 0.28f);
    private static readonly Color Edge = new(1f, 0.82f, 0.45f, 0.55f);
    private static readonly Color EdgeHot = new(1f, 0.9f, 0.55f, 0.95f);
    private static readonly Color FireFill = new(0.75f, 0.08f, 0.04f, 0.38f);
    private static readonly Color FireHot = new(1f, 0.25f, 0.1f, 0.6f);

    private sealed partial class Surface : Control
    {
        public TouchControls Owner;
        public override void _Draw() => Owner.DrawControls(this);
    }

    private Surface _surface;
    private Vector2 _size;

    // Finger index -> role.
    private enum Role { Stick, Look, Fire, Button }
    private readonly Dictionary<int, Role> _roles = new();
    private readonly Dictionary<int, Vector2> _last = new();
    private int _stickIndex = -1;
    private Vector2 _stickOrigin, _stickPos;
    private int _fireIndex = -1;

    private Texture2D _fireText, _restartText, _againText, _runText;
    private readonly Texture2D[] _weaponText = new Texture2D[3];

    public override void _Ready()
    {
        Layer = 15;
        _surface = new Surface { Owner = this, MouseFilter = Control.MouseFilterEnum.Ignore };
        _surface.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        AddChild(_surface);
        var light = new Color(1f, 0.95f, 0.8f);
        var dark = new Color(0.85f, 0.65f, 0.35f);
        _fireText = Pix.TextTexture("FIRE", light, dark);
        _restartText = Pix.TextTexture("RESTART", light, dark);
        _againText = Pix.TextTexture("PLAY AGAIN", light, dark);
        _runText = Pix.TextTexture("RUN", light, dark);
        for (int i = 0; i < 3; i++)
            _weaponText[i] = Pix.TextTexture((i + 1).ToString(), light, dark);
    }

    public override void _ExitTree() => ReleaseAll();

    // ---- Layout -------------------------------------------------------------------------------

    private Vector2 StickHome => new(StickRadius + 70f, _size.Y - BarHeight - StickRadius - 40f);
    private Vector2 FireCenter => new(_size.X - FireRadius - 95f, _size.Y - BarHeight - FireRadius - 60f);

    private Vector2 WeaponCenter(int i) => FireCenter + new Vector2((i - 1) * 112f, -FireRadius - 120f);

    private bool ShowRestart => Game.Player is { } p && (p.Dead || Game.Frozen);

    private Rect2 RestartRect => new(_size.X / 2f - 230f, _size.Y * 0.72f - 50f, 460f, 100f);

    // ---- Input --------------------------------------------------------------------------------

    public override void _Input(InputEvent e)
    {
        if (e is InputEventScreenTouch touch)
        {
            Sfx.Unlock();
            if (!Game.TouchMode)
            {
                Game.TouchMode = true;
                Input.MouseMode = Input.MouseModeEnum.Visible;
            }
            if (touch.Pressed) Press(touch.Index, touch.Position);
            else Release(touch.Index);
            GetViewport().SetInputAsHandled();
        }
        else if (e is InputEventScreenDrag drag)
        {
            Drag(drag.Index, drag.Position);
            GetViewport().SetInputAsHandled();
        }
    }

    private void Press(int index, Vector2 pos)
    {
        _last[index] = pos;
        if (!Game.Started)
        {
            // The first tap dismisses the start screen (the page also goes fullscreen on it).
            Game.Started = true;
            _roles[index] = Role.Button;
            return;
        }
        if (_last.Count >= 3)
        {
            DebugOverlay.Toggle();
            _roles[index] = Role.Button;
            return;
        }
        if (ShowRestart)
        {
            if (RestartRect.Grow(20f).HasPoint(pos)) Game.Main?.Restart();
            _roles[index] = Role.Button;
            return;
        }
        var player = Game.Player;
        for (int i = 0; i < 3; i++)
        {
            if (pos.DistanceTo(WeaponCenter(i)) <= WeaponRadius + 14f)
            {
                player?.Weapons.Select((WeaponId)i);
                _roles[index] = Role.Button;
                return;
            }
        }
        if (pos.DistanceTo(FireCenter) <= FireRadius + 18f)
        {
            _roles[index] = Role.Fire;
            _fireIndex = index;
            Game.TouchFire = true;
            return;
        }
        if (pos.X < _size.X * 0.42f && _stickIndex < 0)
        {
            _roles[index] = Role.Stick;
            _stickIndex = index;
            // Floating stick: it appears under the thumb, unless the thumb lands on the drawn one.
            _stickOrigin = pos.DistanceTo(StickHome) <= StickRadius ? StickHome : pos;
            _stickPos = pos;
            UpdateStick();
            return;
        }
        _roles[index] = Role.Look;
    }

    private void Drag(int index, Vector2 pos)
    {
        if (!_last.TryGetValue(index, out var prev)) prev = pos;
        _last[index] = pos;
        if (!_roles.TryGetValue(index, out var role)) return;
        if (role == Role.Stick)
        {
            _stickPos = pos;
            UpdateStick();
        }
        else if (role is Role.Look or Role.Fire)
        {
            var d = pos - prev;
            Game.Player?.Look(-d.X * LookSensitivity, -d.Y * LookSensitivity * 0.8f);
        }
    }

    private void Release(int index)
    {
        _last.Remove(index);
        if (!_roles.Remove(index, out var role)) return;
        if (role == Role.Stick && index == _stickIndex)
        {
            _stickIndex = -1;
            Game.TouchMove = Vector2.Zero;
            Game.TouchSprint = false;
        }
        else if (role == Role.Fire && index == _fireIndex)
        {
            _fireIndex = -1;
            Game.TouchFire = false;
        }
    }

    private void ReleaseAll()
    {
        _roles.Clear();
        _last.Clear();
        _stickIndex = _fireIndex = -1;
        Game.TouchMove = Vector2.Zero;
        Game.TouchFire = Game.TouchSprint = false;
    }

    private void UpdateStick()
    {
        var v = (_stickPos - _stickOrigin) / StickRadius;
        float len = v.Length();
        if (len > 1f) v /= len;
        // Small dead zone, then a linear response.
        Game.TouchMove = len < 0.12f ? Vector2.Zero : v;
        Game.TouchSprint = len >= RunThreshold;
    }

    // ---- Drawing ------------------------------------------------------------------------------

    public override void _Process(double delta)
    {
        _size = _surface.GetViewportRect().Size;
        bool show = Game.TouchMode && Game.Started;
        if (Visible != show)
        {
            Visible = show;
            if (!show) ReleaseAll();
        }
        if (show) _surface.QueueRedraw();
    }

    private void DrawControls(Control c)
    {
        if (ShowRestart)
        {
            var r = RestartRect;
            c.DrawRect(r, new Color(0f, 0f, 0f, 0.55f));
            c.DrawRect(r, EdgeHot, false, 4f);
            DrawLabel(c, Game.Frozen ? _againText : _restartText, r.GetCenter(), 6f);
            return;
        }

        // Move stick.
        bool active = _stickIndex >= 0;
        var origin = active ? _stickOrigin : StickHome;
        c.DrawCircle(origin, StickRadius, Fill);
        c.DrawArc(origin, StickRadius, 0, Mathf.Tau, 48, active ? EdgeHot : Edge, 4f);
        if (active && Game.TouchSprint)
            DrawLabel(c, _runText, origin + new Vector2(0, -StickRadius - 26f), 3f);
        var knob = origin + Game.TouchMove * StickRadius;
        c.DrawCircle(knob, KnobRadius, active ? new Color(1f, 0.8f, 0.45f, 0.55f) : new Color(1f, 0.8f, 0.45f, 0.3f));

        // Fire.
        bool firing = _fireIndex >= 0;
        c.DrawCircle(FireCenter, FireRadius, firing ? FireHot : FireFill);
        c.DrawArc(FireCenter, FireRadius, 0, Mathf.Tau, 48, firing ? EdgeHot : Edge, 4f);
        DrawLabel(c, _fireText, FireCenter, 5f);

        // Weapons.
        var w = Game.Player?.Weapons;
        for (int i = 0; i < 3; i++)
        {
            bool owned = w != null && w.Owned[i];
            bool current = w != null && (int)w.Current == i;
            var center = WeaponCenter(i);
            c.DrawCircle(center, WeaponRadius, current ? new Color(1f, 0.75f, 0.2f, 0.45f) : Fill);
            c.DrawArc(center, WeaponRadius, 0, Mathf.Tau, 32, owned ? Edge : new Color(0.5f, 0.5f, 0.5f, 0.4f), 3f);
            DrawLabel(c, _weaponText[i], center, 5f, owned ? Colors.White : new Color(1f, 1f, 1f, 0.35f));
        }
    }

    private static void DrawLabel(Control c, Texture2D tex, Vector2 center, float scale, Color? modulate = null)
    {
        var size = tex.GetSize() * scale;
        c.DrawTextureRect(tex, new Rect2((center - size / 2f).Round(), size), false, modulate ?? Colors.White);
    }
}
