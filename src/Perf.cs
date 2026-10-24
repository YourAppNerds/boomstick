using System.Diagnostics;

namespace Boomstick;

/// <summary>
/// Accumulates wall-clock time spent inside C# _Process and _PhysicsProcess bodies.
/// Usage: <c>long t0 = Perf.Now; ...work...; Perf.AddProcess(t0);</c>
/// The debug overlay drains the counters once per rendered frame.
/// </summary>
public static class Perf
{
    private static long _processTicks;
    private static long _physicsTicks;
    private static int _physicsSteps;

    public static long Now => Stopwatch.GetTimestamp();

    public static void AddProcess(long start) => _processTicks += Stopwatch.GetTimestamp() - start;

    public static void AddPhysics(long start) => _physicsTicks += Stopwatch.GetTimestamp() - start;

    /// <summary>Call once per physics step from one node (the player does it) to count steps.</summary>
    public static void CountPhysicsStep() => _physicsSteps++;

    /// <summary>Returns the accumulated milliseconds since the last call and resets the counters.</summary>
    public static void Drain(out double processMs, out double physicsMs, out int physicsSteps)
    {
        processMs = TicksToMs(_processTicks);
        physicsMs = TicksToMs(_physicsTicks);
        physicsSteps = _physicsSteps;
        _processTicks = 0;
        _physicsTicks = 0;
        _physicsSteps = 0;
    }

    public static double TicksToMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
}
