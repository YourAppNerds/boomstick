using System;
using System.Linq;
using Godot;

namespace Boomstick;

/// <summary>
/// Root of the game scene: sets up input actions, environment, level, player, entities from the
/// map, HUD and sound, plus restart (R), the level exit and the stress tests.
///
/// Stress tests (F4 / F6 add 50, F5 clears):
///   F4 "enemies": real enemies with full AI, all updated by the one EntityManager.
///   F6 "nodes":   D1-style Dummy sprites, each with its own C# _Process (per-node cost).
/// Scripted run: "-- --autostress[=steps] [--stressmode=enemies|nodes|wander] [--quit-when-done]"
/// on desktop, or "?autostress[=steps][&amp;stressmode=...]" on web. "wander" spawns managed
/// enemies that run the same trivial wander logic as the Dummy, for a like-for-like comparison.
/// </summary>
public partial class Main : Node3D
{
    private const int StressPerStep = 50;

    public enum StressMode { Enemies, Nodes, Wander }

    private Level _level;
    private Player _player;
    private EntityManager _entities;
    private Node3D _dummies;
    private StartScreen _startScreen;
    private bool _devView; // a dev argument was given: skip the title screen (screenshots)
    private int _dummyCount;
    private int _stressEnemies;
    private bool _complete;
    private readonly Random _rng = new(42);

    public override void _Ready()
    {
        Game.Reset();
        Game.Main = this;
        SetupInput();
        SetupEnvironment();

        _level = new Level { Name = "Level" };
        _level.Build(Level.DefaultMap);
        AddChild(_level);
        Game.Level = _level;

        AddChild(new Sfx { Name = "Sfx" });

        _player = new Player { Name = "Player", Position = _level.PlayerStart + new Vector3(0, 0.1f, 0) };
        AddChild(_player);
        _player.Rotation = new Vector3(0, -Mathf.Pi * 0.5f, 0); // face east, down the first room

        // "--enemyrender=nodes" / "?enemyrender=nodes": one Sprite3D per enemy instead of MultiMesh batches.
        EntityManager.Batched = GetOption("enemyrender") != "=nodes";
        _entities = new EntityManager { Name = "Entities" };
        Game.Entities = _entities;
        AddChild(_entities);
        SpawnFromMap();

        _dummies = new Node3D { Name = "Dummies" };
        AddChild(_dummies);

        // "?scale=0.5" / "-- --scale=0.5": render the 3D view at a lower resolution (phones with
        // very high pixel density); the HUD stays sharp.
        var scale = GetOption("scale");
        if (scale != null && float.TryParse(scale.TrimStart('='), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float s3d) && s3d is > 0.2f and <= 1f)
            GetViewport().Scaling3DScale = s3d;

        AddChild(new Hud { Name = "Hud" });
        AddChild(new TouchControls { Name = "TouchControls" });
        _startScreen = new StartScreen { Name = "StartScreen" };
        AddChild(_startScreen);
        UpdateStressStat();

        // RenderingServer is usable from C# on web since engine commit 11875322e9 (step 7e); before
        // that its static constructor threw NativeMethodBindNotFoundException (logs/step7d1-report.md).
        // The adapter name in the READY line doubles as a check.
        GD.Print($"BOOMSTICK READY ({OS.GetName()}, " +
                 $".NET {System.Environment.Version}, map {_level.Width}x{_level.Depth}, {_level.OpenCells.Count} open cells, " +
                 $"{Game.TotalEnemies} enemies, level mesh {_level.VertexCount} verts, GPU {RenderingServer.GetVideoAdapterName()})");

        ApplyDevArgs();

        int stressSteps = AutostressSteps(out var mode);
        if (stressSteps > 0)
            RunAutostress(stressSteps, mode);
    }

    public override void _ExitTree()
    {
        if (Game.Main == this) Game.Main = null;
    }

    private void SpawnFromMap()
    {
        foreach (var (c, cell) in _level.Spawns)
        {
            var pos = Level.CellCenter(cell);
            var enemy = EnemyDef.FromChar(c);
            if (enemy.HasValue)
            {
                _entities.SpawnEnemy(enemy.Value, pos, counted: true);
                continue;
            }
            var pickup = Player.PickupFromChar(c);
            if (pickup.HasValue)
                _entities.SpawnPickup(pickup.Value, pos);
        }
    }

    private static void AddKeyAction(string name, params Key[] keys)
    {
        if (InputMap.HasAction(name)) return; // already set up by an earlier load of the scene
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
        AddKeyAction("weapon_1", Key.Key1);
        AddKeyAction("weapon_2", Key.Key2);
        AddKeyAction("weapon_3", Key.Key3);
        AddKeyAction("restart", Key.R);
        AddKeyAction("spawn_enemies", Key.F4);
        AddKeyAction("clear_stress", Key.F5);
        AddKeyAction("spawn_dummies", Key.F6);
        if (!InputMap.HasAction("fire"))
        {
            AddKeyAction("fire", Key.Ctrl);
            InputMap.ActionAddEvent("fire", new InputEventMouseButton { ButtonIndex = MouseButton.Left });
        }
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

    public override void _UnhandledInput(InputEvent e)
    {
        if (e.IsActionPressed("spawn_enemies"))
            SpawnStress(StressMode.Enemies, StressPerStep);
        else if (e.IsActionPressed("spawn_dummies"))
            SpawnStress(StressMode.Nodes, StressPerStep);
        else if (e.IsActionPressed("clear_stress"))
            ClearStress();
        else if (e.IsActionPressed("restart"))
            Restart();
    }

    public override void _Process(double delta)
    {
        long t0 = Perf.Now;
        bool playing = !_complete && !_player.Dead;
        bool waiting = Game.TouchMode ? !Game.Started : Input.MouseMode != Input.MouseModeEnum.Captured;
        _startScreen.Refresh(playing && waiting && !_devView, (float)delta);
        if (playing && Game.Started)
            Game.LevelTime += delta;
        Perf.AddProcess(t0);
    }

    /// <summary>Reloads the level after death or at the exit (R key or the touch restart button).</summary>
    public void Restart()
    {
        if (_player.Dead || _complete)
            GetTree().CallDeferred(SceneTree.MethodName.ReloadCurrentScene);
    }

    public void CompleteLevel()
    {
        if (_complete || Game.GodMode) return;
        _complete = true;
        Game.Frozen = true;
        Sfx.Play(Snd.LevelDone);
        Game.Hud?.ShowLevelComplete(Game.LevelTime, Game.Kills, Game.TotalEnemies);
        GD.Print($"LEVEL COMPLETE time={Game.LevelTime:0.0}s kills={Game.Kills}/{Game.TotalEnemies}");
    }

    /// <summary>
    /// Developer/test arguments (desktop "-- ..." only): --warp=cellX,cellZ,yawDegrees moves the
    /// player, --giveall gives every weapon and ammo, --godmode makes him invulnerable,
    /// --autofire holds the trigger. Handy with --write-movie for screenshots.
    /// </summary>
    private void ApplyDevArgs()
    {
        foreach (var arg in OS.GetCmdlineUserArgs())
        {
            if (arg.StartsWith("--warp") || arg == "--autofire")
                _devView = true;
            if (arg.StartsWith("--warp="))
            {
                var v = arg.Substring(7).Split(',');
                if (v.Length >= 2 && int.TryParse(v[0], out int x) && int.TryParse(v[1], out int z))
                {
                    _player.Position = Level.CellCenter(new Vector2I(x, z)) + new Vector3(0, 0.1f, 0);
                    if (v.Length >= 3 && float.TryParse(v[2], System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out float yaw))
                        _player.Rotation = new Vector3(0, Mathf.DegToRad(yaw), 0);
                }
            }
            else if (arg == "--giveall")
            {
                _player.Weapons.GiveWeapon(WeaponId.Shotgun, 50);
                _player.Weapons.GiveWeapon(WeaponId.Launcher, 50);
                _player.Weapons.GiveAmmo(AmmoType.Bullets, 200);
            }
            else if (arg.StartsWith("--weapon=") && int.TryParse(arg.Substring(9), out int w) && w >= 1 && w <= 3)
                _player.Weapons.Select((WeaponId)(w - 1));
            else if (arg == "--godmode")
                Game.GodMode = true;
            else if (arg == "--autofire")
                _player.AutoFire = true;
        }
    }

    // ---- Stress tests -------------------------------------------------------------------------

    public void SpawnStress(StressMode mode, int count)
    {
        var open = _level.OpenCells;
        for (int i = 0; i < count; i++)
        {
            var cell = open[_rng.Next(open.Count)];
            var jitter = new Vector3((float)_rng.NextDouble() - 0.5f, 0, (float)_rng.NextDouble() - 0.5f) * Level.Cell * 0.5f;
            var pos = Level.CellCenter(cell) + jitter;
            if (mode == StressMode.Nodes)
            {
                _dummies.AddChild(new Dummy { Position = pos + new Vector3(0, 0.7f, 0) });
                _dummyCount++;
            }
            else
            {
                // Mostly imps and fiends, the odd brute, like a real horde.
                var kind = (_rng.Next(10)) switch { < 5 => EnemyKind.Imp, < 9 => EnemyKind.Fiend, _ => EnemyKind.Brute };
                _entities.SpawnEnemy(kind, pos, counted: false, awake: mode == StressMode.Enemies, wander: mode == StressMode.Wander);
                _stressEnemies++;
            }
        }
        UpdateStressStat();
    }

    public void ClearStress()
    {
        foreach (var child in _dummies.GetChildren())
            child.QueueFree();
        _entities.ClearEnemies(onlyUncounted: true);
        _dummyCount = 0;
        _stressEnemies = 0;
        UpdateStressStat();
    }

    private void UpdateStressStat() =>
        DebugOverlay.SetStat("stress", $"{_stressEnemies} managed enemies, {_dummyCount} node dummies");

    /// <summary>
    /// Returns the number of autostress steps (0 = off). Desktop: "-- --autostress[=steps]".
    /// Web: "?autostress[=steps]" in the page URL. Default is 6 steps (0..300).
    /// </summary>
    private static int AutostressSteps(out StressMode mode)
    {
        string spec = null, modeSpec = null;
        foreach (var arg in OS.GetCmdlineUserArgs())
        {
            if (arg.StartsWith("--autostress"))
                spec = arg.Substring("--autostress".Length);
            if (arg.StartsWith("--stressmode="))
                modeSpec = arg.Substring("--stressmode=".Length);
        }
        if (spec == null && OS.HasFeature("web"))
        {
            var search = JavaScriptBridge.Eval("window.location.search", true).AsString() ?? "";
            spec = QueryValue(search, "autostress");
            modeSpec ??= QueryValue(search, "stressmode")?.TrimStart('=');
        }
        mode = modeSpec switch
        {
            "nodes" => StressMode.Nodes,
            "wander" => StressMode.Wander,
            _ => StressMode.Enemies,
        };
        if (spec == null) return 0;
        return spec.StartsWith("=") && int.TryParse(spec.Substring(1), out int n) && n > 0 ? n : 6;
    }

    /// <summary>
    /// Reads an option from the desktop command line ("-- --key=value") or the web page URL
    /// ("?key=value"). Returns "" or "=value" when present, null when absent.
    /// </summary>
    public static string GetOption(string key)
    {
        foreach (var arg in OS.GetCmdlineUserArgs())
            if (arg.StartsWith("--" + key))
                return arg.Substring(key.Length + 2);
        if (OS.HasFeature("web"))
            return QueryValue(JavaScriptBridge.Eval("window.location.search", true).AsString() ?? "", key);
        return null;
    }

    /// <summary>Returns "" or "=value" for a present query key, null if absent.</summary>
    private static string QueryValue(string search, string key)
    {
        int at = search.IndexOf(key, StringComparison.Ordinal);
        if (at < 0) return null;
        string rest = search.Substring(at + key.Length);
        int amp = rest.IndexOf('&');
        if (amp >= 0) rest = rest.Substring(0, amp);
        return rest;
    }

    private async void RunAutostress(int steps, StressMode mode)
    {
        const double secondsPerStep = 4.0;
        Game.GodMode = true;
        // Start from an empty level so the counts are exact.
        _entities.ClearEnemies(onlyUncounted: false);
        GD.Print($"STRESS start steps={steps} mode={mode.ToString().ToLowerInvariant()} batched={EntityManager.Batched}");
        await ToSignal(GetTree().CreateTimer(2.0), SceneTreeTimer.SignalName.Timeout);
        for (int i = 0; i <= steps; i++)
        {
            DebugOverlay.ResetBench();
            await ToSignal(GetTree().CreateTimer(secondsPerStep), SceneTreeTimer.SignalName.Timeout);
            int count = mode == StressMode.Nodes ? _dummyCount : _stressEnemies;
            GD.Print($"STRESS mode={mode.ToString().ToLowerInvariant()}{(mode != StressMode.Nodes && !EntityManager.Batched ? "-sprites" : "")} count={count} projectiles={_entities.ProjectileCount} {DebugOverlay.BenchSummary()}");
            if (i < steps)
                SpawnStress(mode, StressPerStep);
        }
        GD.Print("BOOMSTICK STRESS DONE");
        if (OS.GetCmdlineUserArgs().Contains("--quit-when-done"))
            GetTree().Quit();
    }
}
