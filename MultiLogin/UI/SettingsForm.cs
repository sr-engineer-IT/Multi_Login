using MultiLogin.Models;
using MultiLogin.Services;

namespace MultiLogin.UI;

public class SettingsForm : Form
{
    private readonly AppSettings _settings;
    private readonly TextBox _browserPath = new() { Dock = DockStyle.Fill };

    public SettingsForm(AppSettings settings, string dataDir)
    {
        _settings = settings;
        Text = "Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(620, 230);
        Font = new Font("Segoe UI", 9.5f);

        _browserPath.Text = settings.BrowserPath;
        _browserPath.PlaceholderText = "Auto-detect: " + (BrowserLauncher.DetectBrowser() ?? "none found");

        var browse = new Button { Text = "Browse…", AutoSize = true };
        browse.Click += (_, _) => Browse();

        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Padding = new Padding(12) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        grid.Controls.Add(new Label { Text = "Browser", AutoSize = true, Margin = new Padding(3, 8, 3, 3) });
        grid.Controls.Add(_browserPath);
        grid.Controls.Add(browse);

        grid.Controls.Add(new Label());
        var hint = new Label
        {
            Text = "Any Chromium browser works (Chrome, Edge, Brave, Chromium). Leave empty to auto-detect.",
            AutoSize = true, MaximumSize = new Size(440, 0), ForeColor = SystemColors.GrayText
        };
        grid.Controls.Add(hint);
        grid.SetColumnSpan(hint, 2);

        grid.Controls.Add(new Label { Text = "Data folder", AutoSize = true, Margin = new Padding(3, 12, 3, 3) });
        var data = new TextBox { Text = dataDir, ReadOnly = true, Dock = DockStyle.Fill, Margin = new Padding(3, 8, 3, 3) };
        grid.Controls.Add(data);
        grid.SetColumnSpan(data, 2);

        var ok = new Button { Text = "Save", DialogResult = DialogResult.OK, Width = 100, Height = 32 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 100, Height = 32 };
        ok.Click += OnSave;
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 50, Padding = new Padding(8) };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        AcceptButton = ok;
        CancelButton = cancel;

        Controls.Add(grid);
        Controls.Add(buttons);
    }

    private void Browse()
    {
        using var dlg = new OpenFileDialog { Filter = "Browser|*.exe", Title = "Select chrome.exe, msedge.exe or brave.exe" };
        if (dlg.ShowDialog(this) == DialogResult.OK)
            _browserPath.Text = dlg.FileName;
    }

    private void OnSave(object? sender, EventArgs e)
    {
        var path = _browserPath.Text.Trim();
        if (path.Length > 0 && !File.Exists(path))
        {
            MessageBox.Show(this, "That file doesn't exist.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            DialogResult = DialogResult.None;
            return;
        }
        _settings.BrowserPath = path;
    }
}
