using System;
using Godot;

namespace Boomstick;

/// <summary>
/// Doom-style HUD: first-person weapon with bob, recoil and switch animation, the bottom status
/// bar (ammo, health, arms, face, armor, ammo table, kills), crosshair, damage and pickup
/// flashes, pickup messages and the death / level-complete screens. Text is drawn with the
/// 5x7 pixel font into small textures, regenerated only when the value changes.
/// </summary>
public partial class Hud : CanvasLayer
{
    private const int Zoom = 4;            // bar pixels -> screen pixels
    private const int BarHeight = HudArt.BarH * Zoom;

    private static readonly Color Red = new(1f, 0.32f, 0.2f), RedDark = new(0.62f, 0.05f, 0.02f);
    private static readonly Color Yellow = new(1f, 0.95f, 0.4f), YellowDark = new(0.8f, 0.55f, 0.1f);
    private static readonly Color Tan = new(0.85f, 0.75f, 0.55f), TanDark = new(0.55f, 0.45f, 0.3f);
    private static readonly Color Grey = new(0.45f, 0.45f, 0.45f), GreyDark = new(0.3f, 0.3f, 0.3f);
    private static readonly Color White = new(1f, 1f, 1f), WhiteDark = new(0.7f, 0.7f, 0.75f);

    private enum Align { Left, Center, Right }

    /// <summary>A line of pixel-font text shown as a TextureRect, re-rendered only on change.</summary>
    private sealed class HudText
    {
        public readonly TextureRect Rect;
        private string _text;
        private Color _top, _bottom;
        private readonly float _scale;
        private readonly Align _align;
        private readonly Vector2 _at;

        public HudText(Control parent, Vector2 at, float scale, Align align, Color top, Color bottom)
        {
            _at = at;
            _scale = scale;
            _align = align;
            _top = top;
            _bottom = bottom;
            Rect = new TextureRect
            {
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.Scale,
                MouseFilter = Control.MouseFilterEnum.Ignore,
                TextureFilter = CanvasItem.TextureFilterEnum.Nearest,
            };
            parent.AddChild(Rect);
        }

        public void Set(string s, Color? top = null, Color? bottom = null)
        {
            var t = top ?? _top;
            var b = bottom ?? _bottom;
            if (s == _text && t == _top && b == _bottom) return;
            _text = s;
            _top = t;
            _bottom = b;
            var tex = Pix.TextTexture(s, t, b);
            Rect.Texture = tex;
            var size = new Vector2(tex.GetWidth(), tex.GetHeight()) * _scale;
            float x = _align switch
            {
                Align.Center => _at.X - size.X / 2f,
                Align.Right => _at.X - size.X,
                _ => _at.X,
            };
            Rect.Position = new Vector2(Mathf.Round(x), _at.Y);
            Rect.Size = size;
        }
    }

    private Control _root;
    private TextureRect _weapon;
    private ImageTexture[] _weaponFrames;
    private WeaponId _shownWeapon = (WeaponId)(-1);
    private int _shownFrame = -1;
    private ColorRect _damageFlash, _pickupFlash;
    private float _damageAlpha, _pickupAlpha;

    private HudText _ammo, _health, _armor, _kills;
    private readonly HudText[] _arms = new HudText[3];
    private readonly HudText[] _ammoRows = new HudText[3];
    private TextureRect _face;
    private float _faceLookTimer;
    private int _faceLook;
    private float _ouchTimer, _grinTimer;
    private Texture2D _shownFace;

    private HudText _message;
    private float _messageTimer;

    private Control _center;
    private HudText _title;
    private readonly HudText[] _lines = new HudText[3];

    public override void _Ready()
    {
        Game.Hud = this;
        Layer = 10;
        _root = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        _root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        AddChild(_root);

        // First-person weapon (drawn below the bar so the bar covers its bottom edge).
        _weapon = new TextureRect
        {
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            AnchorLeft = 0.5f, AnchorRight = 0.5f, AnchorTop = 1f, AnchorBottom = 1f,
        };
        _root.AddChild(_weapon);

        var crosshair = new TextureRect
        {
            Texture = HudArt.Crosshair(),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            AnchorLeft = 0.5f, AnchorRight = 0.5f, AnchorTop = 0.5f, AnchorBottom = 0.5f,
            OffsetLeft = -9, OffsetRight = 9, OffsetTop = -9, OffsetBottom = 9,
            Modulate = new Color(1, 1, 1, 0.8f),
        };
        _root.AddChild(crosshair);

        _damageFlash = FullRect(new Color(0.9f, 0f, 0f, 0f));
        _pickupFlash = FullRect(new Color(1f, 0.85f, 0.3f, 0f));

        BuildBar();

        var top = new Control { AnchorLeft = 0.5f, AnchorRight = 0.5f, MouseFilter = Control.MouseFilterEnum.Ignore };
        _root.AddChild(top);
        _message = new HudText(top, new Vector2(0, 14), 3, Align.Center, White, WhiteDark);
        _message.Rect.Visible = false;

        _center = new Control
        {
            AnchorLeft = 0.5f, AnchorRight = 0.5f, AnchorTop = 0.35f, AnchorBottom = 0.35f,
            MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false,
        };
        _root.AddChild(_center);
        _title = new HudText(_center, new Vector2(0, -40), 8, Align.Center, Red, RedDark);
        for (int i = 0; i < _lines.Length; i++)
            _lines[i] = new HudText(_center, new Vector2(0, 50 + i * 40), 4, Align.Center, Tan, TanDark);
    }

    public override void _ExitTree()
    {
        if (Game.Hud == this) Game.Hud = null;
    }

    private ColorRect FullRect(Color c)
    {
        var r = new ColorRect { Color = c, MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        r.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _root.AddChild(r);
        return r;
    }

    private void BuildBar()
    {
        // Dark fill for screens wider than the 1280-pixel bar.
        var fill = new ColorRect
        {
            Color = new Color(0.16f, 0.15f, 0.14f), MouseFilter = Control.MouseFilterEnum.Ignore,
            AnchorLeft = 0, AnchorRight = 1, AnchorTop = 1, AnchorBottom = 1, OffsetTop = -BarHeight,
        };
        _root.AddChild(fill);

        int w = HudArt.BarW * Zoom;
        var bar = new TextureRect
        {
            Texture = HudArt.Bar(),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            AnchorLeft = 0.5f, AnchorRight = 0.5f, AnchorTop = 1f, AnchorBottom = 1f,
            OffsetLeft = -w / 2, OffsetRight = w / 2, OffsetTop = -BarHeight, OffsetBottom = 0,
        };
        _root.AddChild(bar);

        var s = HudArt.Sections;
        float Mid(int i) => (s[i] + s[i + 1]) * Zoom / 2f;
        _ammo = new HudText(bar, new Vector2(Mid(0), 8), 6, Align.Center, Red, RedDark);
        _health = new HudText(bar, new Vector2(Mid(1), 8), 6, Align.Center, Red, RedDark);
        _armor = new HudText(bar, new Vector2(Mid(4), 8), 6, Align.Center, Red, RedDark);
        _kills = new HudText(bar, new Vector2(Mid(6), 22), 3, Align.Center, Red, RedDark);
        for (int i = 0; i < 3; i++)
            _arms[i] = new HudText(bar, new Vector2(s[2] * Zoom + 26 + i * 40, 18), 4, Align.Left, Grey, GreyDark);
        for (int i = 0; i < 3; i++)
            _ammoRows[i] = new HudText(bar, new Vector2(s[5] * Zoom + 14, 12 + i * 28), 3, Align.Left, Tan, TanDark);

        _face = new TextureRect
        {
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Position = new Vector2(Mid(3) - 42, 7),
            Size = new Vector2(84, 91),
        };
        bar.AddChild(_face);
    }

    public override void _Process(double delta)
    {
        long t0 = Perf.Now;
        float dt = (float)delta;
        var p = Game.Player;
        if (p != null)
        {
            UpdateWeapon(p, dt);
            UpdateBar(p);
            UpdateFace(p, dt);
        }
        if (_damageAlpha > 0)
        {
            _damageAlpha = Mathf.Max(0, _damageAlpha - dt * 1.5f);
            _damageFlash.Color = new Color(0.9f, 0f, 0f, _damageAlpha);
            _damageFlash.Visible = _damageAlpha > 0;
        }
        if (_pickupAlpha > 0)
        {
            _pickupAlpha = Mathf.Max(0, _pickupAlpha - dt * 1.2f);
            _pickupFlash.Color = new Color(1f, 0.85f, 0.3f, _pickupAlpha);
            _pickupFlash.Visible = _pickupAlpha > 0;
        }
        if (_messageTimer > 0)
        {
            _messageTimer -= dt;
            if (_messageTimer <= 0) _message.Rect.Visible = false;
        }
        Perf.AddProcess(t0);
    }

    private void UpdateWeapon(Player p, float dt)
    {
        var w = p.Weapons;
        if (w.Current != _shownWeapon)
        {
            _shownWeapon = w.Current;
            _weaponFrames = HudArt.WeaponFrames(w.Current);
            _shownFrame = -1;
        }
        int frame = Math.Min(w.Frame, _weaponFrames.Length - 1);
        if (frame != _shownFrame)
        {
            _shownFrame = frame;
            _weapon.Texture = _weaponFrames[frame];
        }
        _weapon.Visible = !p.Dead;
        float ww = HudArt.WeaponW * Zoom, wh = HudArt.WeaponH * Zoom;
        float bob = p.MoveFactor;
        float sx = Mathf.Cos(p.BobPhase) * 22f * bob;
        float sy = Mathf.Abs(Mathf.Sin(p.BobPhase)) * 14f * bob;
        float y = -BarHeight - wh + 44 + sy + w.Kick + w.Lower * wh;
        _weapon.OffsetLeft = Mathf.Round(-ww / 2 + sx);
        _weapon.OffsetRight = _weapon.OffsetLeft + ww;
        _weapon.OffsetTop = Mathf.Round(y);
        _weapon.OffsetBottom = _weapon.OffsetTop + wh;
    }

    private void UpdateBar(Player p)
    {
        var w = p.Weapons;
        _ammo.Set(w.CurrentAmmo.ToString());
        _health.Set(p.Health + "%");
        _armor.Set(p.Armor + "%");
        _kills.Set($"{Game.Kills}/{Game.TotalEnemies}");
        for (int i = 0; i < 3; i++)
        {
            bool owned = w.Owned[i];
            _arms[i].Set((i + 1).ToString(), owned ? Yellow : Grey, owned ? YellowDark : GreyDark);
        }
        for (int i = 0; i < 3; i++)
        {
            bool cur = (int)w.Def.Ammo == i;
            _ammoRows[i].Set($"{WeaponDef.AmmoNames[i]} {w.Ammo[i],3}/{WeaponDef.MaxAmmo[i]}",
                cur ? Yellow : Tan, cur ? YellowDark : TanDark);
        }
    }

    private void UpdateFace(Player p, float dt)
    {
        int tier = p.Health >= 80 ? 0 : p.Health >= 60 ? 1 : p.Health >= 40 ? 2 : p.Health >= 20 ? 3 : 4;
        _ouchTimer -= dt;
        _grinTimer -= dt;
        _faceLookTimer -= dt;
        if (_faceLookTimer <= 0)
        {
            _faceLookTimer = Game.RandF(0.6f, 1.8f);
            _faceLook = Game.RandRange(-1, 1);
        }
        HudArt.FaceMood mood = p.Dead ? HudArt.FaceMood.Dead
            : _ouchTimer > 0 ? HudArt.FaceMood.Ouch
            : _grinTimer > 0 ? HudArt.FaceMood.Grin
            : HudArt.FaceMood.Normal;
        var tex = HudArt.Face(tier, _faceLook, mood);
        if (tex != _shownFace)
        {
            _shownFace = tex;
            _face.Texture = tex;
        }
    }

    public void OnDamage(int amount, Vector3 from)
    {
        _damageAlpha = Mathf.Min(0.6f, _damageAlpha + 0.15f + amount * 0.012f);
        _damageFlash.Visible = true;
        if (amount >= 12) _ouchTimer = 0.7f;
        // Look towards the attacker.
        var p = Game.Player;
        if (p != null)
        {
            var local = p.GlobalTransform.Basis.Inverse() * (from - p.GlobalPosition);
            _faceLook = local.X < -1f ? -1 : local.X > 1f ? 1 : 0;
            _faceLookTimer = 1f;
        }
    }

    public void OnPickup(string message, bool weapon)
    {
        _pickupAlpha = weapon ? 0.35f : 0.2f;
        _pickupFlash.Visible = true;
        if (weapon) _grinTimer = 1.5f;
        ShowMessage(message);
    }

    public void ShowMessage(string message)
    {
        _message.Set(message);
        _message.Rect.Visible = true;
        _messageTimer = 3f;
    }

    public void ShowDeath()
    {
        _damageAlpha = 0.6f;
        _damageFlash.Visible = true;
        ShowCenter("YOU DIED", "PRESS R TO RESTART", "", "");
    }

    public void ShowLevelComplete(double seconds, int kills, int total)
    {
        int m = (int)(seconds / 60), s = (int)(seconds % 60);
        _title.Set("LEVEL COMPLETE", Yellow, YellowDark);
        ShowCenter(null, $"TIME {m}:{s:00}", $"KILLS {kills} / {total}", "PRESS R TO PLAY AGAIN");
    }

    private void ShowCenter(string title, string a, string b, string c)
    {
        if (title != null) _title.Set(title, Red, RedDark);
        _lines[0].Set(a);
        _lines[1].Set(b);
        _lines[2].Set(c);
        _center.Visible = true;
    }
}
