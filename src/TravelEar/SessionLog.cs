using System;
using System.IO;
using System.Linq;
using BepInEx.Logging;

namespace TravelEar;

/// <summary>
/// A per-launch copy of the mod's log lines (and every warning or error from any source) under
/// <c>%LOCALAPPDATA%\TravelEar\logs\game-&lt;timestamp&gt;.log</c>. BepInEx overwrites
/// <c>LogOutput.log</c> on every launch, so a second short launch erases the run before it
/// (M3 run 5); these files survive. The newest <see cref="Keep"/> are kept.
/// </summary>
internal sealed class SessionLog : ILogListener
{
    private const int Keep = 12;
    private static SessionLog _instance;
    private readonly StreamWriter _writer;
    private readonly object _lock = new();

    public static string Directory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TravelEar", "logs");

    public string FilePath { get; }

    public LogLevel LogLevelFilter => LogLevel.All;

    private SessionLog(string path)
    {
        FilePath = path;
        _writer = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
    }

    /// <summary>Starts the per-launch file and registers it with BepInEx's logger; failures are logged and ignored.</summary>
    public static void Start(ManualLogSource log)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            foreach (var stale in new DirectoryInfo(Directory).GetFiles("game-*.log").OrderByDescending(f => f.Name).Skip(Keep - 1))
            {
                try { stale.Delete(); } catch (Exception) { /* best effort */ }
            }
            var path = Path.Combine(Directory, $"game-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            _instance = new SessionLog(path);
            Logger.Listeners.Add(_instance);
            log.LogInfo($"Session log: {path}");
        }
        catch (Exception e)
        {
            log.LogWarning($"Session log: not started ({e.Message}); BepInEx/LogOutput.log is the only copy.");
        }
    }

    public static void Stop()
    {
        var instance = _instance;
        _instance = null;
        if (instance is null) return;
        try { Logger.Listeners.Remove(instance); } catch (Exception) { /* shutting down */ }
        instance.Dispose();
    }

    public void LogEvent(object sender, LogEventArgs eventArgs)
    {
        try
        {
            var ours = eventArgs.Source?.SourceName == Plugin.Name;
            if (!ours && (eventArgs.Level & (LogLevel.Warning | LogLevel.Error | LogLevel.Fatal)) == 0) return;
            lock (_lock)
            {
                _writer.Write(DateTime.Now.ToString("HH:mm:ss.fff "));
                _writer.WriteLine(eventArgs.ToString().TrimEnd());
            }
        }
        catch (Exception)
        {
            // Never let the log copy disturb the game.
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            try { _writer.Dispose(); } catch (Exception) { /* best effort */ }
        }
    }
}
