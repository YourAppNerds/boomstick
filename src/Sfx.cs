using System;
using Godot;

namespace Boomstick;

public enum Snd
{
    Pistol, Shotgun, Pump, RocketFire, Explosion, Fireball, Plasma, ProjHit, Alert, Pain, Death,
    Swipe, PlayerHurt, PlayerDeath, Item, WeaponPickup, Click, LevelDone, Footstep, Count,
}

/// <summary>
/// Procedurally synthesized sound effects (generated once into 16-bit AudioStreamWAVs) played
/// through a small pool of AudioStreamPlayers. Positional sounds are attenuated by distance to
/// the player in C#; there is no AudioStreamPlayer3D per entity.
///
/// Every sound has a mix level (<see cref="Mix"/>) and a little random pitch, so repeated shots
/// and growls don't sound identical. At most <see cref="MaxSame"/> copies of one sound play at
/// once. On the web nothing plays until the first key, click or tap: browsers refuse to start
/// audio before a user gesture and log a warning for every attempt.
/// </summary>
public partial class Sfx : Node
{
    public static Sfx Instance { get; private set; }

    private const int Rate = 22050;
    private const int Voices = 12;

    private static AudioStreamWav[] _streams;
    private readonly AudioStreamPlayer[] _players = new AudioStreamPlayer[Voices];
    private readonly ulong[] _started = new ulong[Voices];
    private int _next;

    /// <summary>Mix level in dB per sound, applied on top of the caller's volume.</summary>
    private static readonly float[] Mix = new float[(int)Snd.Count];

    /// <summary>Random pitch range per sound (0 = always the same pitch, for jingles).</summary>
    private static readonly float[] Jitter = new float[(int)Snd.Count];

    private const int MaxSame = 3;

    /// <summary>False on the web until the first user gesture; always true on desktop.</summary>
    public static bool Unlocked { get; private set; } = !OS.HasFeature("web");

    static Sfx()
    {
        void Set(Snd s, float db, float jitter) { Mix[(int)s] = db; Jitter[(int)s] = jitter; }
        Set(Snd.Pistol, -3f, 0.05f);
        Set(Snd.Shotgun, 0f, 0.04f);
        Set(Snd.Pump, -5f, 0.03f);
        Set(Snd.RocketFire, -2f, 0.04f);
        Set(Snd.Explosion, 1f, 0.08f);
        Set(Snd.Fireball, -4f, 0.08f);
        Set(Snd.Plasma, -7f, 0.06f);
        Set(Snd.ProjHit, -5f, 0.1f);
        Set(Snd.Alert, -3f, 0.06f);
        Set(Snd.Pain, -4f, 0.06f);
        Set(Snd.Death, -2f, 0.06f);
        Set(Snd.Swipe, -5f, 0.1f);
        Set(Snd.PlayerHurt, -3f, 0.05f);
        Set(Snd.PlayerDeath, 0f, 0f);
        Set(Snd.Item, -6f, 0f);
        Set(Snd.WeaponPickup, -4f, 0f);
        Set(Snd.Click, -6f, 0.05f);
        Set(Snd.LevelDone, -2f, 0f);
        Set(Snd.Footstep, -15f, 0.12f);
    }

    public override void _Ready()
    {
        Instance = this;
        _streams ??= BuildAll();
        for (int i = 0; i < Voices; i++)
        {
            _players[i] = new AudioStreamPlayer { Name = $"Voice{i}" };
            AddChild(_players[i]);
        }
    }

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
    }

    /// <summary>
    /// Allows sound after a user gesture. The engine resumes the web AudioContext inside its key,
    /// mouse button and touch callbacks. Nodes that mark those events as handled (the mouse
    /// capture click, the touch controls) call this themselves, since the event never reaches us.
    /// </summary>
    public static void Unlock() => Unlocked = true;

    public override void _Input(InputEvent e)
    {
        if (!Unlocked && e is InputEventKey { Pressed: true } or InputEventMouseButton { Pressed: true } or InputEventScreenTouch { Pressed: true })
            Unlock();
    }

    /// <summary>Plays a sound at full volume (player weapons, pickups, UI).</summary>
    public static void Play(Snd s, float volumeDb = 0f, float pitch = 1f) => Instance?.PlayVoice(s, volumeDb, pitch);

    /// <summary>Plays a sound emitted at a world position, attenuated by distance to the listener.</summary>
    public static void PlayAt(Snd s, Vector3 pos, float pitch = 1f, float volumeDb = 0f)
    {
        var player = Game.Player;
        if (Instance == null || player == null) return;
        float d = player.GlobalPosition.DistanceTo(pos);
        float gain = 1f / (1f + d / 7f);
        if (gain < 0.06f) return;
        Instance.PlayVoice(s, volumeDb + Mathf.LinearToDb(gain), pitch);
    }

    private void PlayVoice(Snd s, float db, float pitch)
    {
        if (!Unlocked) return;
        var stream = _streams[(int)s];
        // Prefer a free voice; otherwise steal the oldest one. Skip the sound if it is already
        // playing MaxSame times (a horde waking up at once).
        int pick = -1, same = 0;
        ulong oldest = ulong.MaxValue;
        int oldestIdx = 0;
        for (int k = 0; k < Voices; k++)
        {
            int i = (_next + k) % Voices;
            var v = _players[i];
            if (!v.Playing) { if (pick < 0) pick = i; continue; }
            if (v.Stream == stream && ++same >= MaxSame) return;
            if (_started[i] < oldest) { oldest = _started[i]; oldestIdx = i; }
        }
        if (pick < 0) pick = oldestIdx;
        float j = Jitter[(int)s];
        if (j > 0f) pitch *= 1f + ((float)Game.Rng.NextDouble() * 2f - 1f) * j;
        db += Mix[(int)s];
        _next = (pick + 1) % Voices;
        var p = _players[pick];
        p.Stream = stream;
        p.VolumeDb = db;
        p.PitchScale = pitch;
        p.Play();
        _started[pick] = Time.GetTicksMsec();
    }

    // ---- Synthesis ------------------------------------------------------------------------------

    private static AudioStreamWav[] BuildAll()
    {
        var a = new AudioStreamWav[(int)Snd.Count];
        var rng = new Random(7);
        float Noise() => (float)rng.NextDouble() * 2f - 1f;

        a[(int)Snd.Pistol] = Synth(0.22f, (t, s) =>
            s.Lp(Noise(), 0.45f) * Env(t, 28f) * 0.9f + Sine(t, 140f - t * 300f) * Env(t, 22f) * 0.6f);
        a[(int)Snd.Shotgun] = Synth(0.6f, (t, s) =>
            s.Lp(Noise(), 0.25f) * Env(t, 7f) * 1.1f + Sine(t, 75f - t * 40f) * Env(t, 10f) * 0.9f);
        a[(int)Snd.Pump] = Synth(0.3f, (t, s) =>
            (Pulse(t, 0.02f, 0.012f) + Pulse(t, 0.17f, 0.015f)) * s.Lp(Noise(), 0.7f) * 0.8f);
        a[(int)Snd.RocketFire] = Synth(0.55f, (t, s) =>
            s.Lp(Noise(), 0.15f + t * 0.3f) * MathF.Min(1f, t * 30f) * Env(t, 4f) * 1.1f + Sine(t, 220f - t * 250f) * Env(t, 8f) * 0.4f);
        a[(int)Snd.Explosion] = Synth(1.1f, (t, s) =>
            s.Lp(Noise(), 0.08f) * Env(t, 3.5f) * 2.2f + Sine(t, 55f - t * 20f) * Env(t, 4f) * 0.7f);
        a[(int)Snd.Fireball] = Synth(0.4f, (t, s) =>
            s.Lp(Noise(), 0.3f) * Env(t, 6f) * 0.7f + Square(t, 320f - t * 400f) * Env(t, 9f) * 0.25f);
        a[(int)Snd.Plasma] = Synth(0.35f, (t, s) =>
            Square(t, 700f - t * 1200f + MathF.Sin(t * 120f) * 60f) * Env(t, 7f) * 0.35f);
        a[(int)Snd.ProjHit] = Synth(0.45f, (t, s) =>
            s.Lp(Noise(), 0.18f) * Env(t, 8f) * 1.2f);
        a[(int)Snd.Alert] = Synth(0.65f, (t, s) =>
            (Saw(t, 95f + MathF.Sin(t * 9f) * 15f) * 0.5f + s.Lp(Noise(), 0.2f) * 0.4f) * MathF.Min(1f, t * 12f) * Env(t, 3f) * (0.7f + 0.3f * MathF.Sin(t * 40f)));
        a[(int)Snd.Pain] = Synth(0.28f, (t, s) =>
            (Square(t, 420f - t * 800f) * 0.4f + Noise() * 0.2f) * Env(t, 9f));
        a[(int)Snd.Death] = Synth(0.9f, (t, s) =>
            (Saw(t, 280f - t * 250f) * 0.5f + s.Lp(Noise(), 0.25f) * 0.5f) * Env(t, 3f) * (0.75f + 0.25f * MathF.Sin(t * 50f)));
        a[(int)Snd.Swipe] = Synth(0.22f, (t, s) =>
            (Noise() - s.Lp(Noise(), 0.1f)) * MathF.Sin(MathF.Min(1f, t / 0.22f) * MathF.PI) * 0.6f);
        a[(int)Snd.PlayerHurt] = Synth(0.3f, (t, s) =>
            (Square(t, 230f - t * 300f) * 0.35f + Saw(t, 115f - t * 150f) * 0.3f) * Env(t, 8f));
        a[(int)Snd.PlayerDeath] = Synth(1.2f, (t, s) =>
            (Saw(t, 260f - t * 180f) * 0.4f + Square(t, 130f - t * 90f) * 0.25f) * Env(t, 2.2f));
        a[(int)Snd.Item] = Synth(0.14f, (t, s) =>
            Square(t, t < 0.06f ? 880f : 1320f) * 0.25f * Env(t, 10f));
        a[(int)Snd.WeaponPickup] = Synth(0.5f, (t, s) =>
            Square(t, Arp(t, 0.09f, 440f, 554f, 659f, 880f, 1100f)) * 0.22f * Env(t, 3f));
        a[(int)Snd.Click] = Synth(0.05f, (t, s) => Noise() * Env(t, 120f) * 0.5f);
        a[(int)Snd.LevelDone] = Synth(1.4f, (t, s) =>
            (Square(t, Arp(t, 0.14f, 523f, 659f, 784f, 1047f, 784f, 1047f, 1047f, 1047f, 1047f, 1047f)) * 0.18f +
             Sine(t, Arp(t, 0.14f, 262f, 330f, 392f, 523f, 392f, 523f, 523f, 523f, 523f, 523f)) * 0.25f) * Env(t, 1.6f));
        a[(int)Snd.Footstep] = Synth(0.12f, (t, s) =>
            s.Lp(Noise(), 0.12f) * Env(t, 38f) * 1.6f + Sine(t, 70f - t * 120f) * Env(t, 45f) * 0.6f);
        return a;
    }

    private sealed class State
    {
        private float _lp;

        /// <summary>One-pole low-pass filter; smaller k = darker.</summary>
        public float Lp(float x, float k) => _lp += (x - _lp) * Mathf.Clamp(k, 0.001f, 1f);
    }

    private static float Env(float t, float decay) => MathF.Exp(-t * decay);
    private static float Sine(float t, float f) => MathF.Sin(t * f * MathF.Tau);
    private static float Square(float t, float f) => (t * f) % 1f < 0.5f ? 1f : -1f;
    private static float Saw(float t, float f) => (t * f) % 1f * 2f - 1f;
    private static float Pulse(float t, float at, float len) => t >= at && t < at + len ? 1f : 0f;

    private static float Arp(float t, float step, params float[] notes) =>
        notes[Math.Min(notes.Length - 1, (int)(t / step))];

    private static AudioStreamWav Synth(float seconds, Func<float, State, float> f)
    {
        int n = (int)(seconds * Rate);
        var data = new byte[n * 2];
        var st = new State();
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Rate;
            float v = f(t, st);
            // Short fade-out to avoid a click at the end.
            float tail = (n - i) / (Rate * 0.01f);
            if (tail < 1f) v *= tail;
            // Headroom: several voices often overlap (shotgun + pain + death).
            short s = (short)(Mathf.Clamp(v * 0.5f, -1f, 1f) * 30000f);
            data[i * 2] = (byte)(s & 0xFF);
            data[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
        }
        return new AudioStreamWav
        {
            Format = AudioStreamWav.FormatEnum.Format16Bits,
            MixRate = Rate,
            Stereo = false,
            Data = data,
        };
    }
}
