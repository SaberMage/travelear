namespace TravelEar.Helper;

/// <summary>
/// The Helper's only window. Starts minimized (OBS needs a top-level window to target; the user
/// needs nothing) and shows live status when restored. Status text is pushed from the renderer
/// thread through <see cref="Report"/>, which marshals to the UI thread.
/// </summary>
internal sealed class StatusForm : Form
{
    private readonly Label _status;

    public int ExitCode { get; set; } = Program.ExitOk;

    public StatusForm(string title)
    {
        Text = title;
        WindowState = FormWindowState.Minimized;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(520, 200);
        MinimumSize = new Size(360, 160);
        FormBorderStyle = FormBorderStyle.Sizable;

        _status = new Label
        {
            Dock = DockStyle.Fill,
            Font = new Font(FontFamily.GenericMonospace, 9.5f),
            Padding = new Padding(10),
            Text = "Starting...",
            AutoSize = false,
        };
        Controls.Add(_status);
    }

    /// <summary>Thread-safe status update.</summary>
    public void Report(string text)
    {
        if (IsDisposed) return;
        try
        {
            if (InvokeRequired) BeginInvoke(() => SetText(text));
            else SetText(text);
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    private void SetText(string text)
    {
        if (!IsDisposed) _status.Text = text;
    }
}
