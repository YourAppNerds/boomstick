using System;
using System.Collections.Generic;
using System.Text;
using Godot;

namespace Boomstick;

/// <summary>
/// Autoloaded performance overlay (toggle with F3). Shows FPS, frame time, time spent in
/// C# _Process/_PhysicsProcess (via <see cref="Perf"/>), node count, GC counts and managed
/// memory. Other code can add lines with <see cref="SetStat"/>. It also prints a [perf] line
/// to stdout (browser console on web) every few seconds, and keeps a resettable "bench"
/// accumulator for scripted measurements.
///
/// Timing sources, all measured with Stopwatch in C#:
///   cpu frame   from the first callback of the frame (FrameProbe, lowest priority) to the
///               RenderingServer "frame_post_draw" signal, so scripts + engine process +
///               render submission. Idle/vsync wait is excluded.
///   render      from this overlay's _Process (the last process callback) to frame_post_draw:
///               mostly RenderingServer::draw, i.e. building and submitting GL/WebGL calls.
///   engine max  Performance TIME_PROCESS. Note: the engine stores the MAXIMUM process time of
///               the last ~1 s window (main.cpp, process_max), and that span includes
///               RenderingServer::draw. It is a worst-case figure, not a per-frame average.
/// </summary>
public partial class DebugOverlay : CanvasLayer
{
    public static DebugOverlay Instance { get; private set; }

    private const double DisplayInterval = 0.25;
    private const double LogInterval = 5.0;

    private static readonly SortedDictionary<string, string> Extra = new();

    private PanelContainer _panel;
    private Label _label;
    private readonly StringBuilder _sb = new();

    private Window _window;
    private Window _bench = new();
    private double _sinceDisplay;
    private double _sinceLog;

    private struct Window
    {
        public int Frames;
        public double Time;
        public double MaxFrame;
        public double CsProcess;
        public double CsPhysics;
        public int PhysicsSteps;
        public double EngineProcess;
        public double CpuFrame;
        public double MaxCpuFrame;
        public double Render;
        public int CpuFrames;

        public void AddCpu(double cpuMs, double renderMs)
        {
            CpuFrames++;
            CpuFrame += cpuMs;
            Render += renderMs;
            if (cpuMs > MaxCpuFrame) MaxCpuFrame = cpuMs;
        }

        public void Add(double dt, double proc, double phys, int steps, double engineProc)
        {
            if (engineProc > EngineProcess) EngineProcess = engineProc; // already a 1 s max
            Frames++;
            Time += dt;
            if (dt > MaxFrame) MaxFrame = dt;
            CsProcess += proc;
            CsPhysics += phys;
            PhysicsSteps += steps;
        }

        public double Fps => Time > 0 ? Frames / Time : 0;
        public double AvgFrameMs => Frames > 0 ? Time * 1000.0 / Frames : 0;
        public double AvgProcMs => Frames > 0 ? CsProcess / Frames : 0;
        public double AvgPhysMs => Frames > 0 ? CsPhysics / Frames : 0;
        public double EngineMaxMs => EngineProcess;
        public double AvgCpuMs => CpuFrames > 0 ? CpuFrame / CpuFrames : 0;
        public double AvgRenderMs => CpuFrames > 0 ? Render / CpuFrames : 0;
    }

    /// <summary>Adds or updates an extra line in the overlay ("key: value").</summary>
    public static void SetStat(string key, string value) => Extra[key] = value;

    public static void ResetBench() => Instance._bench = new Window();

    /// <summary>One-line summary of everything since the last <see cref="ResetBench"/>.</summary>
    public static string BenchSummary() => Instance.Summarize(Instance._bench);

    public override void _Ready()
    {
        Instance = this;
        Layer = 100;
        ProcessMode = ProcessModeEnum.Always;
        // Run after every other node so this frame's C# work is already counted.
        ProcessPriority = int.MaxValue;

        _panel = new PanelContainer { Position = new Vector2(8, 8), MouseFilter = Control.MouseFilterEnum.Ignore };
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0, 0, 0, 0.6f),
            ContentMarginLeft = 8, ContentMarginRight = 8, ContentMarginTop = 6, ContentMarginBottom = 6,
        };
        _panel.AddThemeStyleboxOverride("panel", style);
        _label = new Label { MouseFilter = Control.MouseFilterEnum.Ignore };
        _label.AddThemeFontSizeOverride("font_size", 14);
        _label.AddThemeColorOverride("font_color", new Color(0.6f, 1f, 0.6f));
        _panel.AddChild(_label);
        AddChild(_panel);

        _probe = new FrameProbe { Name = "FrameProbe" };
        AddChild(_probe);
        // RenderingServer from C# works on the web template since engine commit 11875322e9
        // (step 7e). Keep the guard so the overlay still works on older templates.
        try
        {
            RenderingServer.FramePostDraw += OnPostDraw;
            _postDrawOk = true;
        }
        catch (Exception ex)
        {
            GD.PrintErr("DebugOverlay: frame_post_draw unavailable: ", ex.Message);
        }
    }

    public override void _ExitTree()
    {
        if (_postDrawOk) RenderingServer.FramePostDraw -= OnPostDraw;
    }

    private FrameProbe _probe;
    private bool _postDrawOk;
    private long _processEnd;

    /// <summary>Records the start of each frame: the first process or physics callback.</summary>
    private partial class FrameProbe : Node
    {
        public long FrameStart;
        private ulong _frame = ulong.MaxValue;

        public override void _Ready()
        {
            ProcessMode = ProcessModeEnum.Always;
            ProcessPriority = int.MinValue;
            ProcessPhysicsPriority = int.MinValue;
        }

        private void Mark()
        {
            ulong f = Engine.GetProcessFrames();
            if (f == _frame) return;
            _frame = f;
            FrameStart = Perf.Now;
        }

        public override void _PhysicsProcess(double delta) => Mark();
        public override void _Process(double delta) => Mark();
    }

    private void OnPostDraw()
    {
        long now = Perf.Now;
        if (_processEnd == 0 || _probe.FrameStart == 0) return;
        double cpu = Perf.TicksToMs(now - _probe.FrameStart);
        double render = Perf.TicksToMs(now - _processEnd);
        _window.AddCpu(cpu, render);
        _bench.AddCpu(cpu, render);
    }

    public override void _Input(InputEvent e)
    {
        if (e is InputEventKey { Pressed: true, Echo: false } k && k.PhysicalKeycode == Key.F3)
        {
            _panel.Visible = !_panel.Visible;
            GetViewport().SetInputAsHandled();
        }
    }

    public override void _Process(double delta)
    {
        Perf.Drain(out double proc, out double phys, out int steps);
        // Engine-measured time of the previous frame's process step (includes native->C# call overhead).
        double engineProc = Performance.GetMonitor(Performance.Monitor.TimeProcess) * 1000.0;
        _window.Add(delta, proc, phys, steps, engineProc);
        _bench.Add(delta, proc, phys, steps, engineProc);
        _sinceDisplay += delta;
        _sinceLog += delta;

        if (_sinceDisplay >= DisplayInterval)
        {
            if (_panel.Visible)
                _label.Text = BuildText(_window);
            _window = new Window();
            _sinceDisplay = 0;
        }

        if (_sinceLog >= LogInterval)
        {
            GD.Print("[perf] ", Summarize(_bench));
            _sinceLog = 0;
        }
        _processEnd = Perf.Now;
    }

    private string BuildText(in Window w)
    {
        _sb.Clear();
        _sb.Append($"FPS {w.Fps:0.0}   frame {w.AvgFrameMs:0.00} ms (max {w.MaxFrame * 1000:0.0})\n");
        _sb.Append($"C# _Process {w.AvgProcMs:0.000} ms/f   _Physics {w.AvgPhysMs:0.000} ms/f ({w.PhysicsSteps} steps)\n");
        if (_postDrawOk)
            _sb.Append($"cpu frame {w.AvgCpuMs:0.00} ms (max {w.MaxCpuFrame:0.0})   render submit {w.AvgRenderMs:0.00} ms\n");
        _sb.Append($"engine process max/1s {w.EngineMaxMs:0.00} ms   physics max/1s {Performance.GetMonitor(Performance.Monitor.TimePhysicsProcess) * 1000:0.00} ms\n");
        _sb.Append($"nodes {Performance.GetMonitor(Performance.Monitor.ObjectNodeCount):0}   objects {Performance.GetMonitor(Performance.Monitor.ObjectCount):0}   draw calls {Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame):0}\n");
        _sb.Append($"GC gen0 {GC.CollectionCount(0)}  gen1 {GC.CollectionCount(1)}  gen2 {GC.CollectionCount(2)}   managed {GC.GetTotalMemory(false) / (1024.0 * 1024.0):0.00} MiB\n");
        foreach (var kv in Extra)
            _sb.Append(kv.Key).Append(": ").Append(kv.Value).Append('\n');
        _sb.Append("[F3] overlay  [F4] +50 enemies  [F6] +50 node dummies  [F5] clear");
        return _sb.ToString();
    }

    private string Summarize(in Window w) =>
        $"fps={w.Fps:0.0} frame_ms={w.AvgFrameMs:0.00} max_ms={w.MaxFrame * 1000:0.0} " +
        $"cs_process_ms={w.AvgProcMs:0.000} cs_physics_ms={w.AvgPhysMs:0.000} cpu_frame_ms={w.AvgCpuMs:0.00} cpu_frame_max_ms={w.MaxCpuFrame:0.0} " +
        $"render_ms={w.AvgRenderMs:0.00} engine_process_max_ms={w.EngineMaxMs:0.00} frames={w.Frames} " +
        $"draw_calls={Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame):0} " +
        $"nodes={Performance.GetMonitor(Performance.Monitor.ObjectNodeCount):0} " +
        $"gc={GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)} " +
        $"managed_mib={GC.GetTotalMemory(false) / (1024.0 * 1024.0):0.00}";
}
