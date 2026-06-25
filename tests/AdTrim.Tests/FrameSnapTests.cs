using AwesomeAssertions;
using AdTrim.Models;
using AdTrim.Services;
using Xunit;

namespace AdTrim.Tests;

public class FrameSnapTests
{
    // 30000/1001 is the canonical 29.97 fps US OTA rate - one frame is
    // ~33366.7µs. Snapping any time in that frame's interval should land
    // on the start of the frame.
    [Fact]
    public void Snap_AtFrameBoundary_IsUnchanged()
    {
        var fps = new Rational(30000, 1001);
        // Frame 0 starts at 0us - the formula returns 0 for t=0.
        FrameSnap.Snap(0, fps).Should().Be(0);
    }

    [Fact]
    public void Snap_BetweenFrames_RoundsToNearest()
    {
        var fps = new Rational(30000, 1001);
        // Frame duration = 1_001_000us / 30 ≈ 33366.67us
        // Halfway between frame 0 (0us) and frame 1 (~33367us) is ~16683us,
        // which should round to frame 1.
        var oneFrameUs = 33367L;
        FrameSnap.Snap(oneFrameUs - 1, fps).Should().BeCloseTo(oneFrameUs, 1);
    }

    [Fact]
    public void Snap_With25fpsExact_IsAtMultipleOf40000()
    {
        var fps = new Rational(25, 1);   // 25 fps exact, 40_000µs per frame
        // Frame boundaries at 0, 40_000, 80_000, 120_000, ...; midpoint between
        // frames 2 and 3 is 100_000.
        FrameSnap.Snap(80_000, fps).Should().Be(80_000);
        FrameSnap.Snap(99_999, fps).Should().Be(80_000);   // just below midpoint → frame 2
        FrameSnap.Snap(100_001, fps).Should().Be(120_000); // just above midpoint → frame 3
    }

    [Fact]
    public void Snap_NonSensicalFrameRate_PassesThrough()
    {
        var fps = new Rational(0, 0);  // pathological
        FrameSnap.Snap(123_456, fps).Should().Be(123_456);
    }

    [Theory]
    [InlineData(50L, 0L, 100L, 50L)]
    [InlineData(-5L, 0L, 100L, 0L)]
    [InlineData(150L, 0L, 100L, 100L)]
    [InlineData(0L, 10L, 20L, 10L)]
    public void Clamp_BoundsAreInclusive(long t, long min, long max, long expected)
    {
        FrameSnap.Clamp(t, min, max).Should().Be(expected);
    }

    [Fact]
    public void Snap_WithPhase_AlignsToShiftedGrid()
    {
        // 25 fps exact, phase = 5 ms. Frames at 5_000, 45_000, 85_000, ...
        var fps = new Rational(25, 1);
        const long phaseUs = 5_000L;
        FrameSnap.Snap(5_000, fps, phaseUs).Should().Be(5_000);
        FrameSnap.Snap(45_000, fps, phaseUs).Should().Be(45_000);
        // Halfway between phase-frame 1 and 2 = 25_000. Should round to either
        // boundary; with banker's rounding (Math.Round default = ToEven) it
        // lands on the even-index frame.
        FrameSnap.Snap(46_000, fps, phaseUs).Should().Be(45_000);
        FrameSnap.Snap(64_000, fps, phaseUs).Should().Be(45_000);   // closer to frame at 45
        FrameSnap.Snap(66_000, fps, phaseUs).Should().Be(85_000);   // closer to frame at 85
    }

    [Fact]
    public void Snap_BBTLikePhase_LandsOnRealFrame()
    {
        // BBT fixture: r_frame_rate=60000/1001, video start_time=1.827.
        // Real frames are at 1_827_000 + N * (1_000_000 * 1001/60_000) µs.
        // Frame 15591 = 1_827_000 + 15591 * 16683.333... = 261_936_850 µs ≈ 04:21.936.
        // Without phase, the old snap rounded 261_936_850 → 261_944_350 (between real frames).
        // With phase, it should round to itself.
        var fps = new Rational(60_000, 1_001);
        const long phase = 1_827_000L;
        FrameSnap.Snap(261_936_850, fps, phase).Should().BeCloseTo(261_936_850, 1);
    }
}
