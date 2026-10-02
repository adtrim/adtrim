namespace AdTrim.Services;

public sealed class HardwareExportException(string message) : Exception(message)
{
    public static bool IsDeviceFailure(string stderr)
    {
        // Generic encoder errors also occur for bad inputs and full disks.
        if (new[] { "No space left", "Permission denied", "Invalid data found", "No such file or directory", "I/O error" }
            .Any(s => stderr.Contains(s, StringComparison.OrdinalIgnoreCase))) return false;
        return new[] { "No capable devices found", "No NVENC capable devices", "Cannot load nvcuda", "Cannot load nvEncodeAPI",
            "Driver does not support", "minimum required Nvidia driver", "OpenEncodeSessionEx failed",
            "DXGI_ERROR_DEVICE_REMOVED", "DXGI_ERROR_DEVICE_RESET", "AMF_NO_DEVICE", "AMF_NOT_SUPPORTED",
            "AMF_FAIL", "CreateComponent failed", "Failed to initialise AMF", "Failed to initialize AMF",
            "Error creating a MFX session", "Error initializing an MFX session", "Error initializing an internal MFX session", "MFX_ERR_DEVICE_FAILED",
            "MFX_ERR_DEVICE_LOST", "Device creation failed", "No device available for encoder",
            "Unknown encoder 'h264_nvenc'", "Unknown encoder 'h264_qsv'", "Unknown encoder 'h264_amf'",
            "Cannot load amfrt64" }
            .Any(s => stderr.Contains(s, StringComparison.OrdinalIgnoreCase));
    }
}
