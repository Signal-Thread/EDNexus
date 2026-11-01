namespace EDNexus.Core.Ship;

/// <summary>
/// FSD Injection synthesis: a one-shot multiplier on the <i>next</i> jump's range. The journal records
/// it as a <c>Synthesis</c> event named for the grade, and nothing marks the boost as consumed except
/// the following <c>FSDJump</c>.
/// </summary>
public static class FsdInjection
{
    /// <summary>The range multiplier an injection recipe gives the next jump, or null if <paramref name="synthesisName"/> isn't one.</summary>
    public static double? MultiplierFor(string? synthesisName) => synthesisName?.Trim().ToLowerInvariant() switch
    {
        "fsd basic" => 1.25,
        "fsd standard" => 1.5,
        "fsd premium" => 2.0,
        _ => null,
    };
}
