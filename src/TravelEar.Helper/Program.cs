namespace TravelEar.Helper;

/// <summary>
/// Entry point. The Helper owns a minimized, titled window ("TravelEar for Big Walk") so OBS's
/// Application Audio Capture can select it, reads float32 PCM frames from the mod over a named pipe,
/// and renders them to the configured WASAPI endpoint.
/// </summary>
internal static class Program
{
    public const string WindowTitle = "TravelEar for Big Walk";
    public const string PipeName = "TravelEar.Sink";

    [STAThread]
    private static int Main(string[] args)
    {
        // Next: parse --endpoint <substring>, open the pipe server, start WasapiOut on a dedicated thread,
        // exit when the pipe closes. See docs/DESIGN.md (Sink).
        return 0;
    }
}
