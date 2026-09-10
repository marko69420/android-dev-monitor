using AndroidDevMonitor.Core.Analysis;

namespace AndroidDevMonitor.Core.Tests;

public sealed class ScreenshotComparerTests
{
    [Fact]
    public void Identical_frames_report_no_change()
    {
        byte[] frame = Pixels((0, 10, 20), (30, 40, 50), (60, 70, 80), (90, 100, 110));

        ScreenshotDiff diff = ScreenshotComparer.Compare(2, 2, frame, (byte[])frame.Clone());

        Assert.False(diff.AnyChange);
        Assert.Equal(0, diff.ChangedPixels);
        Assert.Equal(0d, diff.ChangedPercent);
        Assert.Equal("none", diff.BoundingBox);
    }

    [Fact]
    public void Single_pixel_change_is_located()
    {
        byte[] before = Pixels((0, 0, 0), (0, 0, 0), (0, 0, 0), (0, 0, 0));
        byte[] after = Pixels((0, 0, 0), (0, 0, 0), (255, 255, 255), (0, 0, 0));

        ScreenshotDiff diff = ScreenshotComparer.Compare(2, 2, before, after);

        Assert.True(diff.AnyChange);
        Assert.Equal(1, diff.ChangedPixels);
        Assert.Equal(25d, diff.ChangedPercent, 3);
        Assert.Equal("0,1 -> 0,1", diff.BoundingBox);
        Assert.Equal(255, diff.MaxChannelDelta);
    }

    [Fact]
    public void Threshold_suppresses_small_noise()
    {
        byte[] before = Pixels((100, 100, 100), (100, 100, 100));
        byte[] after = Pixels((103, 100, 100), (100, 100, 100));

        ScreenshotDiff diff = ScreenshotComparer.Compare(2, 1, before, after);

        Assert.False(diff.AnyChange);
        Assert.Equal(3, diff.MaxChannelDelta);
        Assert.Equal(1.5d, diff.MeanChannelDelta, 3);
    }

    [Fact]
    public void Buffer_size_must_match_dimensions()
    {
        Assert.Throws<ArgumentException>(() => ScreenshotComparer.Compare(2, 2, new byte[15], new byte[16]));
    }

    [Fact]
    public void Invalid_dimensions_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ScreenshotComparer.Compare(0, 1, [], []));
    }

    private static byte[] Pixels(params (byte R, byte G, byte B)[] pixels)
    {
        byte[] buffer = new byte[pixels.Length * 4];
        for (int index = 0; index < pixels.Length; index++)
        {
            buffer[index * 4] = pixels[index].B;
            buffer[index * 4 + 1] = pixels[index].G;
            buffer[index * 4 + 2] = pixels[index].R;
            buffer[index * 4 + 3] = 255;
        }
        return buffer;
    }
}
