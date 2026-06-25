namespace AdTrim.Models;

/// <summary>Per-split refinement outcome.</summary>
public sealed record RefineResult(
    string SplitId,
    long OriginalTimeUs,
    long RefinedTimeUs,
    Confidence Confidence,
    double TopScore,
    double Margin,
    int TiedAtTop = 1)
{
    public long DeltaUs => RefinedTimeUs - OriginalTimeUs;
}

/// <summary>Aggregate refine-pass progress.</summary>
public sealed record RefineProgress(
    int CurrentIndex,
    int TotalCount,
    string Message);

/// <summary>Aggregate summary shown in the status bar after a refine pass.</summary>
public sealed record RefineSummary(
    int High,
    int Medium,
    int Low,
    int Unchanged,
    int Confirmed)
{
    public int TotalRefined => High + Medium + Low + Unchanged;
}
