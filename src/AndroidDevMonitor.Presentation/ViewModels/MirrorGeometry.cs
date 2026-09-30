namespace AndroidDevMonitor.Presentation.ViewModels;

/// <summary>Maps a click on the mirror image to device pixels.</summary>
public static class MirrorGeometry
{
    /// <summary>
    /// The image uses Uniform stretch, so the frame can be letterboxed inside the control.
    /// Returns null for clicks on the letterbox bars or when there is no frame.
    /// </summary>
    public static (int X, int Y)? ToDevice(double controlWidth, double controlHeight, int frameWidth, int frameHeight, double clickX, double clickY)
    {
        if (frameWidth <= 0 || frameHeight <= 0 || controlWidth <= 0 || controlHeight <= 0) return null;
        double scale = Math.Min(controlWidth / frameWidth, controlHeight / frameHeight);
        if (scale <= 0) return null;
        double offsetX = (controlWidth - frameWidth * scale) / 2;
        double offsetY = (controlHeight - frameHeight * scale) / 2;
        double deviceX = (clickX - offsetX) / scale;
        double deviceY = (clickY - offsetY) / scale;
        if (deviceX < 0 || deviceY < 0 || deviceX > frameWidth || deviceY > frameHeight) return null;
        return ((int)Math.Round(deviceX), (int)Math.Round(deviceY));
    }
}
