using System.Text;

namespace AndroidDevMonitor.Core.Analysis;

/// <summary>Result of a pixel-by-pixel comparison between two BGRA frames of the same size.</summary>
public sealed record ScreenshotDiff(
    int Width,
    int Height,
    int ComparedPixels,
    int ChangedPixels,
    byte MaxChannelDelta,
    double MeanChannelDelta,
    int MinChangedX,
    int MinChangedY,
    int MaxChangedX,
    int MaxChangedY)
{
    public bool AnyChange => ChangedPixels > 0;

    public double ChangedPercent => ComparedPixels == 0 ? 0 : ChangedPixels * 100.0 / ComparedPixels;

    public string BoundingBox => AnyChange
        ? $"{MinChangedX},{MinChangedY} -> {MaxChangedX},{MaxChangedY}"
        : "none";

    public string ToReport()
    {
        StringBuilder report = new();
        report.AppendLine($"Frame size: {Width}x{Height} ({ComparedPixels:N0} pixels)");
        report.AppendLine($"Changed pixels: {ChangedPixels:N0} ({ChangedPercent:0.00}%)");
        report.AppendLine($"Max channel delta: {MaxChannelDelta} · mean delta: {MeanChannelDelta:0.00}");
        report.AppendLine($"Changed area: {BoundingBox}");
        return report.ToString();
    }
}

/// <summary>
/// Pure, allocation-free pixel comparison used by the screenshot comparison tool.
/// Both buffers must hold 32-bit BGRA pixels (as WPF produces), top-down, tightly packed.
/// Alpha is ignored: transparency changes alone are not visual regressions.
/// </summary>
public static class ScreenshotComparer
{
    public const int BytesPerPixel = 4;

    public static ScreenshotDiff Compare(int width, int height, ReadOnlySpan<byte> first, ReadOnlySpan<byte> second, int channelThreshold = 8)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width), "Width must be positive.");
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height), "Height must be positive.");
        if (channelThreshold < 0) channelThreshold = 0;
        if (channelThreshold > 255) channelThreshold = 255;

        int pixels = width * height;
        int expected = checked(pixels * BytesPerPixel);
        if (first.Length != expected || second.Length != expected)
            throw new ArgumentException($"Both buffers must hold {expected} bytes for {width}x{height} BGRA pixels.");

        int changed = 0;
        int minX = int.MaxValue;
        int minY = int.MaxValue;
        int maxX = -1;
        int maxY = -1;
        byte maxDelta = 0;
        long deltaSum = 0;

        for (int pixel = 0; pixel < pixels; pixel++)
        {
            int offset = pixel * BytesPerPixel;
            int delta = 0;
            for (int channel = 0; channel < 3; channel++)
            {
                int difference = Math.Abs(first[offset + channel] - second[offset + channel]);
                if (difference > delta) delta = difference;
            }

            deltaSum += delta;
            if (delta > maxDelta) maxDelta = (byte)Math.Min(255, delta);
            if (delta <= channelThreshold) continue;

            changed++;
            int x = pixel % width;
            int y = pixel / width;
            if (x < minX) minX = x;
            if (y < minY) minY = y;
            if (x > maxX) maxX = x;
            if (y > maxY) maxY = y;
        }

        return new ScreenshotDiff(
            width,
            height,
            pixels,
            changed,
            maxDelta,
            (double)deltaSum / pixels,
            minX == int.MaxValue ? -1 : minX,
            minY == int.MaxValue ? -1 : minY,
            maxX,
            maxY);
    }
}
