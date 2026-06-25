using AdTrim.Models;

namespace AdTrim.Services;

/// <summary>
/// Routine frame-snap math against <c>r_frame_rate</c>'s rational, with a
/// phase offset for files whose first video frame doesn't sit at time 0.
/// This is the authoritative snap for
/// marker drag and manual placement - no FFmpeg call needed. Refinement
/// and audition boundary marking still use ffprobe.
///
/// <para><b>Why the phase parameter exists:</b> Plex DVR `.ts` captures
/// usually start mid-GOP, so the autoconverter-produced MP4 has the
/// video stream's first frame at some non-zero PTS (e.g. 1.827 s on the
/// BBT fixture). mpv reports playback positions including this phase, so
/// snapping with a 0-based grid lands the marker between mpv's actual
/// frames. With phase honored, frame N sits at
/// <c>phaseUs + N * (1_000_000 * den / num)</c>.</para>
/// </summary>
public static class FrameSnap
{
    public static long Snap(long timeUs, Rational frameRate, long phaseUs = 0)
    {
        if (frameRate.Numerator <= 0 || frameRate.Denominator <= 0) return timeUs;
        double num = frameRate.Numerator;
        double den = frameRate.Denominator;
        var adjusted = timeUs - phaseUs;
        var frameIndex = Math.Round(adjusted * num / (1_000_000.0 * den));
        return phaseUs + (long)Math.Round(frameIndex * (1_000_000.0 * den) / num);
    }

    /// <summary>Clamp to [minUs, maxUs], inclusive.</summary>
    public static long Clamp(long timeUs, long minUs, long maxUs)
        => Math.Max(minUs, Math.Min(maxUs, timeUs));
}
