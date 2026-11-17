using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Bf4Gui;

internal sealed class MainForm : Form
{
    private readonly GuiSettings _settings = GuiSettings.Load();
    private readonly TextBox _game = new();
    private readonly TextBox _csv = new();
    private readonly TextBox _fonts = new();
    private readonly TextBox _workspace = new();
    private readonly ComboBox _textBackups = new();
    private readonly ComboBox _fontBackups = new();
    private readonly RichTextBox _log = new();
    private readonly Label _status = new();
    private readonly ProgressBar _progress = new();
    private readonly ToolTip _tips = new() { AutoPopDelay = 8000, InitialDelay = 350 };
    private readonly List<Button> _buttons = [];
    private bool _busy;
    private string? _lastError;

    private static readonly Color Navy = Color.FromArgb(23, 39, 64);
    private static readonly Color Blue = Color.FromArgb(28, 105, 209);
    private static readonly Color SoftBlue = Color.FromArgb(231, 241, 255);
    private static readonly Color TextColor = Color.FromArgb(32, 46, 65);
    private static readonly Color Muted = Color.FromArgb(103, 115, 131);
    private static readonly Color Background = Color.FromArgb(244, 247, 251);

    public MainForm()
    {
        Text = "Battlefield 4 — Bộ công cụ Việt hóa";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(930, 700);
        Size = new Size(1100, 790);
        BackColor = Background;
        ForeColor = TextColor;
        Font = new Font("Segoe UI", 9.5f);
        AutoScaleMode = AutoScaleMode.Dpi;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4,
            Margin = Padding.Empty, Padding = Padding.Empty
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 85));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 64));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 36));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        Controls.Add(root);
        root.Controls.Add(CreateHeader(), 0, 0);
        root.Controls.Add(CreateMainArea(), 0, 1);
        root.Controls.Add(CreateLogArea(), 0, 2);
        root.Controls.Add(CreateFooter(), 0, 3);

        _game.Text = _settings.GameDirectory;
        _csv.Text = _settings.CsvPath;
        _fonts.Text = _settings.FontDirectory;
        _workspace.Text = _settings.WorkspaceDirectory;
        _workspace.TextChanged += (_, _) => RefreshBackups();
        RefreshBackups();
        AppendLog("Sẵn sàng. Chọn đường dẫn rồi dùng các nút Xuất hoặc Cài.");
        FormClosing += (_, _) =>
        {
            try { SaveSettings(); }
            catch { /* A settings write must not prevent closing the window. */ }
        };
    }

    private Control CreateHeader()
    {
        var panel = new Panel { Dock = DockStyle.Fill, BackColor = Navy };
        panel.Controls.Add(new Label
        {
            Text = "BATTLEFIELD 4  /  VIỆT HÓA",
            ForeColor = Color.White, Font = new Font("Segoe UI Semibold", 19f),
            AutoSize = true, Location = new Point(22, 15)
        });
        panel.Controls.Add(new Label
        {
            Text = "Xuất bản dịch, thay font, cài vào game và khôi phục khi cần",
            ForeColor = Color.FromArgb(196, 211, 229),
            AutoSize = true, Location = new Point(24, 54)
        });
        return panel;
    }

    private Control CreateMainArea()
    {
        var split = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1,
            Padding = new Padding(12, 12, 12, 7)
        };
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62));
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
        split.Controls.Add(CreateWorkPanel(), 0, 0);
        split.Controls.Add(CreateBackupPanel(), 1, 0);
        return split;
    }

    private Control CreateWorkPanel()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1,
            Margin = new Padding(0, 0, 7, 0)
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 233));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(CreatePathCard(), 0, 0);
        layout.Controls.Add(CreateActionCard(), 0, 1);
        return layout;
    }

    private Control CreatePathCard()
    {
        var card = Card();
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(new Label
        {
            Text = "Đường dẫn", Dock = DockStyle.Top, Height = 30,
            Font = new Font("Segoe UI Semibold", 12f)
        }, 0, 0);
        var rows = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 4,
            Margin = Padding.Empty
        };
        rows.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        rows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        rows.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        for (var i = 0; i < 4; i++) rows.RowStyles.Add(new RowStyle(SizeType.Percent, 25));
        AddPath(rows, 0, "Thư mục BF4", _game, () => PickFolder(_game));
        AddPath(rows, 1, "CSV dịch", _csv, PickCsv);
        AddPath(rows, 2, "Font GFX", _fonts, () => PickFolder(_fonts));
        AddPath(rows, 3, "Nơi lưu", _workspace, () => PickFolder(_workspace));
        layout.Controls.Add(rows, 0, 1);
        card.Controls.Add(layout);
        return card;
    }

    private void AddPath(TableLayoutPanel table, int row, string caption, TextBox field,
        Action browse)
    {
        var label = new Label
        {
            Text = caption, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Muted, Margin = new Padding(0, 2, 5, 2)
        };
        field.Dock = DockStyle.Fill;
        field.Margin = new Padding(0, 7, 6, 7);
        field.BorderStyle = BorderStyle.FixedSingle;
        var button = SmallButton("Chọn…", browse);
        button.Margin = new Padding(0, 6, 0, 6);
        table.Controls.Add(label, 0, row);
        table.Controls.Add(field, 1, row);
        table.Controls.Add(button, 2, row);
    }

    private Control CreateActionCard()
    {
        var card = Card();
        card.Margin = new Padding(0, 8, 0, 0);
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(new Label
        {
            Text = "Thao tác", Dock = DockStyle.Top, Height = 30,
            Font = new Font("Segoe UI Semibold", 12f)
        }, 0, 0);
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2,
            Margin = Padding.Empty
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        grid.Controls.Add(ActionButton("Xuất text CSV", "Để dịch hoặc chỉnh sửa", false,
            async () => await RunOperationAsync("Xuất text", ExportTextAsync)), 0, 0);
        grid.Controls.Add(ActionButton("Xuất 3 font GFX", "Sửa rồi cài lại được", false,
            async () => await RunOperationAsync("Xuất font", ExportFontsAsync)), 1, 0);
        grid.Controls.Add(ActionButton("Cài text", "Tự kiểm tra và sao lưu", true,
            async () => await RunOperationAsync("Cài text", InstallTextAsync)), 0, 1);
        grid.Controls.Add(ActionButton("Cài 3 font GFX", "Nhận font dài hơn gốc", true,
            async () => await RunOperationAsync("Cài font", InstallFontsAsync)), 1, 1);
        layout.Controls.Add(grid, 0, 1);
        card.Controls.Add(layout);
        return card;
    }

    private Control CreateBackupPanel()
    {
        var card = Card();
        card.Margin = new Padding(7, 0, 0, 0);
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 9,
            Margin = Padding.Empty
        };
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 23));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 23));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        grid.Controls.Add(new Label
        {
            Text = "Khôi phục bản gốc", Font = new Font("Segoe UI Semibold", 12f),
            Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);
        grid.Controls.Add(new Label
        {
            Text = "Mỗi lần cài có một bản sao lưu riêng. Chọn đúng lần cài muốn hoàn tác.",
            ForeColor = Muted, Dock = DockStyle.Fill
        }, 0, 1);
        grid.Controls.Add(MutedLabel("Bản sao lưu text"), 0, 2);
        SetupCombo(_textBackups);
        grid.Controls.Add(_textBackups, 0, 3);
        grid.Controls.Add(SmallButton("Khôi phục text", async () =>
            await RunOperationAsync("Khôi phục text", RestoreTextAsync)), 0, 4);
        grid.Controls.Add(MutedLabel("Bản sao lưu font"), 0, 5);
        SetupCombo(_fontBackups);
        grid.Controls.Add(_fontBackups, 0, 6);
        grid.Controls.Add(SmallButton("Khôi phục font", async () =>
            await RunOperationAsync("Khôi phục font", RestoreFontsAsync)), 0, 7);
        var note = new Label
        {
            Text = "BF4 phải tắt khi cài hoặc khôi phục.\nTool không tự mở game.",
            Dock = DockStyle.Fill, ForeColor = Muted,
            TextAlign = ContentAlignment.BottomLeft
        };
        grid.Controls.Add(note, 0, 8);
        card.Controls.Add(grid);
        return card;
    }

    private Control CreateLogArea()
    {
        var card = Card();
        card.Margin = new Padding(12, 3, 12, 7);
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var top = new Panel { Dock = DockStyle.Top, Height = 35 };
        top.Controls.Add(new Label
        {
            Text = "Nhật ký", Dock = DockStyle.Left, Width = 150,
            Font = new Font("Segoe UI Semibold", 12f)
        });
        var open = SmallButton("Mở nơi lưu", OpenWorkspace);
        open.Dock = DockStyle.Right;
        open.Width = 120;
        top.Controls.Add(open);
        _log.Dock = DockStyle.Fill;
        _log.BackColor = Color.FromArgb(249, 251, 254);
        _log.ForeColor = TextColor;
        _log.Font = new Font("Consolas", 9f);
        _log.ReadOnly = true;
        _log.BorderStyle = BorderStyle.FixedSingle;
        _log.WordWrap = false;
        layout.Controls.Add(top, 0, 0);
        layout.Controls.Add(_log, 0, 1);
        card.Controls.Add(layout);
        return card;
    }

    private Control CreateFooter()
    {
        var panel = new Panel { Dock = DockStyle.Fill, BackColor = Navy };
        _status.Text = "Sẵn sàng";
        _status.ForeColor = Color.White;
        _status.Dock = DockStyle.Fill;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.Padding = new Padding(15, 0, 0, 0);
        _progress.Dock = DockStyle.Right;
        _progress.Width = 160;
        _progress.Style = ProgressBarStyle.Marquee;
        _progress.MarqueeAnimationSpeed = 0;
        _progress.Visible = false;
        panel.Controls.Add(_status);
        panel.Controls.Add(_progress);
        return panel;
    }

    private static Panel Card() => new()
    {
        Dock = DockStyle.Fill, BackColor = Color.White,
        BorderStyle = BorderStyle.FixedSingle, Padding = new Padding(15)
    };

    private static Label MutedLabel(string text) => new()
    {
        Text = text, ForeColor = Muted, Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.BottomLeft
    };

    private void SetupCombo(ComboBox combo)
    {
        combo.Dock = DockStyle.Fill;
        combo.DropDownStyle = ComboBoxStyle.DropDownList;
        combo.Margin = new Padding(0, 2, 0, 5);
    }

    private Button SmallButton(string text, Action click)
    {
        var button = new Button
        {
            Text = text, Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat,
            BackColor = SoftBlue, ForeColor = Blue,
            Font = new Font("Segoe UI Semibold", 9f),
            Cursor = Cursors.Hand
        };
        button.FlatAppearance.BorderColor = Color.FromArgb(194, 216, 243);
        button.Click += (_, _) => click();
        _buttons.Add(button);
        return button;
    }

    private Button ActionButton(string title, string hint, bool primary, Func<Task> action)
    {
        var button = new Button
        {
            Text = title, Dock = DockStyle.Fill,
            Margin = new Padding(3, 5, 3, 5),
            FlatStyle = FlatStyle.Flat,
            BackColor = primary ? Blue : SoftBlue,
            ForeColor = primary ? Color.White : TextColor,
            Font = new Font("Segoe UI Semibold", 10f),
            Cursor = Cursors.Hand
        };
        button.FlatAppearance.BorderColor = primary ? Blue : Color.FromArgb(194, 216, 243);
        _tips.SetToolTip(button, hint);
        button.Click += async (_, _) => await action();
        _buttons.Add(button);
        return button;
    }

    private void PickFolder(TextBox field)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Chọn thư mục",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(field.Text) ? field.Text :
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };
        if (dialog.ShowDialog(this) == DialogResult.OK) field.Text = dialog.SelectedPath;
    }

    private void PickCsv()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Chọn CSV bản dịch BF4",
            Filter = "CSV UTF-8 (*.csv)|*.csv|Tất cả file (*.*)|*.*",
            InitialDirectory = Directory.Exists(Path.GetDirectoryName(_csv.Text))
                ? Path.GetDirectoryName(_csv.Text) :
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };
        if (dialog.ShowDialog(this) == DialogResult.OK) _csv.Text = dialog.FileName;
    }

    private async Task RunOperationAsync(string name, Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        foreach (var button in _buttons) button.Enabled = false;
        _progress.Visible = true;
        _progress.MarqueeAnimationSpeed = 25;
        _status.Text = "Đang " + name.ToLowerInvariant() + "…";
        AppendLog("\n[" + DateTime.Now.ToString("HH:mm:ss") + "] " + name);
        try
        {
            SaveSettings();
            await action();
            SaveSettings();
            _status.Text = name + " xong";
            AppendLog("Hoàn tất.");
        }
        catch (Exception ex)
        {
            _status.Text = name + " thất bại";
            AppendLog("LỖI: " + ex.Message);
            MessageBox.Show(this, ex.Message, name + " thất bại",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _busy = false;
            foreach (var button in _buttons) button.Enabled = true;
            _progress.MarqueeAnimationSpeed = 0;
            _progress.Visible = false;
            RefreshBackups();
        }
    }

    private async Task ExportTextAsync()
    {
        var game = GamePath();
        var destination = Path.Combine(WorkspacePath(), "exports",
            "BF4_Text_" + Stamp() + ".csv");
        await RunToolAsync("export-sheet", game, destination);
        _csv.Text = destination;
        AppendLog("CSV có thể sửa rồi chọn Cài text: " + destination);
    }

    private async Task ExportFontsAsync()
    {
        var game = GamePath();
        var destination = Path.Combine(WorkspacePath(), "exports",
            "BF4_Fonts_" + Stamp());
        await RunToolAsync("export-gfx", game, destination);
        _fonts.Text = destination;
        AppendLog("Sửa các GFX trong data\\ui rồi chọn Cài font: " + destination);
    }

    private async Task InstallTextAsync()
    {
        EnsureGameStopped();
        var game = GamePath();
        var csv = Path.GetFullPath(_csv.Text.Trim());
        if (!File.Exists(csv)) throw new FileNotFoundException("Không tìm thấy CSV bản dịch", csv);
        var id = Stamp();
        var root = WorkspacePath();
        var stage = Path.Combine(root, "stages", id + "-text");
        var backup = Path.Combine(root, "backups", id + "-text");
        await RunToolAsync("stage-text", game, csv, stage);
        using var report = JsonDocument.Parse(File.ReadAllText(Path.Combine(stage, "text_stage.json")));
        if (report.RootElement.GetProperty("changed").GetInt32() == 0)
        { AppendLog("Text trong CSV giống game hiện tại; không cần cài lại."); return; }
        await RunToolAsync("install-text", game, stage, backup);
        AppendLog("Sao lưu text: " + backup);
    }

    private async Task InstallFontsAsync()
    {
        EnsureGameStopped();
        var game = GamePath();
        var fonts = Path.GetFullPath(_fonts.Text.Trim());
        if (!Directory.Exists(fonts)) throw new DirectoryNotFoundException("Không tìm thấy thư mục font: " + fonts);
        var id = Stamp();
        var root = WorkspacePath();
        var stage = Path.Combine(root, "stages", id + "-fonts");
        var backup = Path.Combine(root, "backups", id + "-fonts");
        await RunToolAsync("stage-fonts", game, fonts, stage);
        using var report = JsonDocument.Parse(File.ReadAllText(Path.Combine(stage, "font_stage.json")));
        if (report.RootElement.GetArrayLength() == 0)
        { AppendLog("Font GFX giống game hiện tại; không cần cài lại."); return; }
        await RunToolAsync("install-fonts", game, stage, backup);
        AppendLog("Sao lưu font: " + backup);
    }

    private async Task RestoreTextAsync()
    {
        EnsureGameStopped();
        var backup = (_textBackups.SelectedItem as BackupItem)?.Path ??
            throw new InvalidOperationException("Hãy chọn một bản sao lưu text.");
        await RunToolAsync("restore-text", GamePath(), backup);
    }

    private async Task RestoreFontsAsync()
    {
        EnsureGameStopped();
        var backup = (_fontBackups.SelectedItem as BackupItem)?.Path ??
            throw new InvalidOperationException("Hãy chọn một bản sao lưu font.");
        await RunToolAsync("restore-fonts", GamePath(), backup);
    }

    private async Task RunToolAsync(params string[] arguments)
    {
        _lastError = null;
        var oldOut = Console.Out;
        var oldErr = Console.Error;
        using var writer = new LogWriter(line =>
        {
            if (line.StartsWith("Error: ", StringComparison.Ordinal))
                _lastError = line[7..];
            AppendLog(line);
        });
        var result = await Task.Run(() =>
        {
            Console.SetOut(writer);
            Console.SetError(writer);
            try { return Bf4Toolkit.Program.Main(arguments); }
            finally
            {
                writer.Flush();
                Console.SetOut(oldOut);
                Console.SetError(oldErr);
            }
        });
        if (result != 0)
            throw new InvalidOperationException(_lastError ??
                $"Tool trả mã {result}. Xem nhật ký để biết chi tiết.");
    }

    internal Task RunDiagnosticAsync() => RunToolAsync("inspect", GamePath());

    private void RefreshBackups()
    {
        if (_busy) return;
        try
        {
            var root = Path.Combine(_workspace.Text.Trim(), "backups");
            FillBackups(_textBackups, root, "text_backup.json");
            FillBackups(_fontBackups, root, "font_backup.json");
        }
        catch (IOException) { _textBackups.Items.Clear(); _fontBackups.Items.Clear(); }
        catch (UnauthorizedAccessException) { _textBackups.Items.Clear(); _fontBackups.Items.Clear(); }
        catch (ArgumentException) { _textBackups.Items.Clear(); _fontBackups.Items.Clear(); }
    }

    private static void FillBackups(ComboBox combo, string root, string marker)
    {
        var selected = (combo.SelectedItem as BackupItem)?.Path;
        combo.Items.Clear();
        if (Directory.Exists(root))
            foreach (var directory in Directory.GetDirectories(root)
                         .OrderByDescending(path => Path.GetFileName(path)))
                if (File.Exists(Path.Combine(directory, marker)))
                    combo.Items.Add(new BackupItem(Path.GetFileName(directory), directory));
        if (combo.Items.Count != 0)
        {
            var index = combo.Items.Cast<BackupItem>().ToList()
                .FindIndex(item => item.Path.Equals(selected, StringComparison.OrdinalIgnoreCase));
            combo.SelectedIndex = index >= 0 ? index : 0;
        }
    }

    private void OpenWorkspace()
    {
        try
        {
            var directory = WorkspacePath();
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Không mở được thư mục",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void SaveSettings()
    {
        _settings.GameDirectory = _game.Text.Trim();
        _settings.CsvPath = _csv.Text.Trim();
        _settings.FontDirectory = _fonts.Text.Trim();
        _settings.WorkspaceDirectory = _workspace.Text.Trim();
        _settings.Save();
    }

    private string GamePath()
    {
        var path = Path.GetFullPath(_game.Text.Trim());
        if (!File.Exists(Path.Combine(path, "bf4.exe")))
            throw new FileNotFoundException("Thư mục Battlefield 4 không hợp lệ", path);
        return path;
    }

    private string WorkspacePath()
    {
        if (string.IsNullOrWhiteSpace(_workspace.Text))
            throw new InvalidOperationException("Hãy chọn nơi lưu bản xuất và sao lưu.");
        return Path.GetFullPath(_workspace.Text.Trim());
    }

    private static void EnsureGameStopped()
    {
        if (Process.GetProcessesByName("bf4").Length != 0 ||
            Process.GetProcessesByName("bf4_x86").Length != 0)
            throw new InvalidOperationException("Hãy thoát Battlefield 4 trước khi cài hoặc khôi phục.");
    }

    private static string Stamp() => DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");

    private void AppendLog(string value)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            if (IsHandleCreated) BeginInvoke(() => AppendLog(value));
            return;
        }
        _log.AppendText(value + Environment.NewLine);
        _log.SelectionStart = _log.TextLength;
        _log.ScrollToCaret();
    }

    private sealed record BackupItem(string Name, string Path)
    {
        public override string ToString() => Name;
    }

    private sealed class LogWriter(Action<string> callback) : TextWriter
    {
        private readonly StringBuilder _buffer = new();
        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            if (value == '\n')
            {
                var line = _buffer.ToString().TrimEnd('\r');
                _buffer.Clear();
                if (line.Length != 0) callback(line);
            }
            else _buffer.Append(value);
        }

        public override void Write(string? value)
        {
            if (value is null) return;
            foreach (var ch in value) Write(ch);
        }

        public override void Flush()
        {
            if (_buffer.Length != 0)
            {
                callback(_buffer.ToString());
                _buffer.Clear();
            }
        }
    }
}
