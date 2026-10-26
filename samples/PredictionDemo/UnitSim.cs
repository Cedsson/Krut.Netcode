using System.Numerics;
using System.Runtime.InteropServices;

namespace PredictionDemo;

[StructLayout(LayoutKind.Sequential)]
public struct UnitState
{
    public float X, Y, Vx, Vy;

    public readonly Vector2 Pos => new(X, Y);
}

public struct UnitInput
{
    public float Ax, Ay;
}

/// <summary>The game rules: one pure function that client prediction, server simulation and replay all share.</summary>
public static class UnitSim
{
    public const int TickRate = 60;
    private const float Dt = 1f / TickRate;
    private const float Drag = 0.6f;

    public static UnitState Spawn(int slot) => new() { X = slot == 0 ? -10f : 10f };

    // The same state and input must give bit-identical output everywhere, or replay drifts.
    public static UnitState Step(UnitState s, int tick, UnitInput input)
    {
        float vx = (s.Vx + input.Ax * Dt) * (1f - Drag * Dt);
        float vy = (s.Vy + input.Ay * Dt) * (1f - Drag * Dt);
        return new UnitState { X = s.X + vx * Dt, Y = s.Y + vy * Dt, Vx = vx, Vy = vy };
    }

    // A pure function of (slot, tick): both units steer in slow circles with a pulsing throttle.
    public static UnitInput Script(int slot, int tick)
    {
        double t = tick * (double)Dt;
        double angle = t * 1.1 + slot * Math.PI;
        double amplitude = 25.0 * (0.5 + 0.5 * Math.Sin(t * 0.37));
        return new UnitInput { Ax = (float)(Math.Cos(angle) * amplitude), Ay = (float)(Math.Sin(angle) * amplitude) };
    }

    public static float Distance(UnitState a, UnitState b) => Vector2.Distance(a.Pos, b.Pos);
}
