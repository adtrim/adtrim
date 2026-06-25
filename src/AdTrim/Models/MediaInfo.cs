namespace AdTrim.Models;

public sealed record Rational(int Numerator, int Denominator)
{
    public double AsDouble => Denominator == 0 ? 0 : (double)Numerator / Denominator;

    /// <summary>Parse "30000/1001" or "29.97".</summary>
    public static Rational Parse(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return new Rational(0, 1);
        var slash = s.IndexOf('/');
        if (slash > 0
            && int.TryParse(s[..slash], out var n)
            && int.TryParse(s[(slash + 1)..], out var d))
            return new Rational(n, d);
        if (double.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out var f))
            return new Rational((int)Math.Round(f * 1000), 1000);
        return new Rational(0, 1);
    }
}

public sealed record AudioStream(int Index, string Codec, int Channels, bool Default);

public sealed record MediaInfo(
    long DurationUs,
    string VideoCodec,
    int Width,
    int Height,
    Rational FrameRate,
    IReadOnlyList<AudioStream> AudioStreams,
    int PrimaryAudioIndex,
    long VideoStartTimeUs = 0)
{
    public AudioStream? PrimaryAudio =>
        AudioStreams.FirstOrDefault(a => a.Index == PrimaryAudioIndex);
}
