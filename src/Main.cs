using System;
using System.Linq;
using Godot;

namespace Boomstick;

/// <summary>
/// Root of the game scene: sets up input actions, environment, level, player, the stress-test
/// dummies (F4 adds 50, F5 clears) and an optional scripted stress run.
/// Scripted stress run: pass "-- --autostress" on the command line, or add "?autostress" to the
/// page URL on web (append "=N" for N steps of 50). It steps through dummy counts and prints one "STRESS" line per step.
/// </summary>
public partial class Main : Node3D
{
    private const int DummiesPerPress = 50;

    private Level _level;
    private Player _player;
    private Node3D _dummies;
    private Label _hint;
    private int _dummyCount;
    private readonly Random _rng = new(42);

    public override void _Ready()
    {
        SetupInput();
        SetupEnvironment();

        _level = new Level { Name = "Level" };
        _level.Build(Level.DefaultMap);
        AddChild(_level);

        _player = new Player { Name = "Player", Position = _level.PlayerStart + new Vector3(0, 0.1f, 0) };
        AddChild(_player);
        _player.Rotation = new Vector3(0, -Mathf.Pi * 0.5f, 0); // face east, down the first room

        _dummies = new Node3D { Name = "Dummies" };
        AddChild(_dummies);

        SetupHud();
        DebugOverlay.SetStat("dummies", "0");

        // Do not touch RenderingServer from C# here: on the web template its static constructor
        // throws NativeMethodBindNotFoundException (get_video_adapter_type hash mismatch, because
        // the web build has rendering_device disabled). See logs/step7d1-report.md.
        GD.Print($"BOOMSTICK READY ({OS.GetName()}, " +
                 $".NET {System.Environment.Version}, map {_level.Width}x{_level.Depth}, {_level.OpenCells.Count} open cells)");

        int stressSteps = AutostressSteps();
        if (stressSteps > 0)
            RunAutostress(stressSteps);
    }

    private static void AddKeyAction(string name, params Key[] keys)
    {
        if (!InputMap.HasAction(name))
            InputMap.AddAction(name);
        foreach (var k in keys)
            InputMap.ActionAddEvent(name, new InputEventKey { PhysicalKeycode = k });
    }

    private static void SetupInput()
    {
        AddKeyAction("move_forward", Key.W, Key.Up);
        AddKeyAction("move_back", Key.S, Key.Down);
        AddKeyAction("move_left", Key.A, Key.Left);
        AddKeyAction("move_right", Key.D, Key.Right);
        AddKeyAction("sprint", Key.Shift);
        AddKeyAction("jump", Key.Space);
        AddKeyAction("spawn_dummies", Key.F4);
        AddKeyAction("clear_dummies", Key.F5);
    }

    private void SetupEnvironment()
    {
        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = new Color(0.02f, 0.02f, 0.03f),
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = new Color(0.55f, 0.5f, 0.48f),
            AmbientLightEnergy = 0.5f,
            FogEnabled = true,
            FogLightColor = new Color(0.05f, 0.04f, 0.04f),
            FogDensity = 0.03f,
        };
        AddChild(new WorldEnvironment { Environment = env });
    }

    private void SetupHud()
    {
        var layer = new CanvasLayer { Layer = 10 };
        AddChild(layer);
        _hint = new Label
        {
            Text = "CLICK TO PLAY\nWASD move  Shift sprint  Space jump  Esc release mouse\nF3 stats  F4 +50 dummies  F5 clear",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _hint.AddThemeFontSizeOverride("font_size", 22);
        _hint.AddThemeColorOverride("font_outline_color", Colors.Black);
        _hint.AddThemeConstantOverride("outline_size", 6);
        _hint.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        layer.AddChild(_hint);

        var crosshair = new Label
        {
            Text = "+",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        crosshair.AddThemeFontSizeOverride("font_size", 20);
        crosshair.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        layer.AddChild(crosshair);
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e.IsActionPressed("spawn_dummies"))
            SpawnDummies(DummiesPerPress);
        else if (e.IsActionPressed("clear_dummies"))
            ClearDummies();
    }

    public override void _Process(double delta)
    {
        long t0 = Perf.Now;
        _hint.Visible = Input.MouseMode != Input.MouseModeEnum.Captured;
        Perf.AddProcess(t0);
    }

    public void SpawnDummies(int count)
    {
        var open = _level.OpenCells;
        for (int i = 0; i < count; i++)
        {
            var cell = open[_rng.Next(open.Count)];
            var jitter = new Vector3((float)_rng.NextDouble() - 0.5f, 0, (float)_rng.NextDouble() - 0.5f) * Level.Cell * 0.6f;
            _dummies.AddChild(new Dummy { Position = Level.CellCenter(cell) + jitter + new Vector3(0, 0.7f, 0) });
        }
        _dummyCount += count;
        DebugOverlay.SetStat("dummies", _dummyCount.ToString());
    }

    public void ClearDummies()
    {
        foreach (var child in _dummies.GetChildren())
            child.QueueFree();
        _dummyCount = 0;
        DebugOverlay.SetStat("dummies", "0");
    }

    /// <summary>
    /// Returns the number of autostress steps (0 = off). Desktop: "-- --autostress[=steps]".
    /// Web: "?autostress[=steps]" in the page URL. Default is 6 steps (0..300 dummies).
    /// </summary>
    private static int AutostressSteps()
    {
        string spec = null;
        foreach (var arg in OS.GetCmdlineUserArgs())
            if (arg.StartsWith("--autostress"))
                spec = arg.Substring("--autostress".Length);
        if (spec == null && OS.HasFeature("web"))
        {
            var search = JavaScriptBridge.Eval("window.location.search", true).AsString() ?? "";
            int at = search.IndexOf("autostress", StringComparison.Ordinal);
            if (at >= 0)
            {
                spec = search.Substring(at + "autostress".Length);
                int amp = spec.IndexOf('&');
                if (amp >= 0) spec = spec.Substring(0, amp);
            }
        }
        if (spec == null) return 0;
        return spec.StartsWith("=") && int.TryParse(spec.Substring(1), out int n) && n > 0 ? n : 6;
    }

    private async void RunAutostress(int steps)
    {
        const double secondsPerStep = 4.0;
        GD.Print($"STRESS start steps={steps}");
        await ToSignal(GetTree().CreateTimer(2.0), SceneTreeTimer.SignalName.Timeout);
        for (int i = 0; i <= steps; i++)
        {
            DebugOverlay.ResetBench();
            await ToSignal(GetTree().CreateTimer(secondsPerStep), SceneTreeTimer.SignalName.Timeout);
            GD.Print($"STRESS dummies={_dummyCount} {DebugOverlay.BenchSummary()}");
            if (i < steps)
                SpawnDummies(DummiesPerPress);
        }
        GD.Print("BOOMSTICK STRESS DONE");
        if (OS.GetCmdlineUserArgs().Contains("--quit-when-done"))
            GetTree().Quit();
    }
}
