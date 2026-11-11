using System.Text.Json;
using IProgressReport = LibHac.Common.IProgressReport;

namespace NxExtractor;

public sealed class MainForm : Form
{
    private readonly TextBox _prod = new(), _title = new(), _base = new(), _update = new(), _out = new();
    private readonly ListBox _dlc = new() { SelectionMode = SelectionMode.MultiExtended, IntegralHeight = false };
    private readonly CheckBox _cExe = new() { Text = "ExeFS", Checked = true, AutoSize = true };
    private readonly CheckBox _cRom = new() { Text = "RomFS", Checked = true, AutoSize = true };
    private readonly CheckBox _cNacp = new() { Checked = true, AutoSize = true };
    private readonly CheckBox _cDlc = new() { Text = "DLC RomFS", Checked = true, AutoSize = true };
    private readonly Button _go = new() { Height = 36, Dock = DockStyle.Fill };
    private readonly ProgressBar _bar = new() { Dock = DockStyle.Fill, Maximum = 1000 };
    private readonly TextBox _log = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill,
        Font = new Font("Consolas", 9f), BackColor = SystemColors.Window
    };
    private readonly Button _langBtn = new() { AutoSize = true, Anchor = AnchorStyles.Right, FlatStyle = FlatStyle.System };

    // Controles cuyo texto sale de la tabla de idiomas; se actualizan al cambiar de idioma.
    private readonly List<(Control control, string key)> _localized = new();

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NxExtractor", "settings.json");

    public MainForm()
    {
        LoadLanguageSetting();

        var v = typeof(MainForm).Assembly.GetName().Version!;
        Text = $"NX Extractor v{v.Major}.{v.Minor}.{v.Build} — NSP / NSZ / XCI";
        Width = 820; Height = 880;
        MinimumSize = new Size(660, 720);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9.5f);
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { }

        // Fila superior: selector de idioma a la derecha. Debajo, claves (compartidas) y pestañas.
        var outer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(10, 4, 10, 10) };
        outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(outer);

        _langBtn.Click += (_, _) => ShowLanguageDialog();
        outer.Controls.Add(_langBtn);

        outer.Controls.Add(Group("keys", new[]
        {
            Row("prod.keys", _prod, BrowseFile(_prod, "filterKeys")),
            Row("titleKeysOpt", _title, BrowseFile(_title, "filterKeys"))
        }));
        var tabs = new TabControl { Dock = DockStyle.Fill };
        outer.Controls.Add(tabs);
        var extractTab = Loc(new TabPage(), "tabExtract");
        tabs.TabPages.Add(extractTab);
        tabs.TabPages.Add(BuildConvertTab());

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(6) };
        extractTab.Controls.Add(root);

        root.Controls.Add(Group("game", new[]
        {
            Row("baseGame", _base, BrowseFile(_base, "filterRoms"), Clear(_base)),
            Row("update", _update, BrowseFile(_update, "filterRoms"), Clear(_update))
        }));

        var dlcGroup = Loc(new GroupBox { Dock = DockStyle.Fill, Height = 150, Padding = new Padding(8) }, "dlc");
        var dlcButtons = new FlowLayoutPanel { Dock = DockStyle.Right, FlowDirection = FlowDirection.TopDown, Width = 120 };
        var addDlc = Loc(new Button { Width = 110 }, "add");
        var remDlc = Loc(new Button { Width = 110 }, "remove");
        addDlc.Click += (_, _) =>
        {
            using var d = new OpenFileDialog { Filter = L.T("filterRoms"), Multiselect = true };
            if (d.ShowDialog(this) == DialogResult.OK) foreach (var f in d.FileNames) _dlc.Items.Add(f);
        };
        remDlc.Click += (_, _) =>
        {
            foreach (var i in _dlc.SelectedItems.Cast<object>().ToList()) _dlc.Items.Remove(i);
        };
        dlcButtons.Controls.AddRange(new Control[] { addDlc, remDlc });
        _dlc.Dock = DockStyle.Fill;
        _dlc.AllowDrop = true;
        _dlc.DragEnter += (_, e) => e.Effect = e.Data!.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        _dlc.DragDrop += (_, e) => { foreach (var f in (string[])e.Data!.GetData(DataFormats.FileDrop)!) _dlc.Items.Add(f); };
        dlcGroup.Controls.Add(_dlc);
        dlcGroup.Controls.Add(dlcButtons);
        root.Controls.Add(dlcGroup);

        Loc(_cNacp, "nacpIcons");
        var opts = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        opts.Controls.AddRange(new Control[] { _cExe, _cRom, _cNacp, _cDlc });
        root.Controls.Add(Group("output", new Control[]
        {
            Row("folder", _out, BrowseFolder(_out)),
            opts
        }));

        Loc(_go, "extractBtn");
        var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Height = 40 };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
        actions.Controls.Add(_bar, 0, 0);
        actions.Controls.Add(_go, 1, 0);
        root.Controls.Add(actions);
        root.Controls.Add(_log);
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 150));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        foreach (var tb in new[] { _base, _update, _prod, _title }) EnableDrop(tb);

        _go.Click += async (_, _) => await RunAsync();
        LoadSettings();
        ApplyLanguage();
        FormClosing += (_, _) => SaveSettings();
    }

    // ---------- Idioma ----------

    private T Loc<T>(T control, string key) where T : Control
    {
        _localized.Add((control, key));
        return control;
    }

    private void ApplyLanguage()
    {
        SuspendLayout();
        foreach (var (control, key) in _localized) control.Text = L.T(key);
        _langBtn.Text = "🌐 " + L.CurrentName;
        ResumeLayout(true);
    }

    private void ShowLanguageDialog()
    {
        using var dlg = new Form
        {
            Text = L.T("language"),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false,
            ClientSize = new Size(300, 250),
            Font = Font, Icon = Icon
        };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(12) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        dlg.Controls.Add(layout);

        layout.Controls.Add(new Label { Text = L.T("chooseLanguage"), AutoSize = true, Margin = new Padding(0, 0, 0, 6) });
        var list = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false, ItemHeight = 22, DrawMode = DrawMode.OwnerDrawFixed };
        foreach (var lang in L.Languages) list.Items.Add(lang.Name);
        list.SelectedIndex = Array.FindIndex(L.Languages, l => l.Code == L.Current);
        list.DrawItem += (_, e) =>
        {
            if (e.Index < 0) return;
            e.DrawBackground();
            TextRenderer.DrawText(e.Graphics, (string)list.Items[e.Index], list.Font, e.Bounds, e.ForeColor,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.LeftAndRightPadding);
            e.DrawFocusRectangle();
        };
        layout.Controls.Add(list);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Margin = new Padding(0, 8, 0, 0) };
        var ok = new Button { Text = L.T("ok"), DialogResult = DialogResult.OK, Width = 90 };
        var cancel = new Button { Text = L.T("cancel"), DialogResult = DialogResult.Cancel, Width = 90 };
        buttons.Controls.AddRange(new Control[] { cancel, ok });
        layout.Controls.Add(buttons);
        dlg.AcceptButton = ok;
        dlg.CancelButton = cancel;
        list.DoubleClick += (_, _) => { dlg.DialogResult = DialogResult.OK; };

        if (dlg.ShowDialog(this) == DialogResult.OK && list.SelectedIndex >= 0)
        {
            L.Current = L.Languages[list.SelectedIndex].Code;
            ApplyLanguage();
            SaveSettings();
        }
    }

    // ---------- Pestaña Convertir ----------

    private readonly TextBox _convOut = new();

    private TabPage BuildConvertTab()
    {
        var page = Loc(new TabPage(), "tabConvert");
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(6) };
        page.Controls.Add(root);

        var files = new ListBox { Dock = DockStyle.Fill, SelectionMode = SelectionMode.MultiExtended, IntegralHeight = false, AllowDrop = true };
        var filesGroup = Loc(new GroupBox { Dock = DockStyle.Fill, Padding = new Padding(8) }, "filesGroup");
        var btns = new FlowLayoutPanel { Dock = DockStyle.Right, FlowDirection = FlowDirection.TopDown, Width = 120 };
        var add = Loc(new Button { Width = 110 }, "add");
        var addFolder = Loc(new Button { Width = 110 }, "addFolder");
        var rem = Loc(new Button { Width = 110 }, "remove");
        var clr = Loc(new Button { Width = 110 }, "clearList");
        add.Click += (_, _) =>
        {
            using var d = new OpenFileDialog { Filter = L.T("filterRoms"), Multiselect = true };
            if (d.ShowDialog(this) == DialogResult.OK) files.Items.AddRange(d.FileNames);
        };
        addFolder.Click += (_, _) =>
        {
            using var d = new FolderBrowserDialog();
            if (d.ShowDialog(this) == DialogResult.OK) files.Items.Add(d.SelectedPath);
        };
        rem.Click += (_, _) => { foreach (var i in files.SelectedItems.Cast<object>().ToList()) files.Items.Remove(i); };
        clr.Click += (_, _) => files.Items.Clear();
        files.DragEnter += (_, e) => e.Effect = e.Data!.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        files.DragDrop += (_, e) => files.Items.AddRange((string[])e.Data!.GetData(DataFormats.FileDrop)!);
        btns.Controls.AddRange(new Control[] { add, addFolder, rem, clr });
        filesGroup.Controls.Add(files);
        filesGroup.Controls.Add(btns);
        root.Controls.Add(filesGroup);

        var toNsp = Loc(new RadioButton { Checked = true, AutoSize = true }, "toNsp");
        var toNsz = Loc(new RadioButton { AutoSize = true }, "toNsz");
        var toNup = Loc(new RadioButton { AutoSize = true }, "toNup");
        var level = new NumericUpDown { Minimum = 1, Maximum = 22, Value = 18, Width = 60 };
        var levelLbl = Loc(new Label { AutoSize = true, Margin = new Padding(16, 6, 0, 0) }, "zstdLevel");
        var modeRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        modeRow.Controls.AddRange(new Control[] { toNsp, toNsz, toNup, levelLbl, level });
        toNsz.CheckedChanged += (_, _) => level.Enabled = levelLbl.Enabled = toNsz.Checked;
        level.Enabled = levelLbl.Enabled = false;
        root.Controls.Add(Group("conversion", new Control[] { modeRow, Row("outputFolder", _convOut, BrowseFolder(_convOut)) }));

        var bar = new ProgressBar { Dock = DockStyle.Fill, Maximum = 1000 };
        var go = Loc(new Button { Height = 36, Dock = DockStyle.Fill }, "convertBtn");
        var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
        actions.Controls.Add(bar, 0, 0);
        actions.Controls.Add(go, 1, 0);
        root.Controls.Add(actions);

        var log = new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill,
            Font = new Font("Consolas", 9f), BackColor = SystemColors.Window
        };
        root.Controls.Add(log);
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 200));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        void Log(string m)
        {
            if (log.InvokeRequired) { log.BeginInvoke(() => Log(m)); return; }
            log.AppendText(m + Environment.NewLine);
        }

        go.Click += async (_, _) =>
        {
            var inputs = files.Items.Cast<string>().ToList();
            string outDir = _convOut.Text.Trim();
            bool nup = toNup.Checked;
            string err =
                !nup && !File.Exists(_prod.Text.Trim()) ? L.T("errProd") :
                inputs.Count == 0 ? L.T("errNoFiles") :
                nup && inputs.Any(f => !Directory.Exists(f)) ? L.T("errNotFolder") :
                !nup && inputs.Any(f => !File.Exists(f)) ? L.T("errMissing") :
                outDir == "" ? L.T("errOut") : null;
            if (err != null) { MessageBox.Show(this, err, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }

            var target = nup ? ConvertTarget.Nup : toNsz.Checked ? ConvertTarget.Nsz : ConvertTarget.Nsp;
            SaveSettings();
            go.Enabled = false;
            log.Clear();
            int ok = 0;
            try
            {
                Directory.CreateDirectory(outDir);
                var conv = new Converter(_prod.Text.Trim(), _title.Text.Trim(), Log, new UiProgress(this, bar)) { Level = (int)level.Value };
                await Task.Run(() =>
                {
                    foreach (var f in inputs)
                    {
                        try { conv.Convert(f, outDir, target); ok++; }
                        catch (Exception ex) { Log($"  ERROR: {ex.Message}"); }
                    }
                });
                bar.Value = bar.Maximum;
                Log(L.F("convDoneLog", ok, inputs.Count));
                if (MessageBox.Show(this, L.F("convDoneMsg", ok, inputs.Count), Text,
                        MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                    System.Diagnostics.Process.Start("explorer.exe", outDir);
            }
            catch (Exception ex)
            {
                Log("ERROR: " + ex.Message);
                MessageBox.Show(this, ex.Message, L.T("error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally { go.Enabled = true; }
        };
        return page;
    }

    // ---------- UI helpers ----------

    private GroupBox Group(string titleKey, Control[] rows)
    {
        var g = Loc(new GroupBox { Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(8) }, titleKey);
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, AutoSize = true };
        foreach (var r in rows) t.Controls.Add(r);
        g.Controls.Add(t);
        return g;
    }

    private Control Row(string labelKey, TextBox box, params Button[] buttons)
    {
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 + buttons.Length, AutoSize = true };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        t.Controls.Add(Loc(new Label { AutoSize = true, Anchor = AnchorStyles.Left }, labelKey));
        box.Dock = DockStyle.Fill;
        t.Controls.Add(box);
        foreach (var b in buttons) t.Controls.Add(b);
        return t;
    }

    private Button BrowseFile(TextBox target, string filterKey)
    {
        var b = new Button { Text = "...", Width = 36 };
        b.Click += (_, _) =>
        {
            using var d = new OpenFileDialog { Filter = L.T(filterKey) };
            if (d.ShowDialog(this) == DialogResult.OK) target.Text = d.FileName;
        };
        return b;
    }

    private Button BrowseFolder(TextBox target)
    {
        var b = new Button { Text = "...", Width = 36 };
        b.Click += (_, _) =>
        {
            using var d = new FolderBrowserDialog { SelectedPath = target.Text };
            if (d.ShowDialog(this) == DialogResult.OK) target.Text = d.SelectedPath;
        };
        return b;
    }

    private static Button Clear(TextBox target)
    {
        var b = new Button { Text = "✕", Width = 36 };
        b.Click += (_, _) => target.Text = "";
        return b;
    }

    private static void EnableDrop(TextBox tb)
    {
        tb.AllowDrop = true;
        tb.DragEnter += (_, e) => e.Effect = e.Data!.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        tb.DragDrop += (_, e) => tb.Text = ((string[])e.Data!.GetData(DataFormats.FileDrop)!)[0];
    }

    private void Log(string msg)
    {
        if (InvokeRequired) { BeginInvoke(() => Log(msg)); return; }
        _log.AppendText(msg + Environment.NewLine);
    }

    // ---------- Run ----------

    private async Task RunAsync()
    {
        var o = new ExtractOptions
        {
            ProdKeys = _prod.Text.Trim(), TitleKeys = _title.Text.Trim(), OutputDir = _out.Text.Trim(),
            BaseFile = _base.Text.Trim(), UpdateFile = _update.Text.Trim(),
            DlcFiles = _dlc.Items.Cast<string>().ToList(),
            ExeFs = _cExe.Checked, RomFs = _cRom.Checked, Nacp = _cNacp.Checked, DlcRomFs = _cDlc.Checked
        };

        string err =
            !File.Exists(o.ProdKeys) && !new[] { o.BaseFile, o.UpdateFile }.Concat(o.DlcFiles).Where(f => f != "").All(Extractor.IsNup) ? L.T("errProd") :
            o.TitleKeys != "" && !File.Exists(o.TitleKeys) ? L.T("errTitle") :
            o.BaseFile == "" && o.UpdateFile == "" && o.DlcFiles.Count == 0 ? L.T("errNoInput") :
            new[] { o.BaseFile, o.UpdateFile }.Concat(o.DlcFiles).Any(f => f != "" && !File.Exists(f)) ? L.T("errMissing") :
            o.OutputDir == "" ? L.T("errOut") : null;
        if (err != null) { MessageBox.Show(this, err, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }

        SaveSettings();
        _go.Enabled = false;
        _log.Clear();
        _bar.Value = 0;
        var progress = new UiProgress(this, _bar);
        try
        {
            Directory.CreateDirectory(o.OutputDir);
            await Task.Run(() =>
            {
                using var ex = new Extractor(o, Log, progress);
                ex.Run();
            });
            _bar.Value = _bar.Maximum;
            if (MessageBox.Show(this, L.T("extractDone"), Text,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                System.Diagnostics.Process.Start("explorer.exe", o.OutputDir);
        }
        catch (Exception ex)
        {
            Log("ERROR: " + ex.Message);
            MessageBox.Show(this, ex.Message, L.T("error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _go.Enabled = true;
        }
    }

    // ---------- Settings ----------

    private sealed class Settings
    {
        public string Prod { get; set; } = "";
        public string Title { get; set; } = "";
        public string Out { get; set; } = "";
        public string ConvOut { get; set; } = "";
        public string Lang { get; set; } = "en";
    }

    private static Settings ReadSettings()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath)) ?? new Settings();
        }
        catch { }
        return new Settings();
    }

    private static void LoadLanguageSetting() => L.Current = ReadSettings().Lang;

    private void LoadSettings()
    {
        var s = ReadSettings();
        _prod.Text = s.Prod; _title.Text = s.Title; _out.Text = s.Out; _convOut.Text = s.ConvOut;

        // Si no hay nada guardado, buscar las claves en las rutas habituales.
        if (_prod.Text == "")
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string exeDir = AppContext.BaseDirectory;
            foreach (var dir in new[] { exeDir, Path.Combine(home, ".switch") })
            {
                if (File.Exists(Path.Combine(dir, "prod.keys")))
                {
                    _prod.Text = Path.Combine(dir, "prod.keys");
                    if (File.Exists(Path.Combine(dir, "title.keys"))) _title.Text = Path.Combine(dir, "title.keys");
                    break;
                }
            }
        }
        if (_out.Text == "") _out.Text = Path.Combine(AppContext.BaseDirectory, "extracted");
        if (_convOut.Text == "") _convOut.Text = Path.Combine(AppContext.BaseDirectory, "converted");
    }

    private void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new Settings
            { Prod = _prod.Text, Title = _title.Text, Out = _out.Text, ConvOut = _convOut.Text, Lang = L.Current }));
        }
        catch { }
    }

    private sealed class UiProgress : IProgressReport
    {
        private readonly Control _owner;
        private readonly ProgressBar _bar;
        private long _total, _value;

        public UiProgress(Control owner, ProgressBar bar) { _owner = owner; _bar = bar; }

        public void Report(long value) { _value = value; Update(); }
        public void ReportAdd(long value) { _value += value; Update(); }
        public void SetTotal(long value) { _total = value; _value = 0; Update(); }
        public void LogMessage(string message) { }

        private void Update()
        {
            if (_total <= 0) return;
            int v = (int)Math.Clamp(_value * 1000 / _total, 0, 1000);
            _owner.BeginInvoke(() => _bar.Value = v);
        }
    }
}
