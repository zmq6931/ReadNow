using System.Reflection;
using System.Windows;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace ReadNow;

public partial class App : System.Windows.Application
{
    private Forms.NotifyIcon? _trayIcon;
    private Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 单实例保护：已在运行则提示并退出，避免多实例钩子叠加导致设置"失灵"
        _singleInstanceMutex = new Mutex(true, "Global\\ReadNow.SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            System.Windows.MessageBox.Show(
                "ReadNow 已经在运行了（看看右下角托盘的蓝色喇叭图标）。",
                "ReadNow", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // 全局兜底：异常写日志 + 气泡提示，不静默失败
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            LogError(args.Exception);
            Balloon("ReadNow 出错了", args.Exception.Message);
        };

        // 隐藏宿主窗口（承载热键 / 剪贴板消息循环）
        var host = new MainWindow();
        host.Show();
        host.Hide();
        MainWindow = host;

        BuildTrayIcon(host);

        // 调试参数：启动后立即弹一个测试窗口
        if (e.Args.Contains("--test-popup"))
            host.DebugShowPopup();

        // 启动提示，让用户知道程序在后台运行
        _trayIcon?.ShowBalloonTip(4000, "ReadNow 已启动",
            "划选文字弹出朗读窗；Alt+Q 区域截图朗读；截图进剪贴板也会自动识别。",
            Forms.ToolTipIcon.Info);
    }

    /// <summary>托盘气泡通知。</summary>
    public void Balloon(string title, string message)
    {
        _trayIcon?.ShowBalloonTip(3000, title, message, Forms.ToolTipIcon.Info);
    }

    /// <summary>把异常完整写入 %APPDATA%\ReadNow\error.log。</summary>
    public static void LogError(Exception ex)
    {
        try
        {
            var dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ReadNow");
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "error.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\r\n\r\n");
        }
        catch { }
    }

    private void BuildTrayIcon(MainWindow host)
    {
        var menu = new Forms.ContextMenuStrip();

        var selectionItem = new Forms.ToolStripMenuItem("划词朗读") { Checked = true, CheckOnClick = true };
        selectionItem.Click += (_, _) =>
        {
            host.SelectionWatchEnabled = selectionItem.Checked;
            Balloon("ReadNow", selectionItem.Checked ? "划词朗读已开启" : "划词朗读已关闭");
        };

        var screenshotItem = new Forms.ToolStripMenuItem("截图识别（自动检测 + Alt+Q 截图）") { Checked = true, CheckOnClick = true };
        screenshotItem.Click += (_, _) =>
        {
            host.ScreenshotWatchEnabled = screenshotItem.Checked;
            Balloon("ReadNow", screenshotItem.Checked ? "截图识别已开启" : "截图识别已关闭");
        };

        var hotkeyItem = new Forms.ToolStripMenuItem(
            "快捷键：Alt+Q 区域截图朗读 ｜ Ctrl+Alt+R 朗读剪贴板截图")
        { Enabled = false };

        var exitItem = new Forms.ToolStripMenuItem("退出", null, (_, _) => Shutdown());

        menu.Items.Add(selectionItem);
        menu.Items.Add(screenshotItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(hotkeyItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(exitItem);

        // 每次打开菜单时同步勾选状态（与弹窗内复选框保持一致）
        menu.Opening += (_, _) =>
        {
            selectionItem.Checked = host.SelectionWatchEnabled;
            screenshotItem.Checked = host.ScreenshotWatchEnabled;
        };

        _trayIcon = new Forms.NotifyIcon
        {
            Text = "ReadNow - 划词/截图朗读",
            Icon = LoadTrayIcon(),
            Visible = true,
            ContextMenuStrip = menu
        };

        _trayIcon.DoubleClick += (_, _) =>
            Balloon("ReadNow",
                "正在运行中：划词弹出朗读窗；Ctrl+Alt+Q 区域截图朗读。");
    }

    /// <summary>从嵌入资源加载专属喇叭图标。</summary>
    private static Drawing.Icon LoadTrayIcon()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var resName = asm.GetManifestResourceNames()
                .First(n => n.EndsWith("readnow.ico", StringComparison.OrdinalIgnoreCase));
            var stream = asm.GetManifestResourceStream(resName)!;
            return new Drawing.Icon(stream); // stream 不释放，Icon 持有引用
        }
        catch
        {
            return Drawing.SystemIcons.Application;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        MainWindow?.Close();
        base.OnExit(e);
    }
}
