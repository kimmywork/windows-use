using System.Drawing;
using System.Windows.Forms;
using WinUse.Core.Scripts;

namespace WinUse.Host.Ui;

/// <summary>
/// 托盘驻留宿主：编辑器启动后不弹窗，驻留系统托盘；双击/右键打开窗口。
/// 通过命名 Mutex 保证单实例——再次执行 editor 时唤起已有窗口而非启动第二份。
/// </summary>
internal sealed class EditorTrayHost : IDisposable
{
    private const string MutexName = @"Local\WinUse.Editor";
    private const string ShowEventName = @"Local\WinUse.Editor.Show";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _showEvent;
    private readonly ScriptEditorForm _form;
    private readonly NotifyIcon _tray;
    private readonly Thread _waitThread;
    private bool _disposed;

    private EditorTrayHost(Mutex mutex, EventWaitHandle showEvent, ScriptEditorForm form)
    {
        _mutex = mutex;
        _showEvent = showEvent;
        _form = form;

        _tray = new NotifyIcon
        {
            Icon = BuildIcon(),
            Text = "WinUse 脚本编辑器",
            Visible = true,
            ContextMenuStrip = BuildMenu(),
        };
        _tray.DoubleClick += (_, _) => _form.ShowEditor();

        _waitThread = new Thread(WaitForShow) { IsBackground = true, Name = "WinUse editor show waiter" };
    }

    /// <summary>
    /// 单实例入口：首个实例返回托盘宿主；已有实例运行时唤起其窗口并返回 null。
    /// </summary>
    public static EditorTrayHost? CreateOrSignal(ScriptEditorForm form)
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var mutexFirst);
        var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName, out _);

        if (!mutexFirst)
        {
            // 已有实例：发唤起信号后退出。
            TrayTrace.Log("second instance: signaling existing instance");
            showEvent.Set();
            TrayTrace.Log("second instance: event set, exiting");
            showEvent.Dispose();
            mutex.Dispose();
            return null;
        }

        TrayTrace.Log("first instance: creating tray host");
        return new EditorTrayHost(mutex, showEvent, form);
    }

    public void Start()
    {
        _waitThread.Start();
        _tray.ShowBalloonTip(
            3000,
            "WinUse 脚本编辑器",
            "已驻留系统托盘，双击图标打开编辑器；右键托盘图标可退出。",
            ToolTipIcon.Info);
    }

    private void WaitForShow()
    {
        TrayTrace.Log("wait thread started");
        var heartbeats = 0;
        while (!_disposed)
        {
            try
            {
                var signaled = _showEvent.WaitOne(5000);
                if (heartbeats++ % 6 == 0)
                {
                    TrayTrace.Log($"heartbeat, disposed={_disposed}");
                }
                if (!signaled) continue;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            TrayTrace.Log("event signaled");
            if (_disposed) break;
            try
            {
                _form.BeginInvoke(() =>
                {
                    TrayTrace.Log("invoke: calling ShowEditor");
                    _form.ShowEditor();
                    TrayTrace.Log($"invoke: ShowEditor returned, visible={_form.Visible}");
                });
                TrayTrace.Log("BeginInvoke queued");
            }
            catch (InvalidOperationException)
            {
                break;
            }
        }
        TrayTrace.Log("wait thread exiting");
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();

        var openItem = new ToolStripMenuItem("打开编辑器");
        openItem.Font = new Font(openItem.Font, FontStyle.Bold);
        openItem.Click += (_, _) => _form.ShowEditor();

        var dirItem = new ToolStripMenuItem("打开脚本目录");
        dirItem.Click += (_, _) => _form.OpenScriptsDirectory();

        var exitItem = new ToolStripMenuItem("退出 WinUse");
        exitItem.Click += (_, _) => ExitApplication();

        menu.Items.Add(openItem);
        menu.Items.Add(dirItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);
        return menu;
    }

    private void ExitApplication()
    {
        _form.RequestExit();
        Application.Exit();
    }

    private static Icon BuildIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var bg = new SolidBrush(Color.FromArgb(43, 87, 154));
            g.FillRectangle(bg, 0, 0, 32, 32);
            TextRenderer.DrawText(
                g, "W", new Font("Segoe UI", 17F, FontStyle.Bold),
                new Rectangle(0, 0, 32, 32), Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
        return Icon.FromHandle(bitmap.GetHicon());
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _tray.Visible = false;
        _tray.Dispose();
        _showEvent.Dispose();
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}

internal static class TrayTrace
{
    private static readonly string LogPath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinUse", "editor-tray.log");

    public static void Log(string message)
    {
        try
        {
            System.IO.File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {message}\n");
        }
        catch
        {
            // 跟踪日志失败保持静默。
        }
    }
}
