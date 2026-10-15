using System;

/// <summary>
/// One-shot compatibility switches for the 2026-10-15 optimisation round. A report that a
/// joining player cannot move, or that attack effects look wrong, can be bisected without a
/// rebuild: run the same session with one switch at a time and see whether the symptom stops.
/// All four defaults are the optimised behaviour.
/// </summary>
public static class CoopCompat
{
    private static readonly string[] Arguments = Environment.GetCommandLineArgs();
    private static bool Has(string name) => Array.IndexOf(Arguments, name) >= 0;
    /// <summary>Send movement input at 30Hz instead of 60Hz.</summary>
    public static readonly bool Input30 = Has("-coopCompatInput30");
    /// <summary>Send character poses at 30Hz instead of frame rate.</summary>
    public static readonly bool Motion30 = Has("-coopCompatMotion30");
    /// <summary>Restore the pre-round behaviour of always re-decoding fields, appearances and
    /// re-capturing appearance payloads, even when the payload did not change.</summary>
    public static readonly bool FullApply = Has("-coopCompatFullApply");
    public static bool Any => Input30 || Motion30 || FullApply;
    public static string Describe() => (Input30 ? "input30 " : "") + (Motion30 ? "motion30 " : "") + (FullApply ? "fullApply" : "");
}
