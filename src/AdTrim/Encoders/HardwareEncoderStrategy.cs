using AdTrim.Models;
using AdTrim.Services;

namespace AdTrim.Encoders;

public sealed record HardwareAdapter(int Index, string Name, uint VendorId, uint DeviceId,
    string Luid, string DriverVersion)
{
    // LUID changes after reboot; keep it in the capability cache, not the saved preference.
    public string Id => $"{VendorId:x4}:{DeviceId:x4}:{Index}:{Name}";
    public string? Encoder => VendorId switch { 0x10de => "h264_nvenc", 0x8086 => "h264_qsv", 0x1002 => "h264_amf", _ => null };
}

public sealed class HardwareEncoderStrategy(HardwareAdapter adapter) : IEncoderStrategy
{
    public HardwareAdapter Adapter { get; } = adapter;
    public bool IsHardware => true;
    public string DisplayName => Adapter.Name + " (hardware H.264)";
    public void ValidateDevice()
    {
        try
        {
            if (!GraphicsAdapters.Enumerate().Contains(Adapter))
                throw new HardwareExportException("The selected GPU or its driver changed and is no longer available for this export.");
        }
        catch (System.Runtime.InteropServices.COMException ex)
        { throw new HardwareExportException("The selected adapter is unavailable: " + ex.Message); }
    }
    public IReadOnlyList<string> DeviceArgs => Adapter.Encoder == "h264_qsv"
        ? new[] { "-init_hw_device", $"d3d11va=adtrim:{Adapter.Index}", "-init_hw_device", "qsv=encode@adtrim", "-filter_hw_device", "encode" }
        : new[] { "-init_hw_device", $"d3d11va=adtrim:{Adapter.Index}", "-filter_hw_device", "adtrim" };
    public string Filter => "bwdif=mode=send_frame:parity=auto:deint=all,format=nv12,hwupload"
        + (Adapter.Encoder == "h264_qsv" ? "=extra_hw_frames=32" : "");
    public IReadOnlyList<string> EncoderArgs => Adapter.Encoder switch
    {
        "h264_nvenc" => new[] { "-c:v", "h264_nvenc", "-preset", "p5", "-tune", "hq", "-rc", "vbr", "-cq", "20", "-b:v", "0" },
        "h264_qsv" => new[] { "-c:v", "h264_qsv", "-preset", "medium", "-global_quality", "20", "-async_depth", "4" },
        "h264_amf" => new[] { "-c:v", "h264_amf", "-usage", "transcoding", "-quality", "quality", "-rc", "cqp",
            "-qp_i", "18", "-qp_p", "20", "-qp_b", "22", "-async_depth", "4" },
        _ => throw new NotSupportedException("This adapter has no supported H.264 encoder."),
    };

    public IReadOnlyList<string> BuildSegmentArgs(string sourcePath, ExportSegment segment,
        int primaryAudioStreamIndex, string outputPath)
        => LibX264EncoderStrategy.BuildArgs(sourcePath, segment, primaryAudioStreamIndex, outputPath,
            DeviceArgs, Filter, EncoderArgs);
}
