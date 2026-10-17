using System;
using Mirror;
using UnityEngine;

/// <summary>
/// Low-latency network mode (`-coopLowLatency`).
///
/// The joiner renders poses on Mirror's buffered timeline (<c>NetworkTime.unscaledTime</c>), which
/// sits <c>bufferTime = sendInterval * bufferTimeMultiplier</c> behind the host's newest pose, and
/// Mirror raises that multiplier again whenever delivery jitter grows. That buffer is the single
/// largest term in "press a key, the character reacts noticeably later".
///
/// This mode trades jitter tolerance for latency, deliberately and only when asked for:
///   * Mirror's dynamic adjustment is switched off and the multiplier is pinned to a low value
///     (<c>-coopBufferTime</c>, default 1.5 => ~25ms at a 60Hz snapshot rate);
///   * the local player's own pose renders closer to the newest sample, so your own movement
///     answers sooner than a remote player's would;
///   * poses extrapolate along their last sample pair for at most <see cref="MaxExtrapolation"/>
///     seconds instead of holding still, so a late packet does not look like a freeze.
///
/// Without the flag nothing in this file runs: the shipped default keeps Mirror's adaptive buffer
/// and the bounded-hold behaviour.
/// </summary>
public static class CoopLowLatency
{
    private static readonly string[] Arguments = Environment.GetCommandLineArgs();

    /// <summary>Opt-in switch; everything below is inert without it.</summary>
    public static readonly bool Enabled = Array.IndexOf(Arguments, "-coopLowLatency") >= 0;

    /// <summary>Interpolation buffer multiplier used in this mode (1.0 = one send interval).</summary>
    public static readonly double BufferMultiplier = Math.Max(1, Math.Min(6, ValueAfter("-coopBufferTime", 1.5)));

    /// <summary>How much of the interpolation buffer the local player's own pose skips (0..1).</summary>
    public static readonly double OwnerBoostFraction = Math.Max(0, Math.Min(1, ValueAfter("-coopOwnerBoost", 0.5)));

    /// <summary>Hard ceiling on extrapolation, so a stalled stream cannot drift forever.</summary>
    public const double MaxExtrapolation = 0.09;

    private static bool reported;

    private static double ValueAfter(string name, double fallback)
    {
        int index = Array.IndexOf(Arguments, name);
        if (index < 0 || index + 1 >= Arguments.Length) return fallback;
        return double.TryParse(Arguments[index + 1], System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double value) &&
            !double.IsNaN(value) && !double.IsInfinity(value) && value >= 0 ? value : fallback;
    }

    /// <summary>Client only. Pins Mirror's buffer so latency cannot grow with jitter.</summary>
    public static void Pin()
    {
        if (!Enabled || NetworkServer.active || !NetworkClient.active) return;
        NetworkClient.snapshotSettings.dynamicAdjustment = false;
        NetworkClient.snapshotSettings.bufferTimeMultiplier = BufferMultiplier;
        if (NetworkClient.bufferTimeMultiplier > BufferMultiplier) NetworkClient.bufferTimeMultiplier = BufferMultiplier;
        if (!reported)
        {
            reported = true;
            Debug.Log("[CoopLatency] low-latency mode: bufferTimeMultiplier=" + NetworkClient.bufferTimeMultiplier.ToString("F2") +
                "; bufferTimeMs=" + (NetworkClient.bufferTime * 1000.0).ToString("F1") +
                "; ownerBoost=" + OwnerBoostFraction.ToString("F2") + "; maxExtrapolationMs=" + (MaxExtrapolation * 1000.0).ToString("F0"));
        }
    }

    /// <summary>Render-clock advance for the local player's own pose, in seconds.</summary>
    public static double OwnerBoost()
    {
        if (!Enabled || NetworkServer.active || !NetworkClient.active) return 0;
        return NetworkClient.bufferTime * OwnerBoostFraction;
    }

    /// <summary>
    /// Extends the last sample pair when the render clock has run past the newest sample. Bounded by
    /// <see cref="MaxExtrapolation"/>; returns false when nothing had to be extrapolated.
    /// </summary>
    public static bool Extrapolate(ref Vector3 position, ref Quaternion rotation, double renderTime,
        TransformSnapshot from, TransformSnapshot to, out double aheadSeconds)
    {
        aheadSeconds = 0;
        if (!Enabled) return false;
        return ExtrapolateSamples(ref position, ref rotation, renderTime, from, to, out aheadSeconds);
    }

    // The runtime regression exercises this calculation even in default mode.
    internal static bool ExtrapolateSamples(ref Vector3 position, ref Quaternion rotation, double renderTime,
        TransformSnapshot from, TransformSnapshot to, out double aheadSeconds)
    {
        aheadSeconds = 0;
        double segment = to.remoteTime - from.remoteTime;
        if (renderTime <= to.remoteTime || segment <= 1e-6) return false;
        aheadSeconds = Math.Min(renderTime - to.remoteTime, MaxExtrapolation);
        float k = (float)(aheadSeconds / segment);
        position = to.position + (to.position - from.position) * k;
        Quaternion delta = to.rotation * Quaternion.Inverse(from.rotation);
        rotation = Quaternion.SlerpUnclamped(to.rotation, to.rotation * delta, Mathf.Clamp01(k));
        return true;
    }
}
