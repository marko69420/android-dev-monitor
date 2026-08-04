using System.Globalization;

namespace AndroidDevMonitor.Core.Formatting;

public static class UnitFormatter
{
    public static string Bytes(double? bytes)
    {
        if (bytes is null) return "N/A"; string[] units = ["B", "KB", "MB", "GB", "TB"]; var value = bytes.Value; var index = 0;
        while (Math.Abs(value) >= 1024 && index < units.Length - 1) { value /= 1024; index++; }
        return $"{value.ToString("N1", CultureInfo.InvariantCulture)} {units[index]}";
    }
    public static string Rate(double? bytesPerSecond) => bytesPerSecond is null ? "N/A" : $"{Bytes(bytesPerSecond)}/s";
}
