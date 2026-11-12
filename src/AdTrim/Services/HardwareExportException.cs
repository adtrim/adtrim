namespace AdTrim.Services;

public sealed class HardwareExportException(string message) : Exception(message)
{
    public static bool IsDeviceFailure(string stderr)
    {
        bool Has(params string[] texts) => texts.Any(s => stderr.Contains(s, StringComparison.OrdinalIgnoreCase));
        // A full disk, a missing file or a refused write is never the graphics card.
        if (Has("No space left", "Permission denied", "No such file or directory")) return false;
        // These name the device or its driver and nothing else. FFmpeg also logs "Invalid data found"
        // for every damaged frame of a broadcast recording, which must not hide them.
        if (Has("No capable devices found", "No NVENC capable devices", "Cannot load nvcuda", "Cannot load nvEncodeAPI",
            "minimum required Nvidia driver", "DXGI_ERROR_DEVICE_REMOVED", "DXGI_ERROR_DEVICE_RESET", "AMF_NO_DEVICE",
            "MFX_ERR_DEVICE_FAILED", "MFX_ERR_DEVICE_LOST", "Device creation failed", "No device available for encoder",
            "Unknown encoder 'h264_nvenc'", "Unknown encoder 'h264_qsv'", "Unknown encoder 'h264_amf'", "Cannot load amfrt64"))
            return true;
        // Generic encoder errors also occur for bad inputs.
        if (Has("Invalid data found", "I/O error")) return false;
        return Has("Driver does not support", "OpenEncodeSessionEx failed", "AMF_NOT_SUPPORTED",
            "AMF_FAIL", "CreateComponent failed", "Failed to initialise AMF", "Failed to initialize AMF",
            "Error creating a MFX session", "Error initializing an MFX session", "Error initializing an internal MFX session");
    }
}
