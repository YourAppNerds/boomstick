using Godot;

namespace Boomstick;

/// <summary>
/// Title and controls screen. Shows the controls for the current input type: touch when a touch
/// screen is present (until a real mouse click switches to keyboard and mouse), keyboard and
/// mouse otherwise. Desktop: shown while the mouse is not captured. Touch: shown until the first
/// tap. Text uses the HUD's pixel font.
/// </summary>
public partial class StartScreen : CanvasLayer
{
    private static readonly Color Red = new(1f, 0.32f, 0.2f), RedDark = new(0.62f, 0.05f, 0.02f);
    private static readonly Color Tan = new(0.85f, 0.75f, 0.55f), TanDark = new(0.55f, 0.45f, 0.3f);
    private static readonly Color Yellow = new(1f, 0.95f, 0.4f), YellowDark = new(0.8f, 0.55f, 0.1f);
    private static readonly Color Grey = new(0.6f, 0.6f, 0.6f), GreyDark = new(0.4f, 0.4f, 0.4f);

    private static readonly string[] DesktopLines =
    {
        "WASD / ARROWS   MOVE        SHIFT   RUN",
        "MOUSE   LOOK        CLICK / CTRL   FIRE",
        "1 2 3 / WHEEL   SWITCH WEAPON",
        "SPACE   JUMP        ESC   RELEASE MOUSE",
        "R   RESTART AFTER DEATH        F3   STATS",
    };

    private static readonly string[] TouchLines =
    {
        "LEFT THUMB   MOVE  -  PUSH FAR TO RUN",
        "DRAG ON THE RIGHT   LOOK",
        "FIRE BUTTON   SHOOT  -  DRAG IT TO AIM",
        "1 2 3   SWITCH WEAPON",
        "THREE-FINGER TAP   STATS",
    };

    private VBoxContainer _box;
    private float _blink;
    private TextureRect _prompt;
    private int _shownMode = -1;

    public override void _Ready()
    {
        Layer = 20;
        var shade = new ColorRect { Color = new Color(0f, 0f, 0f, 0.55f), MouseFilter = Control.MouseFilterEnum.Ignore };
        shade.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        AddChild(shade);
        _box = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore, Alignment = BoxContainer.AlignmentMode.Center };
        _box.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _box.OffsetBottom = -HudArt.BarH * 4; // keep clear of the status bar
        _box.AddThemeConstantOverride("separation", 12);
        AddChild(_box);
    }

    /// <summary>0 = keyboard and mouse, 1 = touch, 2 = touch screen detected but not used yet.</summary>
    private static int Mode =>
        Game.TouchMode ? 1 : DisplayServer.IsTouchscreenAvailable() && !Game.Started ? 2 : 0;

    private void Rebuild(int mode)
    {
        foreach (var child in _box.GetChildren())
            child.QueueFree();
        Add("BOOMSTICK", 12, Red, RedDark);
        Spacer(18);
        foreach (var line in mode == 0 ? DesktopLines : TouchLines)
            Add(line, 3, Tan, TanDark);
        Spacer(6);
        Add("FIND THE GREEN EXIT PAD.", 3, Tan, TanDark);
        Spacer(18);
        _prompt = Add(mode == 0 ? "CLICK TO PLAY" : "TAP TO PLAY", 5, Yellow, YellowDark);
        if (mode == 2)
            Add("OR CLICK TO PLAY WITH KEYBOARD AND MOUSE", 2, Grey, GreyDark);
        if (mode == 0)
        {
            Spacer(10);
            Add("F4 +50 ENEMIES   F6 +50 NODE DUMMIES   F5 CLEAR", 2, Grey, GreyDark);
        }
    }

    private TextureRect Add(string text, float scale, Color top, Color bottom)
    {
        var tex = Pix.TextTexture(text, top, bottom);
        var rect = new TextureRect
        {
            Texture = tex,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            CustomMinimumSize = tex.GetSize() * scale,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter,
        };
        _box.AddChild(rect);
        return rect;
    }

    private void Spacer(float h) =>
        _box.AddChild(new Control { CustomMinimumSize = new Vector2(0, h), MouseFilter = Control.MouseFilterEnum.Ignore });

    /// <summary>Called by Main every frame with whether the screen should be up.</summary>
    public void Refresh(bool show, float dt)
    {
        Visible = show;
        if (!show) return;
        int mode = Mode;
        if (mode != _shownMode)
        {
            _shownMode = mode;
            Rebuild(mode);
        }
        _blink += dt;
        if (_prompt != null && IsInstanceValid(_prompt))
            _prompt.Modulate = new Color(1, 1, 1, 0.55f + 0.45f * Mathf.Abs(Mathf.Sin(_blink * 3f)));
    }
}
