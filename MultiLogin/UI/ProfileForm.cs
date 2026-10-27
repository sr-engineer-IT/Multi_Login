using MultiLogin.Models;
using MultiLogin.Services;

namespace MultiLogin.UI;

public class ProfileForm : Form
{
    private readonly Profile _profile;
    private Fingerprint _fp;

    // General
    private readonly TextBox _name = new();
    private readonly TextBox _tags = new();
    private readonly TextBox _notes = Multiline(4);
    private readonly TextBox _startUrls = Multiline(4);

    // Proxy
    private readonly ComboBox _proxyType = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _proxyHost = new();
    private readonly NumericUpDown _proxyPort = new() { Minimum = 0, Maximum = 65535, Width = 100 };
    private readonly TextBox _proxyUser = new();
    private readonly TextBox _proxyPass = new() { UseSystemPasswordChar = true };
    private readonly TextBox _proxyPaste = new() { PlaceholderText = "socks5://user:pass@host:port  or  host:port:user:pass" };
    private readonly Label _proxyResult = new() { AutoSize = true, MaximumSize = new Size(480, 0), ForeColor = SystemColors.GrayText };
    private readonly Button _proxyCheck = new() { Text = "Check proxy", AutoSize = true };

    // Browser
    private readonly TextBox _userAgent = Multiline(3);
    private readonly ComboBox _language = new() { DropDownStyle = ComboBoxStyle.DropDown };
    private readonly NumericUpDown _width = new() { Minimum = 0, Maximum = 7680, Width = 90 };
    private readonly NumericUpDown _height = new() { Minimum = 0, Maximum = 4320, Width = 90 };
    private readonly CheckBox _blockWebRtc = new() { Text = "Block WebRTC from exposing the real IP", AutoSize = true };
    private readonly TextBox _extraArgs = Multiline(3);

    // Fingerprint
    private readonly CheckBox _fpEnabled = new() { Text = "Spoof this profile's device fingerprint", AutoSize = true };
    private readonly ComboBox _fpPlatform = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };
    private readonly ComboBox _fpTimezone = new() { DropDownStyle = ComboBoxStyle.DropDown };
    private readonly CheckBox _fpGeo = new() { Text = "Report the proxy IP's location to sites that ask for it", AutoSize = true };
    private readonly ComboBox _fpCores = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 80 };
    private readonly ComboBox _fpMemory = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 80 };
    private readonly ComboBox _fpScreen = new() { DropDownStyle = ComboBoxStyle.DropDown, Width = 140 };
    private readonly ComboBox _fpGpu = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly CheckBox _fpCanvas = new() { Text = "Canvas noise", AutoSize = true };
    private readonly CheckBox _fpAudio = new() { Text = "Audio noise", AutoSize = true };
    private readonly CheckBox _fpWebGl = new() { Text = "WebGL / WebGPU vendor", AutoSize = true };
    private readonly List<Control> _fpControls = new();
    private bool _loading;

    private const string AutoTimezone = "Auto (from proxy IP)";
    private static readonly string[] Timezones =
    {
        AutoTimezone,
        "America/New_York", "America/Chicago", "America/Denver", "America/Los_Angeles", "America/Phoenix",
        "America/Anchorage", "America/Toronto", "America/Vancouver", "America/Mexico_City", "America/Sao_Paulo",
        "America/Argentina/Buenos_Aires", "America/Bogota", "America/Lima", "Europe/London", "Europe/Dublin",
        "Europe/Lisbon", "Europe/Paris", "Europe/Berlin", "Europe/Madrid", "Europe/Rome", "Europe/Amsterdam",
        "Europe/Brussels", "Europe/Zurich", "Europe/Stockholm", "Europe/Warsaw", "Europe/Prague", "Europe/Vienna",
        "Europe/Athens", "Europe/Bucharest", "Europe/Kyiv", "Europe/Istanbul", "Europe/Moscow", "Asia/Dubai",
        "Asia/Karachi", "Asia/Kolkata", "Asia/Dhaka", "Asia/Bangkok", "Asia/Ho_Chi_Minh", "Asia/Jakarta",
        "Asia/Singapore", "Asia/Kuala_Lumpur", "Asia/Manila", "Asia/Hong_Kong", "Asia/Shanghai", "Asia/Taipei",
        "Asia/Seoul", "Asia/Tokyo", "Australia/Perth", "Australia/Sydney", "Australia/Melbourne",
        "Pacific/Auckland", "Africa/Cairo", "Africa/Johannesburg", "Africa/Lagos", "Africa/Nairobi"
    };

    private static readonly string[] Languages =
    {
        "", "en-US", "en-GB", "de-DE", "fr-FR", "es-ES", "it-IT", "pt-BR", "pt-PT", "nl-NL", "pl-PL",
        "ru-RU", "uk-UA", "tr-TR", "ja-JP", "ko-KR", "zh-CN", "zh-TW", "vi-VN", "th-TH", "id-ID", "ar-SA", "hi-IN"
    };

    public ProfileForm(Profile profile, string? browserPath)
    {
        _profile = profile;
        _fp = profile.Fingerprint.Copy();

        Text = $"Profile — {profile.Name}";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(680, 680);
        Font = new Font("Segoe UI", 9.5f);

        var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(14, 5) };
        tabs.TabPages.Add(BuildGeneralTab());
        tabs.TabPages.Add(BuildProxyTab());
        tabs.TabPages.Add(BuildFingerprintTab());
        tabs.TabPages.Add(BuildBrowserTab());

        var ok = new Button { Text = "Save", DialogResult = DialogResult.OK, Width = 100, Height = 32 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 100, Height = 32 };
        ok.Click += OnSave;
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 50, Padding = new Padding(8)
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        AcceptButton = ok;
        CancelButton = cancel;

        Controls.Add(tabs);
        Controls.Add(buttons);
        LoadFromProfile();
    }

    private static TextBox Multiline(int lines) => new()
    {
        Multiline = true, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true, Height = 22 * lines + 6
    };

    private static TableLayoutPanel NewGrid()
    {
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(12), AutoScroll = true };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return grid;
    }

    private static void AddRow(TableLayoutPanel grid, string label, Control control, string? hint = null)
    {
        control.Dock = control is NumericUpDown or CheckBox or Button or FlowLayoutPanel ? DockStyle.None : DockStyle.Fill;
        control.Margin = new Padding(3, 4, 3, hint == null ? 4 : 0);
        grid.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(3, 8, 3, 3) });
        grid.Controls.Add(control);
        if (hint != null)
        {
            grid.Controls.Add(new Label());
            grid.Controls.Add(new Label
            {
                Text = hint, AutoSize = true, MaximumSize = new Size(460, 0),
                ForeColor = SystemColors.GrayText, Margin = new Padding(3, 0, 3, 8)
            });
        }
    }

    private TabPage BuildGeneralTab()
    {
        var grid = NewGrid();
        AddRow(grid, "Name", _name);
        AddRow(grid, "Tags", _tags, "Comma-separated, e.g. shop, client-a. Searchable from the main window.");
        AddRow(grid, "Notes", _notes);
        AddRow(grid, "Start URLs", _startUrls, "One per line. Opened every time the profile launches.");
        var page = new TabPage("General");
        page.Controls.Add(grid);
        return page;
    }

    private TabPage BuildProxyTab()
    {
        _proxyType.Items.AddRange(new object[] { "No proxy", "HTTP / HTTPS", "SOCKS5" });
        _proxyType.SelectedIndexChanged += (_, _) => UpdateProxyEnabled();

        var paste = new Button { Text = "Fill", AutoSize = true };
        paste.Click += (_, _) => ApplyPastedProxy();
        var pasteRow = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        pasteRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        pasteRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _proxyPaste.Dock = DockStyle.Fill;
        pasteRow.Controls.Add(_proxyPaste);
        pasteRow.Controls.Add(paste);

        _proxyCheck.Click += async (_, _) => await CheckProxy();

        var grid = NewGrid();
        AddRow(grid, "Quick paste", pasteRow, "Paste a proxy string and click Fill to split it into the fields below.");
        AddRow(grid, "Type", _proxyType);
        AddRow(grid, "Host", _proxyHost);
        AddRow(grid, "Port", _proxyPort);
        AddRow(grid, "Username", _proxyUser);
        AddRow(grid, "Password", _proxyPass, "Stored encrypted with your Windows account.");
        AddRow(grid, "", _proxyCheck);
        AddRow(grid, "", _proxyResult);
        var page = new TabPage("Proxy");
        page.Controls.Add(grid);
        return page;
    }

    private TabPage BuildFingerprintTab()
    {
        _fpPlatform.Items.AddRange(Fingerprint.Platforms.Cast<object>().ToArray());
        _fpPlatform.SelectedIndexChanged += (_, _) =>
        {
            if (_loading) return;
            // Screen sizes and GPUs differ per platform, so re-roll them to stay consistent.
            Randomize(keepSeed: true);
        };
        _fpTimezone.Items.AddRange(Timezones.Cast<object>().ToArray());
        _fpCores.Items.AddRange(Fingerprint.CpuChoices.Cast<object>().ToArray());
        _fpMemory.Items.AddRange(Fingerprint.MemoryChoices.Cast<object>().ToArray());
        _fpEnabled.CheckedChanged += (_, _) => UpdateFingerprintEnabled();

        var randomize = new Button { Text = "🎲  New random fingerprint", AutoSize = true };
        randomize.Click += (_, _) => Randomize(keepSeed: false);

        var hw = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        hw.Controls.Add(new Label { Text = "CPU cores", AutoSize = true, Margin = new Padding(0, 7, 6, 0) });
        hw.Controls.Add(_fpCores);
        hw.Controls.Add(new Label { Text = "Memory (GB)", AutoSize = true, Margin = new Padding(16, 7, 6, 0) });
        hw.Controls.Add(_fpMemory);

        var noise = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        noise.Controls.AddRange(new Control[] { _fpCanvas, _fpAudio, _fpWebGl });

        var grid = NewGrid();
        AddRow(grid, "", _fpEnabled);
        AddRow(grid, "Platform", _fpPlatform, "Sets the user agent, client hints and navigator.platform to match.");
        AddRow(grid, "Timezone", _fpTimezone, "Auto matches the proxy's IP. Without a proxy, your real timezone is kept.");
        AddRow(grid, "Geolocation", _fpGeo);
        AddRow(grid, "Hardware", hw);
        AddRow(grid, "Screen", _fpScreen);
        AddRow(grid, "GPU", _fpGpu);
        AddRow(grid, "Protection", noise, "Noise makes Canvas/audio hashes unique per profile but stable across launches. " +
                                          "It's done by script injection, which advanced anti-bot systems can sometimes detect.");
        AddRow(grid, "", randomize);

        _fpControls.AddRange(new Control[] { _fpPlatform, _fpTimezone, _fpGeo, hw, _fpScreen, _fpGpu, noise, randomize });

        var page = new TabPage("Fingerprint");
        page.Controls.Add(grid);
        return page;
    }

    private void Randomize(bool keepSeed)
    {
        var platform = (string?)_fpPlatform.SelectedItem ?? Fingerprint.Windows;
        var fresh = Fingerprint.Generate(platform);
        ReadFingerprintControls();
        _fp.Platform = platform;
        _fp.HardwareConcurrency = fresh.HardwareConcurrency;
        _fp.DeviceMemory = fresh.DeviceMemory;
        _fp.ScreenWidth = fresh.ScreenWidth;
        _fp.ScreenHeight = fresh.ScreenHeight;
        _fp.WebGlVendor = fresh.WebGlVendor;
        _fp.WebGlRenderer = fresh.WebGlRenderer;
        if (!keepSeed) _fp.NoiseSeed = fresh.NoiseSeed;
        LoadFingerprintControls();
    }

    private void LoadFingerprintControls()
    {
        _loading = true;
        _fpEnabled.Checked = _fp.Enabled;
        _fpPlatform.SelectedItem = Fingerprint.Platforms.Contains(_fp.Platform) ? _fp.Platform : Fingerprint.Windows;
        _fpTimezone.Text = string.IsNullOrEmpty(_fp.Timezone) ? AutoTimezone : _fp.Timezone;
        _fpGeo.Checked = _fp.GeolocationFromIp;
        SelectOrAdd(_fpCores, _fp.HardwareConcurrency);
        SelectOrAdd(_fpMemory, _fp.DeviceMemory);

        _fpScreen.Items.Clear();
        _fpScreen.Items.AddRange(Fingerprint.ScreensFor(_fp.Platform).Distinct().Select(s => (object)$"{s.W}×{s.H}").ToArray());
        _fpScreen.Text = $"{_fp.ScreenWidth}×{_fp.ScreenHeight}";

        _fpGpu.Items.Clear();
        _fpGpu.Items.AddRange(Fingerprint.GpusFor(_fp.Platform).Select(g => (object)g.Renderer).ToArray());
        if (!_fpGpu.Items.Contains(_fp.WebGlRenderer)) _fpGpu.Items.Add(_fp.WebGlRenderer);
        _fpGpu.SelectedItem = _fp.WebGlRenderer;

        _fpCanvas.Checked = _fp.CanvasNoise;
        _fpAudio.Checked = _fp.AudioNoise;
        _fpWebGl.Checked = _fp.WebGlSpoof;
        _loading = false;
        UpdateFingerprintEnabled();
    }

    private static void SelectOrAdd(ComboBox combo, int value)
    {
        if (!combo.Items.Contains(value)) combo.Items.Add(value);
        combo.SelectedItem = value;
    }

    /// <summary>Copies the fingerprint controls into _fp. Returns an error message, or null.</summary>
    private string? ReadFingerprintControls()
    {
        _fp.Enabled = _fpEnabled.Checked;
        _fp.Platform = (string?)_fpPlatform.SelectedItem ?? Fingerprint.Windows;
        var tz = _fpTimezone.Text.Trim();
        _fp.Timezone = tz == AutoTimezone ? "" : tz;
        _fp.GeolocationFromIp = _fpGeo.Checked;
        if (_fpCores.SelectedItem is int cores) _fp.HardwareConcurrency = cores;
        if (_fpMemory.SelectedItem is int mem) _fp.DeviceMemory = mem;

        var screen = _fpScreen.Text.Split('×', 'x', 'X', '*');
        if (screen.Length == 2 && int.TryParse(screen[0].Trim(), out var w) && int.TryParse(screen[1].Trim(), out var h)
            && w >= 640 && h >= 480)
            (_fp.ScreenWidth, _fp.ScreenHeight) = (w, h);
        else if (_fp.Enabled)
            return "Enter the screen size as width×height, e.g. 1920×1080.";

        if (_fpGpu.SelectedItem is string renderer)
        {
            _fp.WebGlRenderer = renderer;
            var known = Fingerprint.Platforms.SelectMany(Fingerprint.GpusFor).FirstOrDefault(g => g.Renderer == renderer);
            if (known.Vendor != null) _fp.WebGlVendor = known.Vendor;
        }
        _fp.CanvasNoise = _fpCanvas.Checked;
        _fp.AudioNoise = _fpAudio.Checked;
        _fp.WebGlSpoof = _fpWebGl.Checked;

        if (_fp.Enabled && _fp.Timezone.Length > 0 && !IsValidTimezone(_fp.Timezone))
            return $"\"{_fp.Timezone}\" isn't a known timezone. Use an IANA name like Europe/Berlin, or Auto.";
        return null;
    }

    private static bool IsValidTimezone(string iana)
    {
        try { TimeZoneInfo.FindSystemTimeZoneById(iana); return true; }
        catch (TimeZoneNotFoundException) { return false; }
    }

    private void UpdateFingerprintEnabled()
    {
        foreach (var c in _fpControls) c.Enabled = _fpEnabled.Checked;
    }

    private TabPage BuildBrowserTab()
    {
        _language.Items.AddRange(Languages.Cast<object>().ToArray());

        var size = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        size.Controls.Add(_width);
        size.Controls.Add(new Label { Text = "×", AutoSize = true, Margin = new Padding(6, 6, 6, 0) });
        size.Controls.Add(_height);

        var grid = NewGrid();
        AddRow(grid, "Custom user agent", _userAgent, "Leave empty (recommended): it's generated from the Fingerprint platform " +
                                                      "and your installed browser version, so everything stays consistent.");
        AddRow(grid, "Language", _language, "Browser UI and Accept-Language, e.g. en-US. Empty = system default.");
        AddRow(grid, "Window size", size, "0 × 0 = browser default.");
        AddRow(grid, "WebRTC", _blockWebRtc);
        AddRow(grid, "Extra switches", _extraArgs, "Advanced: extra Chromium command-line switches, one per line.");
        var page = new TabPage("Browser");
        page.Controls.Add(grid);
        return page;
    }

    private void LoadFromProfile()
    {
        var p = _profile;
        _name.Text = p.Name;
        _tags.Text = p.Tags;
        _notes.Text = ToWindowsLines(p.Notes);
        _startUrls.Text = ToWindowsLines(p.StartUrls);
        _proxyType.SelectedIndex = (int)p.ProxyType;
        _proxyHost.Text = p.ProxyHost;
        _proxyPort.Value = p.ProxyPort;
        _proxyUser.Text = p.ProxyUser;
        _proxyPass.Text = p.ProxyPassword;
        _userAgent.Text = p.UserAgent;
        _language.Text = p.Language;
        _width.Value = p.WindowWidth;
        _height.Value = p.WindowHeight;
        _blockWebRtc.Checked = p.BlockWebRtcLeak;
        _extraArgs.Text = ToWindowsLines(p.ExtraArgs);
        UpdateProxyEnabled();
        LoadFingerprintControls();
    }

    private void SaveTo(Profile p)
    {
        p.Name = _name.Text.Trim();
        p.Tags = _tags.Text.Trim();
        p.Notes = _notes.Text.Trim();
        p.StartUrls = _startUrls.Text.Trim();
        p.ProxyType = (ProxyType)_proxyType.SelectedIndex;
        p.ProxyHost = _proxyHost.Text.Trim();
        p.ProxyPort = (int)_proxyPort.Value;
        p.ProxyUser = _proxyUser.Text.Trim();
        p.ProxyPassword = _proxyPass.Text;
        p.UserAgent = _userAgent.Text.Trim().Replace("\r", "").Replace("\n", " ");
        p.Language = _language.Text.Trim();
        p.WindowWidth = (int)_width.Value;
        p.WindowHeight = (int)_height.Value;
        p.BlockWebRtcLeak = _blockWebRtc.Checked;
        p.ExtraArgs = _extraArgs.Text.Trim();
    }

    private static string ToWindowsLines(string s) => s.Replace("\r\n", "\n").Replace("\n", "\r\n");

    private void OnSave(object? sender, EventArgs e)
    {
        string? error = null;
        if (string.IsNullOrWhiteSpace(_name.Text))
            error = "Enter a profile name.";
        else if (_proxyType.SelectedIndex > 0 && (string.IsNullOrWhiteSpace(_proxyHost.Text) || _proxyPort.Value == 0))
            error = "Enter the proxy host and port, or set the proxy type to \"No proxy\".";
        else
            error = ReadFingerprintControls();

        if (error != null)
        {
            MessageBox.Show(this, error, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            DialogResult = DialogResult.None;
            return;
        }
        SaveTo(_profile);
        _profile.Fingerprint = _fp.Copy();
    }

    private void UpdateProxyEnabled()
    {
        var on = _proxyType.SelectedIndex > 0;
        _proxyHost.Enabled = _proxyPort.Enabled = _proxyUser.Enabled = _proxyPass.Enabled = on;
    }

    private async Task CheckProxy()
    {
        var temp = new Profile();
        SaveTo(temp);
        _proxyCheck.Enabled = false;
        _proxyResult.ForeColor = SystemColors.GrayText;
        _proxyResult.Text = temp.HasProxy ? "Checking…" : "No proxy set — checking your direct connection…";

        var result = await ProxyChecker.CheckAsync(temp);
        _proxyResult.ForeColor = result.Ok ? Color.SeaGreen : Color.Firebrick;
        _proxyResult.Text = (result.Ok ? "✔ " : "✘ ") + result.Message;
        _proxyCheck.Enabled = true;
    }

    /// <summary>Accepts scheme://user:pass@host:port, host:port:user:pass, user:pass@host:port or host:port.</summary>
    private void ApplyPastedProxy()
    {
        var s = _proxyPaste.Text.Trim();
        if (s.Length == 0) return;

        var type = ProxyType.Http;
        var schemeEnd = s.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd >= 0)
        {
            type = s[..schemeEnd].ToLowerInvariant().StartsWith("socks") ? ProxyType.Socks5 : ProxyType.Http;
            s = s[(schemeEnd + 3)..];
        }
        else if (_proxyType.SelectedIndex > 0)
        {
            type = (ProxyType)_proxyType.SelectedIndex;
        }

        string hostPort, user = "", pass = "";
        var at = s.LastIndexOf('@');
        if (at >= 0)
        {
            var creds = s[..at];
            hostPort = s[(at + 1)..];
            var c = creds.IndexOf(':');
            (user, pass) = c >= 0 ? (creds[..c], creds[(c + 1)..]) : (creds, "");
        }
        else
        {
            var parts = s.Split(':');
            if (parts.Length >= 4)
            {
                user = parts[2];
                pass = string.Join(":", parts.Skip(3));
            }
            hostPort = parts.Length >= 2 ? $"{parts[0]}:{parts[1]}" : s;
        }

        var colon = hostPort.LastIndexOf(':');
        if (colon < 0 || !int.TryParse(hostPort[(colon + 1)..].TrimEnd('/'), out var port) || port is < 1 or > 65535)
        {
            MessageBox.Show(this, "Couldn't find a host and port in that text.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _proxyType.SelectedIndex = (int)type;
        _proxyHost.Text = hostPort[..colon];
        _proxyPort.Value = port;
        _proxyUser.Text = Uri.UnescapeDataString(user);
        _proxyPass.Text = Uri.UnescapeDataString(pass);
        _proxyPaste.Clear();
    }
}
