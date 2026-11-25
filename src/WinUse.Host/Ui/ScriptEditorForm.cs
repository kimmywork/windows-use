using System.Drawing;
using System.Text.Json;
using System.Windows.Forms;
using WinUse.Core.Capabilities;
using WinUse.Core.Scripts;

namespace WinUse.Host.Ui;

/// <summary>
/// 脚本编辑器（PRD N6：不做拖拽流程画布，宏/脚本以 JSON 文本定义）。
/// 左侧脚本列表，右侧源码编辑 + 运行输出；脚本逐步经 CapabilityDispatcher 执行。
/// </summary>
internal sealed class ScriptEditorForm : Form
{
    private static readonly JsonSerializerOptions CompactJson = new()
    {
        WriteIndented = false,
    };

    private readonly ScriptStore _store;
    private readonly ScriptRunner _runner;
    private readonly CapabilityRegistry _registry;

    private readonly ListBox _scriptList = new();
    private readonly TextBox _codeBox = new();
    private readonly TextBox _outputBox = new();
    private readonly ToolStrip _toolStrip = new();
    private readonly ToolStripStatusLabel _statusLabel = new();
    private readonly SplitContainer _editorSplit = new()
    {
        Dock = DockStyle.Fill,
        Orientation = Orientation.Horizontal,
        SplitterWidth = 6,
    };

    private string? _current;
    private bool _dirty;
    private bool _loading;
    private bool _running;
    private bool _allowRealExit;

    /// <summary>从托盘唤起：显示窗口、还原最小化并置前。</summary>
    public void ShowEditor()
    {
        TrayTrace.Log($"ShowEditor enter: state={WindowState}, visible={Visible}, handle={Handle}");
        if (WindowState == FormWindowState.Minimized)
        {
            WindowState = FormWindowState.Normal;
        }
        Show();
        Activate();
        BringToFront();
        TrayTrace.Log($"ShowEditor exit: visible={Visible}");
    }

    /// <summary>托盘菜单请求真正退出（关闭窗口不再驻留托盘）。</summary>
    public void RequestExit()
    {
        _allowRealExit = true;
        Close();
    }

    public ScriptEditorForm(ScriptStore store, ScriptRunner runner, CapabilityRegistry registry)
    {
        _store = store;
        _runner = runner;
        _registry = registry;

        Text = "WinUse 脚本编辑器";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1120, 760);
        MinimumSize = new Size(820, 560);
        Font = SystemFonts.MessageBoxFont;

        BuildToolbar();
        BuildBody();
        BuildStatusBar();

        Load += (_, _) =>
        {
            RefreshList(select: null);
            var first = _scriptList.Items.Count > 0 ? _scriptList.Items[0] as string : null;
            if (first is not null)
            {
                LoadScript(first);
            }
            else
            {
                _codeBox.Enabled = false;
                AppendOutput("暂无脚本，点击「新建」创建（或工具栏「帮助」查看脚本格式）。");
            }
        };
    }

    // ---- 布局 ----

    private void BuildToolbar()
    {
        AddToolButton("新建", "创建新脚本", OnNew);
        AddToolButton("保存", "保存当前脚本 (Ctrl+S)", OnSave);
        AddToolButton("校验", "校验 JSON 与步骤引用的能力", OnValidate);
        AddToolButton("运行", "保存并运行脚本", OnRun);
        _toolStrip.Items.Add(new ToolStripSeparator());
        AddToolButton("重命名", "重命名当前脚本", OnRename);
        AddToolButton("删除", "删除当前脚本", OnDelete);
        AddToolButton("刷新", "重新读取脚本目录", (_, _) => RefreshList(_current));
        _toolStrip.Items.Add(new ToolStripSeparator());
        AddToolButton("打开目录", "在资源管理器中打开脚本目录", (_, _) => OpenScriptsDirectory());
        AddToolButton("帮助", "脚本格式说明", (_, _) => ShowHelp());

        _toolStrip.Dock = DockStyle.Top;
        _toolStrip.GripStyle = ToolStripGripStyle.Hidden;
        Controls.Add(_toolStrip);
    }

    private void AddToolButton(string text, string tooltip, EventHandler onClick)
    {
        var button = new ToolStripButton(text)
        {
            ToolTipText = tooltip,
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            Padding = new Padding(4, 2, 4, 2),
        };
        button.Click += onClick;
        _toolStrip.Items.Add(button);
    }

    private void BuildBody()
    {
        var mainSplit = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterWidth = 6,
        };
        Controls.Add(mainSplit);
        mainSplit.BringToFront();
        mainSplit.SplitterDistance = 220;

        // 左：脚本列表 + 按钮
        _scriptList.Dock = DockStyle.Fill;
        _scriptList.IntegralHeight = false;
        _scriptList.Font = new Font("Consolas", 10F);
        _scriptList.SelectedIndexChanged += (_, _) => OnScriptSelected();
        mainSplit.Panel1.Controls.Add(_scriptList);

        var leftButtons = new Panel { Dock = DockStyle.Bottom, Height = 40 };
        var btnNew = new Button { Text = "新建", Left = 8, Top = 8, Width = 60 };
        var btnRename = new Button { Text = "重命名", Left = 74, Top = 8, Width = 64 };
        var btnDelete = new Button { Text = "删除", Left = 144, Top = 8, Width = 60 };
        btnNew.Click += OnNew;
        btnRename.Click += OnRename;
        btnDelete.Click += OnDelete;
        leftButtons.Controls.AddRange(new Control[] { btnNew, btnRename, btnDelete });
        mainSplit.Panel1.Controls.Add(leftButtons);

        // 右：编辑器 + 输出
        _codeBox.Dock = DockStyle.Fill;
        _codeBox.Multiline = true;
        _codeBox.AcceptsTab = true;
        _codeBox.WordWrap = false;
        _codeBox.ScrollBars = ScrollBars.Both;
        _codeBox.Font = new Font("Consolas", 10.5F);
        _codeBox.TextChanged += (_, _) =>
        {
            if (_loading) return;
            _dirty = true;
            UpdateStatus();
        };
        _editorSplit.Panel1.Controls.Add(_codeBox);

        _outputBox.Dock = DockStyle.Fill;
        _outputBox.Multiline = true;
        _outputBox.ReadOnly = true;
        _outputBox.ScrollBars = ScrollBars.Vertical;
        _outputBox.BackColor = Color.FromArgb(250, 250, 250);
        _outputBox.Font = new Font("Consolas", 9.5F);
        _editorSplit.Panel2.Controls.Add(_outputBox);

        mainSplit.Panel2.Controls.Add(_editorSplit);
        mainSplit.Panel2.Controls.Add(new Label
        {
            Text = "  脚本源码（JSON）",
            Dock = DockStyle.Top,
            Height = 22,
            TextAlign = ContentAlignment.MiddleLeft,
            BackColor = Color.FromArgb(240, 240, 240),
        });

        _editorSplit.SplitterDistance = 470;
    }

    private void BuildStatusBar()
    {
        var statusStrip = new StatusStrip();
        _statusLabel.Spring = true;
        _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        _statusLabel.Text = "就绪";
        statusStrip.Items.Add(_statusLabel);
        Controls.Add(statusStrip);
    }

    // ---- 脚本列表 ----

    private void RefreshList(string? select)
    {
        _loading = true;
        var docs = _store.List(out var loadErrors);

        _scriptList.BeginUpdate();
        _scriptList.Items.Clear();
        foreach (var d in docs)
        {
            _scriptList.Items.Add(d.Name);
        }
        _scriptList.EndUpdate();

        foreach (var (file, error) in loadErrors)
        {
            AppendOutput($"[警告] 脚本文件 {file} 无法解析: {error}");
        }

        var target = select is not null && _scriptList.Items.Contains(select) ? select : null;
        if (target is not null)
        {
            _scriptList.SelectedItem = target;
        }
        _loading = false;
    }

    private void OnScriptSelected()
    {
        if (_loading || _running) return;
        var name = _scriptList.SelectedItem as string;
        if (name is null || name == _current) return;

        if (_dirty && !PromptDiscardCurrent())
        {
            // 用户取消：还原选择
            _loading = true;
            _scriptList.SelectedItem = _current;
            _loading = false;
            return;
        }

        LoadScript(name);
    }

    private void LoadScript(string name)
    {
        _loading = true;
        _current = name;
        _codeBox.Enabled = true;
        _codeBox.Text = File.ReadAllText(_store.PathFor(name));
        _dirty = false;
        _loading = false;
        UpdateStatus();
    }

    // ---- 动作：新建/重命名/删除 ----

    private void OnNew(object? sender, EventArgs e)
    {
        if (_running) return;
        if (_dirty && !PromptDiscardCurrent()) return;

        var name = PromptDialog.Show(this, "新建脚本", "脚本名称（将作为文件名，建议英文/数字）:");
        if (name is null) return;

        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            MessageBox.Show(this, "名称含非法文件名字符", "新建脚本", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (_store.Exists(name))
        {
            MessageBox.Show(this, $"脚本 {name} 已存在", "新建脚本", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _store.Save(ScriptStore.Template(name));
        AppendOutput($"已创建脚本 {name}");
        RefreshList(name);
        LoadScript(name);
    }

    private void OnRename(object? sender, EventArgs e)
    {
        if (_running || _current is null) return;

        var newName = PromptDialog.Show(this, "重命名脚本", "新名称:", _current);
        if (newName is null || newName == _current) return;

        if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            MessageBox.Show(this, "名称含非法文件名字符", "重命名", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (_store.Exists(newName))
        {
            MessageBox.Show(this, $"脚本 {newName} 已存在", "重命名", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _store.Rename(_current!, newName);
        AppendOutput($"已重命名 {_current} → {newName}");
        RefreshList(newName);
        LoadScript(newName);
    }

    private void OnDelete(object? sender, EventArgs e)
    {
        if (_running || _current is null) return;

        var confirm = MessageBox.Show(this,
            $"确定删除脚本 {_current}？此操作不可恢复。",
            "删除脚本", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (confirm != DialogResult.Yes) return;

        _store.Delete(_current!);
        AppendOutput($"已删除脚本 {_current}");
        _current = null;
        _loading = true;
        _codeBox.Text = "";
        _codeBox.Enabled = false;
        _dirty = false;
        _loading = false;
        RefreshList(null);
        UpdateStatus();
    }

    // ---- 保存 / 校验 ----

    private void OnSave(object? sender, EventArgs e)
    {
        if (_running) return;
        SaveCurrent();
    }

    private bool SaveCurrent()
    {
        if (_current is null)
        {
            MessageBox.Show(this, "没有可保存的脚本，请先新建", "保存", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }

        if (!TryBuildDocument(out var doc, out var error))
        {
            MessageBox.Show(this, $"保存失败：{error}", "保存", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            AppendOutput($"[保存失败] {error}");
            return false;
        }

        _store.Save(doc);

        // 以磁盘上的规范内容回显，统一缩进并清除脏标记。
        _loading = true;
        _codeBox.Text = File.ReadAllText(_store.PathFor(_current));
        _loading = false;
        _dirty = false;
        UpdateStatus($"已保存 {DateTime.Now:HH:mm:ss}");
        return true;
    }

    private void OnValidate(object? sender, EventArgs e)
    {
        if (_running || _current is null) return;

        if (!TryBuildDocument(out var doc, out var error))
        {
            MessageBox.Show(this, $"校验失败：{error}", "校验", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            AppendOutput($"[校验失败] {error}");
            return;
        }

        try
        {
            ScriptStore.ValidateAgainstCatalog(doc, _registry);
        }
        catch (ScriptFormatException ex)
        {
            MessageBox.Show(this, $"校验失败：{ex.Message}", "校验", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            AppendOutput($"[校验失败] {ex.Message}");
            return;
        }

        AppendOutput($"[校验通过] {doc.Name}：{doc.Steps.Count} 步，on_error={doc.OnError}");
    }

    /// <summary>把编辑器文本解析为脚本文档（name 以当前文件为准）。</summary>
    private bool TryBuildDocument(out ScriptDocument doc, out string? error)
    {
        doc = null!;
        if (_current is null)
        {
            error = "没有打开的脚本";
            return false;
        }

        if (ScriptStore.TryParseText(_codeBox.Text, _current, out var parsed, out error) && parsed is not null)
        {
            doc = parsed;
            return true;
        }
        return false;
    }

    // ---- 运行 ----

    private async void OnRun(object? sender, EventArgs e)
    {
        if (_running || _current is null) return;

        if (!SaveCurrent()) return;

        var doc = _store.Load(_current!);
        SetBusy(true);
        AppendOutput($"[{DateTime.Now:HH:mm:ss}] ▶ 运行脚本 {doc.Name}（{doc.Steps.Count} 步，on_error={doc.OnError}）");

        try
        {
            var result = await Task.Run(() => _runner.RunAsync(doc)).ConfigureAwait(true);
            RenderResult(result);
        }
        catch (Exception ex)
        {
            AppendOutput($"[运行异常] {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void RenderResult(ScriptRunResult result)
    {
        foreach (var t in result.Trace)
        {
            if (t.Ok)
            {
                var resultJson = t.Result is null ? "" : Truncate(JsonSerializer.Serialize(t.Result, CompactJson), 400);
                AppendOutput($"  [{t.Index}/{result.StepsTotal}] {t.Capability}  ✓ {t.DurationMs:0}ms  {resultJson}");
            }
            else
            {
                AppendOutput($"  [{t.Index}/{result.StepsTotal}] {t.Capability}  ✗ {t.DurationMs:0}ms  {t.ErrorCode}: {t.ErrorMessage}");
            }
        }

        if (result.FailedAt is { } failure)
        {
            AppendOutput($"✗ 中止于第 {failure.Index} 步（{failure.Capability}）：{failure.ErrorCode} — {failure.Message}");
        }
        else if (result.Ok)
        {
            AppendOutput($"✓ 脚本完成：{result.StepsCompleted}/{result.StepsTotal} 步成功");
        }
        else
        {
            AppendOutput($"✗ 运行结束（含失败步骤）：{result.StepsCompleted}/{result.StepsTotal} 步成功");
        }

        if (result.Note is { Length: > 0 } note)
        {
            AppendOutput($"注: {note}");
        }
    }

    private void SetBusy(bool busy)
    {
        _running = busy;
        _toolStrip.Enabled = !busy;
        _scriptList.Enabled = !busy;
        _codeBox.Enabled = !busy;
        Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
        if (busy)
        {
            UpdateStatus("运行中…");
        }
        else
        {
            UpdateStatus();
        }
    }

    // ---- 杂项 ----

    public void OpenScriptsDirectory()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_store.DirectoryPath)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"无法打开目录: {ex.Message}", "打开目录", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ShowHelp()
    {
        MessageBox.Show(this,
            """
            脚本以 JSON 文本定义（不提供拖拽流程图）：

            {
              "name": "my-script",
              "title": "脚本标题",
              "description": "脚本说明",
              "on_error": "abort",
              "steps": [
                { "capability": "display.list", "params": {} },
                { "capability": "wait.for", "params": {"kind":"window","title":"记事本"} }
              ]
            }

            字段说明：
            · on_error：abort（首错中止，默认）/ continue（继续）/ rollback（需 v1.5 事务支持，当前按 abort）
            · 每步的 capability 必须是已注册能力，可用 CLI「winuse tools openai」查看
            · 每步都走风险分级/确认门/审计；无头模式下 R2 及以上动作会被拒绝

            脚本目录：
            """ + _store.DirectoryPath,
            "脚本格式帮助", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private bool PromptDiscardCurrent()
    {
        if (!_dirty) return true;

        var answer = MessageBox.Show(this,
            $"当前脚本 {_current} 有未保存改动，是否先保存？\n选「是」保存并继续，「否」放弃改动，「取消」返回。",
            "未保存的改动", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);

        if (answer == DialogResult.Cancel) return false;
        if (answer == DialogResult.No) return true;
        return SaveCurrent();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // 点 X 或 Alt+F4：驻留托盘而非退出（未保存改动随隐藏窗口保留）。
        if (!_allowRealExit)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        if (_dirty)
        {
            var answer = MessageBox.Show(this,
                $"脚本 {_current} 有未保存改动，是否保存？",
                "退出", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (answer == DialogResult.Cancel)
            {
                e.Cancel = true;
                _allowRealExit = false;
                return;
            }
            if (answer == DialogResult.Yes && !SaveCurrent())
            {
                e.Cancel = true;
                _allowRealExit = false;
            }
        }
        base.OnFormClosing(e);
    }

    private void AppendOutput(string line)
    {
        _outputBox.AppendText(line + Environment.NewLine);
    }

    private void UpdateStatus(string? text = null)
    {
        _statusLabel.Text = text ?? (_current is null
            ? "就绪"
            : $"{_current} — {(_dirty ? "● 未保存" : "已保存")}");
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
