using System.Diagnostics;
using MultiLogin.Models;
using MultiLogin.Services;

namespace MultiLogin.UI;

public class MainForm : Form
{
    private readonly ProfileStore _store = new();
    private readonly BrowserLauncher _launcher;

    private readonly ListView _list = new();
    private readonly ToolStripTextBox _search = new() { Width = 220 };
    private readonly ToolStripStatusLabel _status = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolStripStatusLabel _browserLabel = new();

    private readonly ToolStripButton _btnLaunch, _btnStop, _btnEdit, _btnClone, _btnDelete, _btnFolder;

    public MainForm()
    {
        Text = "Multi Login";
        Width = 1100;
        Height = 650;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9.5f);

        _store.Load();
        _launcher = new BrowserLauncher(_store);
        _launcher.ProfileExited += _ => BeginInvoke(RefreshList);

        var toolbar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(6, 4, 6, 4), ImageScalingSize = new Size(20, 20) };
        var btnNew = Button("➕  New profile", (_, _) => NewProfile());
        _btnLaunch = Button("▶  Launch", (_, _) => LaunchSelected());
        _btnStop = Button("■  Stop", async (_, _) => await StopSelected());
        _btnEdit = Button("✎  Edit", (_, _) => EditSelected());
        _btnClone = Button("⧉  Clone", (_, _) => CloneSelected());
        _btnDelete = Button("🗑  Delete", (_, _) => DeleteSelected());
        _btnFolder = Button("📁  Data folder", (_, _) => OpenDataFolder());
        var btnSettings = Button("⚙  Settings", (_, _) => OpenSettings());
        btnSettings.Alignment = ToolStripItemAlignment.Right;
        _search.TextChanged += (_, _) => RefreshList();
        toolbar.Items.AddRange(new ToolStripItem[]
        {
            btnNew, new ToolStripSeparator(), _btnLaunch, _btnStop, new ToolStripSeparator(),
            _btnEdit, _btnClone, _btnDelete, new ToolStripSeparator(),
            new ToolStripLabel("Search:"), _search, btnSettings, _btnFolder
        });
        _btnFolder.Alignment = ToolStripItemAlignment.Right;

        _list.Dock = DockStyle.Fill;
        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.GridLines = false;
        _list.HideSelection = false;
        _list.BorderStyle = BorderStyle.None;
        _list.Columns.Add("Status", 90);
        _list.Columns.Add("Name", 220);
        _list.Columns.Add("Tags", 140);
        _list.Columns.Add("Proxy", 240);
        _list.Columns.Add("Notes", 240);
        _list.Columns.Add("Last launched", 140);
        _list.DoubleClick += (_, _) => LaunchSelected();
        _list.SelectedIndexChanged += (_, _) => UpdateButtons();
        _list.KeyDown += OnListKeyDown;
        _list.ContextMenuStrip = BuildContextMenu();

        var statusStrip = new StatusStrip();
        statusStrip.Items.AddRange(new ToolStripItem[] { _status, _browserLabel });

        Controls.Add(_list);
        Controls.Add(toolbar);
        Controls.Add(statusStrip);

        FormClosing += OnFormClosing;
        RefreshList();
        UpdateBrowserLabel();
    }

    private static ToolStripButton Button(string text, EventHandler onClick)
    {
        var b = new ToolStripButton(text) { DisplayStyle = ToolStripItemDisplayStyle.Text, Padding = new Padding(4, 0, 4, 0) };
        b.Click += onClick;
        return b;
    }

    private ContextMenuStrip BuildContextMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Launch", null, (_, _) => LaunchSelected());
        menu.Items.Add("Stop", null, async (_, _) => await StopSelected());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Edit…", null, (_, _) => EditSelected());
        menu.Items.Add("Clone", null, (_, _) => CloneSelected());
        menu.Items.Add("Check proxy", null, async (_, _) => await CheckSelectedProxy());
        menu.Items.Add("Open profile folder", null, (_, _) => OpenProfileFolder());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Delete", null, (_, _) => DeleteSelected());
        return menu;
    }

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.Enter: LaunchSelected(); break;
            case Keys.Delete: DeleteSelected(); break;
            case Keys.F2: EditSelected(); break;
            case Keys.N when e.Control: NewProfile(); break;
            case Keys.A when e.Control:
                foreach (ListViewItem item in _list.Items) item.Selected = true;
                break;
            default: return;
        }
        e.Handled = true;
    }

    private List<Profile> SelectedProfiles =>
        _list.SelectedItems.Cast<ListViewItem>().Select(i => (Profile)i.Tag!).ToList();

    private void RefreshList()
    {
        var selectedIds = SelectedProfiles.Select(p => p.Id).ToHashSet();
        var filter = _search.Text.Trim();

        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var p in _store.Profiles.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            if (filter.Length > 0 && !Matches(p, filter)) continue;

            var running = _launcher.IsRunning(p.Id);
            var item = new ListViewItem(running ? "● Running" : "Stopped") { Tag = p };
            item.SubItems.Add(p.Name);
            item.SubItems.Add(p.Tags);
            item.SubItems.Add(p.ProxyDisplay);
            item.SubItems.Add(p.Notes.Replace("\r", " ").Replace("\n", " "));
            item.SubItems.Add(p.LastLaunchedUtc?.ToLocalTime().ToString("g") ?? "Never");
            if (running) item.ForeColor = Color.SeaGreen;
            item.Selected = selectedIds.Contains(p.Id);
            _list.Items.Add(item);
        }
        _list.EndUpdate();

        _status.Text = $"{_store.Profiles.Count} profiles  ·  {_launcher.RunningCount} running";
        UpdateButtons();
    }

    private static bool Matches(Profile p, string filter) =>
        p.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
        || p.Tags.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
        || p.Notes.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
        || p.ProxyHost.Contains(filter, StringComparison.OrdinalIgnoreCase);

    private void UpdateButtons()
    {
        var sel = SelectedProfiles;
        var any = sel.Count > 0;
        _btnLaunch.Enabled = sel.Any(p => !_launcher.IsRunning(p.Id));
        _btnStop.Enabled = sel.Any(p => _launcher.IsRunning(p.Id));
        _btnEdit.Enabled = sel.Count == 1;
        _btnClone.Enabled = sel.Count == 1;
        _btnDelete.Enabled = any;
    }

    private void UpdateBrowserLabel()
    {
        var path = _launcher.ResolveBrowserPath();
        _browserLabel.Text = path == null ? "⚠ No browser found — set one in Settings" : $"Browser: {Path.GetFileName(path)} {UserAgents.GetMajorVersion(path)}";
    }

    private void NewProfile()
    {
        var profile = new Profile { Name = $"Profile {_store.Profiles.Count + 1}" };
        using var dlg = new ProfileForm(profile, _launcher.ResolveBrowserPath());
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _store.Profiles.Add(profile);
        _store.Save();
        RefreshList();
    }

    private void EditSelected()
    {
        if (SelectedProfiles is not [var profile]) return;
        if (_launcher.IsRunning(profile.Id))
            MessageBox.Show(this, "Changes take effect the next time this profile is launched.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        using var dlg = new ProfileForm(profile, _launcher.ResolveBrowserPath());
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _store.Save();
        RefreshList();
    }

    private void CloneSelected()
    {
        if (SelectedProfiles is not [var profile]) return;
        // Only settings are cloned; the copy starts with fresh, empty browser data.
        _store.Profiles.Add(profile.Clone());
        _store.Save();
        RefreshList();
    }

    private void DeleteSelected()
    {
        var sel = SelectedProfiles;
        if (sel.Count == 0) return;
        if (sel.Any(p => _launcher.IsRunning(p.Id)))
        {
            MessageBox.Show(this, "Stop the running profiles before deleting them.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        var what = sel.Count == 1 ? $"\"{sel[0].Name}\"" : $"{sel.Count} profiles";
        if (MessageBox.Show(this, $"Delete {what}?\n\nThis permanently removes their cookies, logins and browsing data.",
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;

        foreach (var p in sel)
        {
            try { _store.Delete(p); }
            catch (Exception ex) { ShowError($"Couldn't fully delete \"{p.Name}\": {ex.Message}"); }
        }
        RefreshList();
    }

    private void LaunchSelected()
    {
        foreach (var p in SelectedProfiles.Where(p => !_launcher.IsRunning(p.Id)))
        {
            try { _launcher.Launch(p); }
            catch (Exception ex) { ShowError(ex.Message); }
        }
        RefreshList();
    }

    private async Task StopSelected()
    {
        await Task.WhenAll(SelectedProfiles.Select(p => _launcher.StopAsync(p.Id)));
        RefreshList();
    }

    private async Task CheckSelectedProxy()
    {
        if (SelectedProfiles is not [var profile]) return;
        _status.Text = $"Checking proxy for \"{profile.Name}\"…";
        var result = await ProxyChecker.CheckAsync(profile);
        RefreshList();
        MessageBox.Show(this, result.Message, result.Ok ? "Proxy OK" : "Proxy check failed",
            MessageBoxButtons.OK, result.Ok ? MessageBoxIcon.Information : MessageBoxIcon.Error);
    }

    private void OpenProfileFolder()
    {
        if (SelectedProfiles is not [var profile]) return;
        var dir = _store.GetUserDataDir(profile);
        Directory.CreateDirectory(dir);
        Process.Start("explorer.exe", dir);
    }

    private void OpenDataFolder() => Process.Start("explorer.exe", _store.DataDir);

    private void OpenSettings()
    {
        using var dlg = new SettingsForm(_store.Settings, _store.DataDir);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _store.Save();
        UpdateBrowserLabel();
    }

    private async void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_launcher.RunningCount == 0) return;

        var answer = MessageBox.Show(this,
            $"{_launcher.RunningCount} profile(s) are still running.\n\n" +
            "Profiles that use a proxy lose their connection when this app closes.\n\nClose all browsers and exit?",
            Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        e.Cancel = true;
        if (answer != DialogResult.Yes) return;

        Enabled = false;
        _status.Text = "Closing browsers…";
        await _launcher.StopAllAsync();
        FormClosing -= OnFormClosing;
        Close();
    }

    private void ShowError(string message) =>
        MessageBox.Show(this, message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
}
