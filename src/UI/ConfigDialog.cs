namespace WinGLSLScrSaver;

using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

internal class ConfigDialog : Form
{
    private readonly TextBox _folderBox;
    private readonly NumericUpDown _fpsCap;
    private readonly TrackBar _resMult;
    private readonly Label _resMultLabel;
    private readonly TrackBar _scaleBlend;
    private readonly Label _scaleBlendLabel;

    public ConfigDialog()
    {
        AppSettings s = Program.Cfg;

        Text = "WinGLSLScrSaver Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(460, 300);

        // ---- row 1: shader folder ----
        var folderLabel = new Label { Text = "Shader folder (a random shader is picked each start):",
            Location = new Point(12, 12), AutoSize = true };
        // Default to the local app data dir (where shaders are searched when
        // nothing is configured) instead of an empty box.
        _folderBox = new TextBox
        {
            Location = new Point(12, 34), Width = 340,
            Text = string.IsNullOrWhiteSpace(s.ShaderFolder) ? Settings.AppDataDir : s.ShaderFolder,
        };
        var browse = new Button { Text = "Browse...", Location = new Point(360, 32), Width = 85 };
        browse.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog
            {
                Description = "Pick the folder to draw random shaders from",
                SelectedPath = _folderBox.Text,
                ShowNewFolderButton = true,
            };
            if (dlg.ShowDialog(this) == DialogResult.OK) _folderBox.Text = dlg.SelectedPath;
        };

        // ---- row 2: FPS cap ----
        var fpsLabel = new Label { Text = "FPS cap (0 = auto: run at the fastest display's refresh):",
            Location = new Point(12, 72), AutoSize = true };
        _fpsCap = new NumericUpDown
        {
            Location = new Point(12, 94), Width = 100,
            Minimum = 0, Maximum = 1000, Increment = 5,
            Value = Math.Clamp(s.FpsCap, 0, 1000),
        };
        var fpsHint = new Label
        {
            Text = "Clamped to 1..max(highest monitor refresh, 120) when set.",
            Location = new Point(122, 97), AutoSize = true, ForeColor = SystemColors.GrayText,
        };

        // ---- row 3: resolution multiplier (trackbar = % of native) ----
        var resLabel = new Label { Text = "Resolution (% of native; below 100% renders smaller and upscales):",
            Location = new Point(12, 134), AutoSize = true };
        _resMult = new TrackBar
        {
            Location = new Point(12, 156), Width = 340,
            Minimum = 5, Maximum = 100, TickFrequency = 5,
            Value = (int) Math.Round(s.ResMult * 100f),
        };
        _resMultLabel = new Label { Location = new Point(360, 162), AutoSize = true };
        _resMult.ValueChanged += (_, _) => _resMultLabel.Text = $"{_resMult.Value}%";
        _resMultLabel.Text = $"{_resMult.Value}%";

        // ---- row 4: upscale interpolation blend ----
        var blendLabel = new Label { Text = "Upscale filtering (left = nearest/sharp, right = bilinear/smooth):",
            Location = new Point(12, 198), AutoSize = true };
        _scaleBlend = new TrackBar
        {
            Location = new Point(12, 220), Width = 340,
            Minimum = 0, Maximum = 100, TickFrequency = 10,
            Value = (int) Math.Round(s.ScaleBlend * 100f),
        };
        _scaleBlendLabel = new Label { Location = new Point(360, 226), AutoSize = true };
        _scaleBlendLabel.Text = _scaleBlend.Value switch
        {
            0 => "nearest",
            100 => "bilinear",
            _ => $"{_scaleBlend.Value}% blend",
        };
        _scaleBlend.ValueChanged += (_, _) => _scaleBlendLabel.Text = _scaleBlend.Value switch
        {
            0 => "nearest",
            100 => "bilinear",
            _ => $"{_scaleBlend.Value}% blend",
        };

        // ---- buttons ----
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(270, 262), Width = 85 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(360, 262), Width = 85 };

        Controls.AddRange(new Control[]
        {
            folderLabel, _folderBox, browse,
            fpsLabel, _fpsCap, fpsHint,
            resLabel, _resMult, _resMultLabel,
            blendLabel, _scaleBlend, _scaleBlendLabel,
            ok, cancel,
        });
        AcceptButton = ok;
        CancelButton = cancel;
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        if (DialogResult == DialogResult.OK)
        {
            var s = Program.Cfg;
            s.ShaderFolder = _folderBox.Text;
            s.FpsCap = (int) _fpsCap.Value;
            s.ResMult = Math.Clamp(_resMult.Value / 100f, AppSettings.MinResMult, AppSettings.MaxResMult);
            s.ScaleBlend = _scaleBlend.Value / 100f;
            s.Save();
        }
        base.OnFormClosed(e);
    }
}
