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

    private readonly ToolStripButton _btnLaunch, _btnStop, _btnEdit, _btnClone, _btnDelete, _btnFolder, _btnCheckAll;
    private readonly HashSet<string> _checkedIds = new();
    private bool _refreshing, _suppressCheck;

    public MainForm()
    {
        Text = "Multi Login";
        Icon = AppIcon();
        Width = 1250;
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
        var btnExport = Button("⇩  Export", (_, _) => ExportProfiles());
        var btnImport = Button("⇧  Import", (_, _) => ImportProfiles());
        _btnCheckAll = Button("☑  Check all", (_, _) => ToggleCheckAll());
        var btnSettings = Button("⚙  Settings", (_, _) => OpenSettings());
        btnSettings.Alignment = ToolStripItemAlignment.Right;
        _search.TextChanged += (_, _) => RefreshList();
        toolbar.Items.AddRange(new ToolStripItem[]
        {
            btnNew, _btnCheckAll, new ToolStripSeparator(), _btnLaunch, _btnStop, new ToolStripSeparator(),
            _btnEdit, _btnClone, _btnDelete, new ToolStripSeparator(),
            btnExport, btnImport, new ToolStripSeparator(),
            new ToolStripLabel("Search:"), _search, btnSettings, _btnFolder
        });
        _btnFolder.Alignment = ToolStripItemAlignment.Right;

        _list.Dock = DockStyle.Fill;
        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.GridLines = false;
        _list.HideSelection = false;
        _list.BorderStyle = BorderStyle.None;
        _list.CheckBoxes = true;
        _list.Columns.Add("Status", 110);
        _list.Columns.Add("Name", 220);
        _list.Columns.Add("Tags", 140);
        _list.Columns.Add("Proxy", 220);
        _list.Columns.Add("Fingerprint", 150);
        _list.Columns.Add("Notes", 200);
        _list.Columns.Add("Last launched", 140);
        _list.DoubleClick += (_, _) => LaunchSelected(SelectedProfiles);
        _list.MouseDown += (_, e) => _suppressCheck = e.Clicks > 1;
        _list.MouseUp += (_, _) => _suppressCheck = false;
        _list.ItemCheck += (_, e) => { if (_suppressCheck) e.NewValue = e.CurrentValue; };
        _list.SelectedIndexChanged += (_, _) => UpdateButtons();
        _list.ItemChecked += OnItemChecked;
        // Clicking the first column header checks or unchecks every visible profile.
        _list.ColumnClick += (_, e) => { if (e.Column == 0) ToggleCheckAll(); };
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

    /// <summary>Highlighted rows; used by single-profile actions like Edit and Clone.</summary>
    private List<Profile> SelectedProfiles =>
        _list.SelectedItems.Cast<ListViewItem>().Select(i => (Profile)i.Tag!).ToList();

    /// <summary>What bulk actions (Launch, Stop, Delete, Export) apply to: the checked profiles if any, else the highlighted rows.</summary>
    private List<Profile> TargetProfiles
    {
        get
        {
            var checkedItems = _list.CheckedItems.Cast<ListViewItem>().Select(i => (Profile)i.Tag!).ToList();
            return checkedItems.Count > 0 ? checkedItems : SelectedProfiles;
        }
    }

    private void OnItemChecked(object? sender, ItemCheckedEventArgs e)
    {
        if (_refreshing) return;
        var id = ((Profile)e.Item.Tag!).Id;
        if (e.Item.Checked) _checkedIds.Add(id); else _checkedIds.Remove(id);
        UpdateButtons();
    }

    private void ToggleCheckAll()
    {
        var items = _list.Items.Cast<ListViewItem>().ToList();
        var check = items.Any(i => !i.Checked);
        _refreshing = true;
        foreach (var item in items)
        {
            item.Checked = check;
            var id = ((Profile)item.Tag!).Id;
            if (check) _checkedIds.Add(id); else _checkedIds.Remove(id);
        }
        _refreshing = false;
        UpdateButtons();
    }

    private void RefreshList()
    {
        var selectedIds = SelectedProfiles.Select(p => p.Id).ToHashSet();
        _checkedIds.IntersectWith(_store.Profiles.Select(p => p.Id));
        _refreshing = true;
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
            item.SubItems.Add(p.Fingerprint.Enabled
                ? $"{p.Fingerprint.Platform} · {p.Fingerprint.ScreenWidth}×{p.Fingerprint.ScreenHeight}"
                : "Off");
            item.SubItems.Add(p.Notes.Replace("\r", " ").Replace("\n", " "));
            item.SubItems.Add(p.LastLaunchedUtc?.ToLocalTime().ToString("g") ?? "Never");
            if (running) item.ForeColor = Color.SeaGreen;
            item.Selected = selectedIds.Contains(p.Id);
            item.Checked = _checkedIds.Contains(p.Id);
            _list.Items.Add(item);
        }
        _list.EndUpdate();
        _refreshing = false;
        UpdateButtons();
    }

    private static bool Matches(Profile p, string filter) =>
        p.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
        || p.Tags.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
        || p.Notes.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
        || p.ProxyHost.Contains(filter, StringComparison.OrdinalIgnoreCase);

    private void UpdateButtons()
    {
        var targets = TargetProfiles;
        var toLaunch = targets.Count(p => !_launcher.IsRunning(p.Id));
        var toStop = targets.Count(p => _launcher.IsRunning(p.Id));
        _btnLaunch.Enabled = toLaunch > 0;
        _btnLaunch.Text = toLaunch > 1 ? $"▶  Launch ({toLaunch})" : "▶  Launch";
        _btnStop.Enabled = toStop > 0;
        _btnStop.Text = toStop > 1 ? $"■  Stop ({toStop})" : "■  Stop";
        _btnDelete.Enabled = targets.Count > 0;
        _btnEdit.Enabled = _btnClone.Enabled = SelectedProfiles.Count == 1;

        var visible = _list.Items.Count;
        _btnCheckAll.Text = visible > 0 && _list.CheckedItems.Count == visible ? "☐  Uncheck all" : "☑  Check all";
        var checkedText = _list.CheckedItems.Count > 0 ? $"  ·  {_list.CheckedItems.Count} checked" : "";
        _status.Text = $"{_store.Profiles.Count} profiles  ·  {_launcher.RunningCount} running{checkedText}";
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
        var sel = TargetProfiles;
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

    private async void LaunchSelected(List<Profile>? only = null)
    {
        var toLaunch = (only ?? TargetProfiles).Where(p => !_launcher.IsRunning(p.Id)).ToList();
        if (toLaunch.Count == 0) return;

        var launches = toLaunch.Select(LaunchOneAsync).ToList();
        RefreshList();
        _status.Text = $"Launching {toLaunch.Count} profile(s)…";
        await Task.WhenAll(launches);
        RefreshList();
    }

    private async Task LaunchOneAsync(Profile profile)
    {
        try { await _launcher.LaunchAsync(profile); }
        catch (Exception ex) { ShowError($"Couldn't launch \"{profile.Name}\":\n\n{ex.Message}"); }
    }

    private async Task StopSelected()
    {
        await Task.WhenAll(TargetProfiles.Select(p => _launcher.StopAsync(p.Id)));
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

    /// <summary>Exports the selected profiles, or all of them when none are selected.</summary>
    private void ExportProfiles()
    {
        var targets = TargetProfiles;
        var profiles = targets.Count > 0 ? targets : _store.Profiles.ToList();
        if (profiles.Count == 0) return;

        using var save = new SaveFileDialog
        {
            Filter = ProfileTransfer.FileFilter,
            FileName = profiles.Count == 1 ? $"{profiles[0].Name}.mlprofiles" : $"profiles-{DateTime.Now:yyyy-MM-dd}.mlprofiles"
        };
        if (save.ShowDialog(this) != DialogResult.OK) return;

        using var pw = new PasswordForm("Protect export",
            "Choose a password for this file. It's needed to import it, and lets proxy passwords be included.",
            confirmMode: true);
        if (pw.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            ProfileTransfer.Export(profiles, save.FileName, pw.Password);
            _status.Text = $"Exported {profiles.Count} profile(s) to {Path.GetFileName(save.FileName)}";
        }
        catch (Exception ex)
        {
            ShowError($"Export failed: {ex.Message}");
        }
    }

    private void ImportProfiles()
    {
        using var open = new OpenFileDialog { Filter = ProfileTransfer.FileFilter };
        if (open.ShowDialog(this) != DialogResult.OK) return;

        List<Profile> imported;
        try
        {
            string? password = null;
            if (ProfileTransfer.IsEncrypted(open.FileName))
            {
                using var pw = new PasswordForm("Import profiles", "This file is protected. Enter its password.", confirmMode: false);
                if (pw.ShowDialog(this) != DialogResult.OK) return;
                password = pw.Password;
            }
            imported = ProfileTransfer.Import(open.FileName, password);
        }
        catch (Exception ex)
        {
            ShowError($"Import failed: {ex.Message}");
            return;
        }

        var added = 0;
        foreach (var p in imported)
        {
            var existing = _store.Profiles.FirstOrDefault(x => x.Id == p.Id);
            if (existing != null)
            {
                var answer = MessageBox.Show(this,
                    $"\"{existing.Name}\" already exists here.\n\n" +
                    "Yes = replace its settings (keeps its browser data)\nNo = import as a separate profile\nCancel = skip",
                    "Import profiles", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (answer == DialogResult.Cancel) continue;
                if (answer == DialogResult.Yes)
                {
                    if (_launcher.IsRunning(existing.Id))
                    {
                        ShowError($"Stop \"{existing.Name}\" before replacing it.");
                        continue;
                    }
                    p.LastLaunchedUtc = existing.LastLaunchedUtc;
                    _store.Profiles[_store.Profiles.IndexOf(existing)] = p;
                    added++;
                    continue;
                }
                // Separate profile: new ID (own browser data folder), same fingerprint.
                p.Id = Guid.NewGuid().ToString("N");
            }
            _store.Profiles.Add(p);
            added++;
        }

        _store.Save();
        RefreshList();
        _status.Text = $"Imported {added} profile(s)";
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

    /// <summary>Title-bar / taskbar icon. The exe's own icon comes from ApplicationIcon in the .csproj.</summary>
    private static Icon AppIcon()
    {
        using var stream = typeof(MainForm).Assembly.GetManifestResourceStream("MultiLogin.app.ico")!;
        return new Icon(stream);
    }
}
