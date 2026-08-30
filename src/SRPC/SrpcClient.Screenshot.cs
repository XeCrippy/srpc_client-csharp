using SRPC.Internal;

namespace SRPC;

public sealed partial class SrpcClient
{
    public RawScreenshot CaptureRawScreenshot(int maximumFramebufferSize = ScreenshotOptions.DefaultMaximumSize)
    {
        if (maximumFramebufferSize <= 0) throw new ProtocolException("Maximum screenshot framebuffer size must be greater than zero.");
        lock (_sync)
        {
            var response = SendCommandRawLocked("screenshot"); ProtocolCodec.ThrowIfCommandFailed(response);
            if (response.StatusCode == 202) ReadMultilineLocked();
            if (response.StatusCode != 203)
            {
                if (response.StatusCode is not (200 or 201 or 202)) { _connection.Close(); _selectedProtocol = _options.Protocol; }
                throw new ProtocolException($"screenshot returned status {response.StatusCode} instead of 203.");
            }
            _binaryTransferActive = true;
            try
            {
                var line = _connection.ReadLine();
                uint? Field(string name, bool required = false)
                {
                    uint? value = null;
                    foreach (var token in line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                    {
                        var separator = token.IndexOf('='); if (separator < 0 || token[..separator] != name) continue;
                        if (value.HasValue) throw new ProtocolException($"Screenshot metadata contains duplicate {name} fields.");
                        try { value = ProtocolCodec.ParseHexUInt32(token[(separator + 1)..].TrimEnd(',')); } catch (SrpcException) { throw new ProtocolException($"Screenshot metadata contains an invalid {name} field."); }
                    }
                    if (required && !value.HasValue) throw new ProtocolException($"Screenshot metadata is missing the {name} field.");
                    return value;
                }
                var width = Field("width", true)!.Value; var height = Field("height", true)!.Value;
                var metadata = new ScreenshotMetadata { PitchBytes = Field("pitch", true)!.Value, Width = width, Height = height,
                    FramebufferSize = Field("framebuffersize", true)!.Value, Format = Field("format"), DisplayWidth = Field("sw") ?? width,
                    DisplayHeight = Field("sh") ?? height, OffsetX = Field("offsetx") ?? 0, OffsetY = Field("offsety") ?? 0, ColorSpace = Field("colorspace") };
                if (metadata.PitchBytes == 0 || metadata.PitchBytes % 4 != 0 || width == 0 || height == 0 || metadata.PitchBytes / 4 < width) throw new ProtocolException("Invalid screenshot dimensions or pitch.");
                if (metadata.FramebufferSize == 0 || metadata.FramebufferSize % 4 != 0 || metadata.FramebufferSize > maximumFramebufferSize || metadata.FramebufferSize > int.MaxValue) throw new ProtocolException("Invalid or oversized screenshot framebuffer.");
                if (metadata.DisplayWidth == 0 || metadata.DisplayHeight == 0 || (metadata.OffsetX != 0 || metadata.OffsetY != 0) && ((ulong)metadata.OffsetX + width > metadata.DisplayWidth || (ulong)metadata.OffsetY + height > metadata.DisplayHeight)) throw new ProtocolException("Screenshot visible rectangle does not fit its display surface.");
                var bytes = _connection.ReadExact((int)metadata.FramebufferSize); _binaryTransferActive = false; return new RawScreenshot(metadata, bytes);
            }
            catch { if (_binaryTransferActive) AbandonBinaryLocked(); throw; }
        }
    }
    public ScreenshotImage CaptureScreenshot(ScreenshotOptions? options = null) { options ??= new ScreenshotOptions(); return ScreenshotCodec.Decode(CaptureRawScreenshot(options.MaximumFramebufferSize), options); }
    public void SaveScreenshot(string outputPath, ScreenshotOptions? options = null) { if (string.IsNullOrEmpty(outputPath)) throw new ProtocolException("Screenshot output path cannot be empty."); ImageCodec.WritePng(outputPath, CaptureScreenshot(options).View); }
}
