using System.Diagnostics;
using System.Runtime.InteropServices;
using ReadNow.Native;

namespace ReadNow.Services;

/// <summary>
/// 全局选择监听：
/// - 鼠标钩子：检测"按下-拖动-松开"的划词；
/// - 键盘钩子：检测 Ctrl+A（全选，不拦截按键本身）。
/// 两者都通过模拟 Ctrl+C 取剪贴板文本，取词成功时触发 TextCaptured。
/// </summary>
public sealed class SelectionWatcher : IDisposable
{
    private const int MinDragPixels = 8;
    private const int MinTextLength = 1;
    // 上限 20 万字：约一本书的篇幅，正常使用等于无限制；
    // 保留一个 sanity cap 是防止误把几 MB 的日志/导出数据塞进弹窗卡死界面
    private const int MaxTextLength = 200_000;
    private const int KeyCooldownMs = 800; // Ctrl+A 连发冷却
    private const int VK_A = 0x41;

    private nint _mouseHook = nint.Zero;
    private nint _keyboardHook = nint.Zero;
    private readonly Win32.LowLevelMouseProc _mouseProc;     // 必须持有引用，防止 GC 回收委托
    private readonly Win32.LowLevelKeyboardProc _keyboardProc;

    private Win32.POINT _downPos;
    private bool _leftDown;
    private DateTime _lastKeyTrigger = DateTime.MinValue;

    public bool Enabled { get; set; } = true;

    /// <summary>参数：捕获到的文本、光标位置（屏幕物理像素）。</summary>
    public event Action<string, Win32.POINT>? TextCaptured;

    public SelectionWatcher()
    {
        _mouseProc = MouseHookCallback;
        _keyboardProc = KeyboardHookCallback;
    }

    public void Start()
    {
        using var cur = Process.GetCurrentProcess();
        using var mod = cur.MainModule!;
        var hMod = Win32.GetModuleHandle(mod.ModuleName);
        if (_mouseHook == nint.Zero)
            _mouseHook = Win32.SetWindowsHookEx(Win32.WH_MOUSE_LL, _mouseProc, hMod, 0);
        if (_keyboardHook == nint.Zero)
            _keyboardHook = Win32.SetWindowsHookEx(Win32.WH_KEYBOARD_LL, _keyboardProc, hMod, 0);
    }

    private nint MouseHookCallback(int nCode, nint wParam, nint lParam)
    {
        try
        {
            if (nCode >= 0 && Enabled)
            {
                int msg = (int)wParam;
                if (msg == Win32.WM_LBUTTONDOWN)
                {
                    var data = Marshal.PtrToStructure<Win32.MSLLHOOKSTRUCT>(lParam);
                    _downPos = data.pt;
                    _leftDown = true;
                }
                else if (msg == Win32.WM_LBUTTONUP && _leftDown)
                {
                    _leftDown = false;
                    var data = Marshal.PtrToStructure<Win32.MSLLHOOKSTRUCT>(lParam);
                    var upPos = data.pt;
                    int dx = upPos.x - _downPos.x, dy = upPos.y - _downPos.y;
                    if (Math.Sqrt(dx * dx + dy * dy) >= MinDragPixels)
                    {
                        // 异步执行，避免阻塞钩子回调（Windows 对钩子回调有超时限制）
                        Task.Run(() => TryCapture(upPos));
                    }
                }
            }
        }
        catch { /* 钩子内绝不抛出 */ }
        return Win32.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private nint KeyboardHookCallback(int nCode, nint wParam, nint lParam)
    {
        try
        {
            if (nCode >= 0 && Enabled && (int)wParam == Win32.WM_KEYDOWN)
            {
                var data = Marshal.PtrToStructure<Win32.KBDLLHOOKSTRUCT>(lParam);
                // Ctrl+A：Ctrl 处于按下状态 + A 键
                bool ctrlDown = (Win32.GetAsyncKeyState(Win32.VK_CONTROL) & 0x8000) != 0;
                if (data.vkCode == VK_A && ctrlDown)
                {
                    var now = DateTime.UtcNow;
                    if ((now - _lastKeyTrigger).TotalMilliseconds >= KeyCooldownMs)
                    {
                        _lastKeyTrigger = now;
                        Task.Run(() =>
                        {
                            Win32.GetCursorPos(out var pos);
                            TryCapture(pos);
                        });
                    }
                }
            }
        }
        catch { /* 钩子内绝不抛出 */ }
        return Win32.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    private void TryCapture(Win32.POINT cursorPos)
    {
        try
        {
            // 点在我们自己的窗口上时不触发（避免点弹窗按钮又弹一层）
            var hwnd = Win32.WindowFromPoint(cursorPos);
            Win32.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == Environment.ProcessId) return;

            uint seqBefore = Win32.GetClipboardSequenceNumber();

            // 等目标应用完成选择
            Thread.Sleep(150);
            Win32.SendCtrlC();

            // 等待剪贴板变化（最多 2 秒：全选大文档时目标应用复制耗时较长）
            uint seqAfter = seqBefore;
            for (int i = 0; i < 40; i++)
            {
                Thread.Sleep(50);
                seqAfter = Win32.GetClipboardSequenceNumber();
                if (seqAfter != seqBefore) break;
            }
            if (seqAfter == seqBefore) return; // 剪贴板没变：没有选中任何文本

            string? text = null;
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                try
                {
                    if (System.Windows.Clipboard.ContainsText())
                        text = System.Windows.Clipboard.GetText();
                }
                catch { /* 剪贴板被占用等 */ }
            });

            if (string.IsNullOrWhiteSpace(text)) return;
            text = text.Trim();
            if (text.Length < MinTextLength) return;
            if (text.Length > MaxTextLength) text = text[..MaxTextLength];
            // 排除复制文件路径等场景
            if (text.Length < 260 && text.Contains('\\') && !text.Contains(' ') && System.IO.Path.IsPathRooted(text))
                return;

            TextCaptured?.Invoke(text, cursorPos);
        }
        catch { /* 静默失败 */ }
    }

    public void Dispose()
    {
        if (_mouseHook != nint.Zero)
        {
            Win32.UnhookWindowsHookEx(_mouseHook);
            _mouseHook = nint.Zero;
        }
        if (_keyboardHook != nint.Zero)
        {
            Win32.UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = nint.Zero;
        }
    }
}
