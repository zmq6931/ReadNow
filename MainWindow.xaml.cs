using System.Windows;
using System.Windows.Interop;
using ReadNow.Native;
using ReadNow.Services;
using ReadNow.Views;

namespace ReadNow;

/// <summary>
/// 隐藏的宿主窗口：接收全局热键（Ctrl+Alt+R 剪贴板截图 / Ctrl+Alt+Q 区域截图）
/// 和剪贴板变更消息，串联 划词 / 截图OCR → 单例弹窗 → 朗读 / 翻译 的完整流程。
/// </summary>
public partial class MainWindow : Window
{
    private const int HotkeyReadClipboardImage = 0x1001;
    private const int HotkeySnipAndRead = 0x1002;

    private readonly TtsService _tts = new();
    private SelectionWatcher? _watcher;
    private HwndSource? _source;

    // 全程序唯一的弹窗实例
    private PopupWindow? _popup;

    // 防重入：OCR 流程进行中不再触发新的识别
    private bool _ocrBusy;
    // 剪贴板图片去重（同一张图不重复弹窗）
    private uint _lastImageSeq;
    // 内置截图是否正在框选
    private bool _snipActive;

    /// <summary>开关状态变化（弹窗复选框、托盘菜单据此双向同步）。</summary>
    public event Action? TogglesChanged;

    /// <summary>划词朗读开关：写入 AppSettings 记忆并立即生效。</summary>
    public bool SelectionWatchEnabled
    {
        get => AppSettings.SelectionEnabled;
        set
        {
            AppSettings.SelectionEnabled = value;
            AppSettings.Save();
            if (_watcher != null) _watcher.Enabled = value;
            TogglesChanged?.Invoke();
        }
    }

    /// <summary>截图识别开关：写入 AppSettings 记忆并立即生效。</summary>
    public bool ScreenshotWatchEnabled
    {
        get => AppSettings.ScreenshotEnabled;
        set
        {
            AppSettings.ScreenshotEnabled = value;
            AppSettings.Save();
            TogglesChanged?.Invoke();
        }
    }

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _source = (HwndSource?)PresentationSource.FromVisual(this);
        _source?.AddHook(WndProc);

        // Ctrl+Alt+R：识别剪贴板里的截图
        Win32.RegisterHotKey(_source!.Handle, HotkeyReadClipboardImage,
            Win32.MOD_CONTROL | Win32.MOD_ALT | Win32.MOD_NOREPEAT, (uint)'R');
        // Alt+Q：内置区域截图 → OCR → 弹窗
        Win32.RegisterHotKey(_source.Handle, HotkeySnipAndRead,
            Win32.MOD_ALT | Win32.MOD_NOREPEAT, (uint)'Q');

        // 监听剪贴板：截图工具（Win+Shift+S、微信 Alt+A 等）放图片进来时自动识别
        Win32.AddClipboardFormatListener(_source.Handle);

        // 划词监听（初始状态读取用户记忆）
        _watcher = new SelectionWatcher { Enabled = AppSettings.SelectionEnabled };
        _watcher.TextCaptured += OnTextCaptured;
        _watcher.Start();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Win32.WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            if (id == HotkeyReadClipboardImage || id == HotkeySnipAndRead)
            {
                // 截图识别开关关闭时，快捷键也不生效（并提示原因）
                if (!ScreenshotWatchEnabled)
                {
                    Balloon("ReadNow", "截图识别已关闭，可在托盘菜单重新开启");
                }
                else if (id == HotkeyReadClipboardImage)
                {
                    _ = ReadClipboardScreenshotAsync(notifyOnFailure: true);
                }
                else
                {
                    StartSnip();
                }
                handled = true;
            }
        }
        else if (msg == Win32.WM_CLIPBOARDUPDATE && ScreenshotWatchEnabled)
        {
            try
            {
                // 剪贴板里出现新图片（截图）时自动走 OCR 流程
                uint seq = Win32.GetClipboardSequenceNumber();
                if (seq != _lastImageSeq && System.Windows.Clipboard.ContainsImage())
                {
                    _lastImageSeq = seq;
                    _ = ReadClipboardScreenshotAsync(notifyOnFailure: false);
                }
            }
            catch { /* 剪贴板被占用时静默跳过 */ }
        }
        return IntPtr.Zero;
    }

    /// <summary>划词捕获成功 → 弹窗。</summary>
    private void OnTextCaptured(string text, Win32.POINT cursorPos)
    {
        Dispatcher.Invoke(() => ShowPopup(text, cursorPos));
    }

    /// <summary>剪贴板截图 → OCR → 弹窗。微信等工具的剪贴板图片有延迟，多重试几次。</summary>
    public async Task ReadClipboardScreenshotAsync(bool notifyOnFailure)
    {
        if (_ocrBusy) return;
        _ocrBusy = true;
        try
        {
            string? text = null;
            for (int i = 0; i < 5 && text == null; i++)
            {
                if (i > 0) await Task.Delay(300);
                if (!System.Windows.Clipboard.ContainsImage()) continue;
                text = await OcrService.RecognizeClipboardImageAsync();
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                if (notifyOnFailure)
                    Balloon("ReadNow", "剪贴板里没有截图，或图中没有识别到文字");
                return;
            }

            Win32.GetCursorPos(out var pos);
            ShowPopup(text, pos);
        }
        finally
        {
            _ocrBusy = false;
        }
    }

    /// <summary>内置区域截图（Ctrl+Alt+Q）。</summary>
    private void StartSnip()
    {
        if (_snipActive) return;
        _snipActive = true;

        var overlay = new SnipOverlayWindow();
        overlay.RegionCaptured += async (image, pos) =>
        {
            _snipActive = false;
            var text = await OcrService.RecognizeImageAsync(image);
            if (string.IsNullOrWhiteSpace(text))
            {
                Balloon("ReadNow", "截图里没有识别到文字");
                return;
            }
            ShowPopup(text, pos);
        };
        overlay.Cancelled += () => _snipActive = false;
        overlay.Show();
        overlay.Activate();
    }

    /// <summary>调试：立即弹一个示例窗口。</summary>
    public void DebugShowPopup()
    {
        Win32.GetCursorPos(out var pos);
        ShowPopup("这是一个 ReadNow 测试弹窗。Hello, this is a test of the popup window.", pos);
    }

    /// <summary>单例弹窗：已存在则刷新内容并移到光标处，不存在才新建。</summary>
    private void ShowPopup(string text, Win32.POINT nearPos)
    {
        if (_popup != null)
        {
            _popup.UpdateText(text);
            _popup.MoveNearCursor(nearPos);
            if (!_popup.IsActive) _popup.Activate();
            return;
        }

        _popup = new PopupWindow(text, _tts);
        _popup.Closed += (_, _) => _popup = null;
        _popup.ShowNearCursor(nearPos);
    }

    private static void Balloon(string title, string message)
    {
        if (System.Windows.Application.Current is App app)
            app.Balloon(title, message);
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_source != null)
        {
            Win32.RemoveClipboardFormatListener(_source.Handle);
            Win32.UnregisterHotKey(_source.Handle, HotkeyReadClipboardImage);
            Win32.UnregisterHotKey(_source.Handle, HotkeySnipAndRead);
            _source.RemoveHook(WndProc);
        }
        _watcher?.Dispose();
        _tts.Dispose();
        base.OnClosed(e);
    }
}
