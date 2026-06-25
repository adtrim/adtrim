namespace AdTrim.Models;

public enum Confidence
{
    /// <summary>Unrefined or manually placed - the default.</summary>
    Neutral,
    /// <summary>High-confidence refinement (top &gt; 0.6 AND margin &gt; 0.2).</summary>
    High,
    /// <summary>Medium-confidence refinement (top &gt; 0.4).</summary>
    Medium,
    /// <summary>Low-confidence refinement (top &gt; 0.0 but &lt; 0.4).</summary>
    Low,
    /// <summary>Refine found nothing actionable - marker stays put.</summary>
    Unchanged,
}
