namespace WinGLSLScrSaver;

using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

internal class ConfigDialog : Form
{
    private readonly TextBox _folderBox;

    public ConfigDialog()
    {
        Text = "WinGLSLScrSaver Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(460, 120);

        var label = new Label { Text = "Shader folder (a random shader is picked each start):",
            Location = new Point(12, 12), AutoSize = true };
        // Default to the local app data dir (where shaders are searched when
        // nothing is configured) instead of an empty box.
        string configured = Settings.LoadShaderFolder();
        _folderBox = new TextBox
        {
            Location = new Point(12, 34), Width = 340,
            Text = string.IsNullOrWhiteSpace(configured) ? Settings.AppDataDir : configured,
        };
        var browse = new Button { Text = "Browse...", Location = new Point(360, 32), Width = 85 };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(270, 78), Width = 85 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(360, 78), Width = 85 };

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

        Controls.AddRange(new Control[] { label, _folderBox, browse, ok, cancel });
        AcceptButton = ok;
        CancelButton = cancel;
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        if (DialogResult == DialogResult.OK)
            Settings.SaveShaderFolder(_folderBox.Text);
        base.OnFormClosed(e);
    }
}
