using System;

// Connection-scoped token bucket. Completing a snapshot must not mint new credit.
// Pure clock/byte arithmetic so bandwidth and frame-rate behavior can be tested offline.
public sealed class CoopSnapshotPacer
{
    public const int BurstBytes = 4000 + 128;
    public double Credit { get; private set; }
    public double Scale { get; private set; } = 1;
    private double updated, nextDecrease;

    public CoopSnapshotPacer(double now)
    {
        updated = now;
        Credit = BurstBytes;
    }

    public void Advance(double now, double budget, double queueMs)
    {
        double dt = Math.Max(0, now - updated);
        updated = now;
        // At most one multiplicative decrease per 500ms congestion window. Applying
        // it every render frame drove 60fps hosts to the floor in four frames.
        if (queueMs > 500 && now >= nextDecrease)
        {
            Scale = Math.Max(0.2, Scale * 0.6);
            nextDecrease = now + 0.5;
        }
        else if (queueMs < 120) Scale = Math.Min(1, Scale + dt * 0.25);
        double rate = Math.Max(0, budget) * Scale;
        Credit = Math.Min(Math.Max(BurstBytes, rate / 30), Credit + dt * rate);
    }

    public bool TrySpend(int bytes)
    {
        if (bytes <= 0 || Credit < bytes) return false;
        Credit -= bytes;
        return true;
    }
}
