using AdTrim.Encoders;
using AdTrim.Models;
using AdTrim.Services;
using Xunit;

namespace AdTrim.Tests;

public class HardwareEncoderTests
{
    [Theory]
    [InlineData(0x10de, "h264_nvenc")]
    [InlineData(0x8086, "h264_qsv")]
    [InlineData(0x1002, "h264_amf")]
    public void HardwareChangesEncodingButPreservesAccurateCutAndAudio(int vendor, string codec)
    {
        var adapter = new HardwareAdapter(3, "Test GPU", (uint)vendor, 1, "adapter", "driver");
        var segment = new ExportSegment(1, 12_345_678, 16_345_678, "Opening");
        var software = new LibX264EncoderStrategy().BuildSegmentArgs("source.mp4", segment, 2, "output.mp4").ToList();
        var hardware = new HardwareEncoderStrategy(adapter).BuildSegmentArgs("source.mp4", segment, 2, "output.mp4").ToList();
        Assert.Equal("10.345678", hardware[hardware.IndexOf("-ss") + 1]);
        Assert.Equal("2.000000", hardware[hardware.LastIndexOf("-ss") + 1]);
        Assert.True(hardware.IndexOf("-ss") < hardware.IndexOf("-i"));
        Assert.True(hardware.LastIndexOf("-ss") > hardware.IndexOf("-i"));
        foreach (var key in new[] { "-t", "-map_chapters", "-c:a", "-avoid_negative_ts" })
            Assert.Equal(software[software.IndexOf(key) + 1], hardware[hardware.IndexOf(key) + 1]);
        Assert.Contains("0:2", hardware);
        Assert.Contains("d3d11va=adtrim:3", hardware);
        Assert.Contains(codec, hardware);
        Assert.StartsWith("bwdif=mode=send_frame:parity=auto:deint=all", hardware[hardware.IndexOf("-vf") + 1]);
    }

    [Theory]
    [InlineData("Device creation failed: -542398533", true)]
    [InlineData("No NVENC capable devices found", true)]
    [InlineData("AMF_NO_DEVICE", true)]
    [InlineData("MFX_ERR_DEVICE_LOST", true)]
    [InlineData("Unknown encoder 'h264_nvenc'", true)]
    [InlineData("Unknown encoder 'h264_qsv'", true)]
    [InlineData("Unknown encoder 'h264_amf'", true)]
    [InlineData("Cannot load amfrt64.dll", true)]
    [InlineData("Error initializing output stream", false)]
    [InlineData("No space left on device", false)]
    [InlineData("AMF_FAIL: I/O error", false)]
    [InlineData("Invalid data found when processing input", false)]
    public void FallbackIsLimitedToIdentifiableHardwareFailures(string error, bool expected)
        => Assert.Equal(expected, HardwareExportException.IsDeviceFailure(error));

    [Fact]
    public void SavedAdapterChoiceSurvivesRebootButNotAnAdapterReplacement()
    {
        var adapter = new HardwareAdapter(0, "GPU", 0x1002, 1, "old-session", "driver");
        Assert.Equal(adapter.Id, (adapter with { Luid = "new-session" }).Id);
        Assert.NotEqual(adapter.Id, (adapter with { DeviceId = 2 }).Id);
        Assert.NotEqual(adapter.Id, (adapter with { Index = 1 }).Id);
    }

    [Fact]
    public void AutomaticIgnoresUnavailableAdaptersAndUsesMeasuredOrder()
    {
        var a = new HardwareAdapter(0, "First", 0x1002, 1, "1", "1");
        var b = a with { Index = 1, Name = "Second", Luid = "2" };
        var c = a with { Index = 2, Name = "Unavailable", Luid = "3" };
        var result = new EncoderDetection(new[] { new EncoderCapability(a, true, 200, ""),
            new EncoderCapability(b, true, 100, ""), new EncoderCapability(c, false, 0, "") });
        Assert.Equal(b, result.Preferred!.Adapter);
        Assert.Null((result with { SoftwareMilliseconds = 50 }).Preferred);
        Assert.Null(new EncoderDetection(new[] { new EncoderCapability(c, false, 0, "") }).Preferred);
    }
}
