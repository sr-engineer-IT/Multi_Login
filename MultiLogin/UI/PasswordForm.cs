namespace MultiLogin.UI;

/// <summary>Asks for a file password. In confirm mode (exporting) it asks twice and allows none.</summary>
public class PasswordForm : Form
{
    private readonly TextBox _password = new() { UseSystemPasswordChar = true, Dock = DockStyle.Fill };
    private readonly TextBox _confirm = new() { UseSystemPasswordChar = true, Dock = DockStyle.Fill };
    private readonly bool _confirmMode;

    public string Password => _password.Text;

    public PasswordForm(string title, string message, bool confirmMode)
    {
        _confirmMode = confirmMode;
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(460, confirmMode ? 230 : 170);
        Font = new Font("Segoe UI", 9.5f);

        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(12) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var info = new Label { Text = message, AutoSize = true, MaximumSize = new Size(430, 0), Margin = new Padding(3, 0, 3, 10) };
        grid.Controls.Add(info);
        grid.SetColumnSpan(info, 2);
        grid.Controls.Add(new Label { Text = "Password", AutoSize = true, Margin = new Padding(3, 7, 3, 3) });
        grid.Controls.Add(_password);
        if (confirmMode)
        {
            grid.Controls.Add(new Label { Text = "Confirm", AutoSize = true, Margin = new Padding(3, 7, 3, 3) });
            grid.Controls.Add(_confirm);
        }

        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Width = 100, Height = 32 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 100, Height = 32 };
        ok.Click += OnOk;
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 50, Padding = new Padding(8) };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        AcceptButton = ok;
        CancelButton = cancel;

        Controls.Add(grid);
        Controls.Add(buttons);
    }

    private void OnOk(object? sender, EventArgs e)
    {
        string? error = null;
        if (_confirmMode && _password.Text != _confirm.Text)
            error = "The passwords don't match.";
        else if (!_confirmMode && _password.Text.Length == 0)
            error = "Enter the password.";
        else if (_confirmMode && _password.Text.Length == 0
                 && MessageBox.Show(this, "Export without a password?\n\nProxy passwords will be left out, and the file can be read by anyone.",
                     Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            error = "";

        if (error == null) return;
        if (error.Length > 0) MessageBox.Show(this, error, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        DialogResult = DialogResult.None;
    }
}
