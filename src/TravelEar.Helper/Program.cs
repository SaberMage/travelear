using TravelEar.Core;

namespace TravelEar.Helper;

/// <summary>
/// Entry point. The Helper owns a minimized, titled window ("TravelEar for Big Walk") so OBS's
/// Application Audio Capture can select it, reads float32 PCM frames from the mod over a named pipe,
/// and renders them to the configured WASAPI endpoint. See docs/DESIGN.md (Sink) and ADR-0001.
/// </summary>
internal static class Program
{
    public const string WindowTitle = "TravelEar for Big Walk";

    public const int ExitOk = 0;
    public const int ExitBadArgs = 1;
    public const int ExitNoEndpoint = 2;
    public const int ExitAudioFailure = 3;

    [STAThread]
    private static int Main(string[] args)
    {
        HelperOptions options;
        try
        {
            options = HelperOptions.Parse(args);
        }
        catch (ArgumentException e)
        {
            HelperLog.Write($"Bad arguments: {e.Message}");
            MessageBox.Show(e.Message + "\n\n" + HelperOptions.Usage, WindowTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return ExitBadArgs;
        }
        if (options.ShowHelp)
        {
            MessageBox.Show(HelperOptions.Usage, WindowTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return ExitOk;
        }

        // [impl->REQ-SINK-LIFECYCLE]
        // The mod launches us with --detach: we start the real Helper as our own child and exit
        // at once, so the Helper's parent is this short-lived launcher, not the game. OBS's
        // process-tree matching then cannot fold the Helper's audio into the game's capture.
        if (options.Detach)
        {
            var self = Environment.ProcessPath;
            if (string.IsNullOrEmpty(self))
            {
                HelperLog.Write("Detach: cannot resolve own path; running attached instead.");
            }
            else
            {
                try
                {
                    var start = new System.Diagnostics.ProcessStartInfo(self)
                    {
                        UseShellExecute = false,
                        WorkingDirectory = Path.GetDirectoryName(self) ?? Environment.CurrentDirectory,
                    };
                    foreach (var arg in (options with { Detach = false }).ToArgs()) start.ArgumentList.Add(arg);
                    var child = System.Diagnostics.Process.Start(start);
                    HelperLog.Write($"Detach: started pid {child?.Id}; launcher exiting.");
                    return ExitOk;
                }
                catch (Exception e)
                {
                    HelperLog.Write($"Detach: could not start a detached copy ({e.Message}); running attached instead.");
                }
            }
        }

        HelperLog.Write($"Starting: endpoint='{options.EndpointSetting}' tone={options.Tone} pipe={options.PipeName}");

        ApplicationConfiguration.Initialize();
        using var form = new StatusForm(WindowTitle);
        using var renderer = new SinkRenderer(options, form.Report);

        // [impl->REQ-SINK-HELPER-PROCESS]
        // The window is the capture target OBS keys on; audio runs on the renderer's own thread so
        // the message loop never blocks the stream and the stream never blocks the window.
        renderer.Exited += code => form.BeginInvoke(() => { form.ExitCode = code; form.Close(); });
        form.FormClosing += (_, _) => renderer.Stop();
        form.Shown += (_, _) => renderer.Start();

        Application.Run(form);
        renderer.Stop();
        HelperLog.Write($"Exit {form.ExitCode}");
        return form.ExitCode;
    }
}
