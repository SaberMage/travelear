namespace TravelEar.Helper;

/// <summary>
/// Append-only log at <c>%LOCALAPPDATA%\TravelEar\Helper.log</c>. A WinExe has no console, and the
/// operator verifies spikes by reading this file. Never throws.
/// </summary>
internal static class HelperLog
{
    private static readonly object Gate = new();
    private static readonly string? Path = Resolve();

    public static string? FilePath => Path;

    public static void Write(string message)
    {
        if (Path is null) return;
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}";
        lock (Gate)
        {
            try { File.AppendAllText(Path, line); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static string? Resolve()
    {
        try
        {
            var dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TravelEar");
            Directory.CreateDirectory(dir);
            var path = System.IO.Path.Combine(dir, "Helper.log");
            // Keep the log bounded: start fresh once it passes 1 MB.
            if (File.Exists(path) && new FileInfo(path).Length > 1_000_000) File.Delete(path);
            return path;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
