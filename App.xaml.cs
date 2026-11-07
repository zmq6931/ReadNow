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

        // 调试参数：验证在线语音接口是否打通（结果写到 %TEMP%\readnow_tts_test.txt）
        if (e.Args.Contains("--test-tts"))
        {
            _ = Task.Run(async () =>
            {
                var resultFile = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(), "readnow_tts_test.txt");
                try
                {
                    // 1) 先校验令牌算法是否与 edge-tts 参考实现一致（固定时间戳测试向量）
                    var vectors = new Dictionary<long, string>
                    {
                        [1762444800] = "C5004301D2042BC2-2C3A7719AED00A84F2EBCAD703B75D08E81F4F77ED4658901667074AA7895995",
                        [1762448100] = "F8A45E05ABED99B7-D30686365B56BB740E72C320D304001BBCD91C3EFC0C7BA55A244742B5F27548",
                        [1700000000] = "2CD23A2CA0B2595A-F3C846C370E05F9CC83ACC5F995C5F5D87A4D0F14CE8936A0F902E64099E38A2",
                        [1800000000] = "99E54519B490BE5C-E162DB0977FBDCB9B86DD8148BF1BCDC3C0303176CE39852A42B415935C20654",
                    };
                    foreach (var (ts, expected) in vectors)
                    {
                        var actual = Services.EdgeTtsDrm.GenerateSecMsGec(ts);
                        if (actual != expected)
                        {
                            System.IO.File.WriteAllText(resultFile,
                                $"GEC MISMATCH ts={ts}\nexpected {expected}\nactual   {actual}");
                            Dispatcher.Invoke(Shutdown);
                            return;
                        }
                    }

                    // 2) 令牌正确 → 实测一次在线合成
                    var mp3 = await Services.EdgeTtsClient.SynthesizeAsync(
                        "测试一下晓曼的声音。This is a test.",
                        "zh-HK-HiuMaanNeural", "+0%", "-10Hz", default);
                    System.IO.File.WriteAllText(resultFile, $"OK {mp3.Length} bytes");
                }
                catch (Exception ex)
                {
                    System.IO.File.WriteAllText(resultFile, "FAIL " + ex);
                }
                Dispatcher.Invoke(Shutdown);
            });
        }

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

        // 显示界面：Esc 关掉弹窗、或划词/截图都关闭时，从这里找回主界面
        var showItem = new Forms.ToolStripMenuItem("显示界面", null, (_, _) => host.ShowMainInterface());

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

        menu.Items.Add(showItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
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

        _trayIcon.DoubleClick += (_, _) => host.ShowMainInterface();
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
