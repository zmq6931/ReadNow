using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ReadNow.Services;

namespace ReadNow.Views;

/// <summary>
/// 划词 / 截图识别后弹出的操作小窗：朗读、倍速滑块、声音选择、中英互译。
/// 可拖动；全程序只存在一个实例，新内容会刷新它。
/// </summary>
public partial class PopupWindow : Window
{
    private readonly TtsService _tts;
    private string _text;
    private bool _translating;

    // 滑块防抖：拖动结束 300ms 后才真正应用速度。
    // 注意：XAML 加载阶段（设置 Minimum 时）就会触发 ValueChanged，此时本字段还是 null，必须可空。
    private DispatcherTimer? _speedDebounce;
    private int _sentenceCurrent, _sentenceTotal;

    public PopupWindow(string text, TtsService tts)
    {
        InitializeComponent();
        _text = text;
        _tts = tts;

        PreviewText.Text = text;

        // 先创建防抖定时器，再给滑块赋值（赋值会触发 ValueChanged）
        _speedDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _speedDebounce.Tick += (_, _) =>
        {
            _speedDebounce.Stop();
            ApplySpeed();
        };

        // 倍速滑块：读取全局记忆
        SpeedSlider.Value = AppSettings.Speed;
        SpeedLabel.Text = FormatSpeed(AppSettings.Speed);

        _tts.SpeakEnded += OnSpeakEnded;
        _tts.SentenceProgress += OnSentenceProgress;

        // 功能开关复选框：与托盘菜单双向同步
        SyncTogglesFromHost();
        HostWindow.TogglesChanged += OnHostTogglesChanged;

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) CloseAndStop();
        };
        Closed += (_, _) =>
        {
            _tts.SpeakEnded -= OnSpeakEnded;
            _tts.SentenceProgress -= OnSentenceProgress;
            HostWindow.TogglesChanged -= OnHostTogglesChanged;
        };
    }

    private static MainWindow HostWindow => (MainWindow)System.Windows.Application.Current.MainWindow;
    private bool _updatingToggles;

    private void Toggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingToggles) return;
        // 只更新被点击的那个开关：若两个都写，第一个写入触发的同步事件会把
        // 第二个复选框刷回旧值，导致它永远改不动（截图开关点不掉的 bug）
        if (ReferenceEquals(sender, SelectionToggle))
            HostWindow.SelectionWatchEnabled = SelectionToggle.IsChecked == true;
        else
            HostWindow.ScreenshotWatchEnabled = ScreenshotToggle.IsChecked == true;
    }

    private void OnHostTogglesChanged()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(OnHostTogglesChanged); return; }
        SyncTogglesFromHost();
    }

    private void SyncTogglesFromHost()
    {
        _updatingToggles = true;
        SelectionToggle.IsChecked = HostWindow.SelectionWatchEnabled;
        ScreenshotToggle.IsChecked = HostWindow.ScreenshotWatchEnabled;
        StartupToggle.IsChecked = StartupService.IsEnabled();
        _updatingToggles = false;
    }

    private void StartupToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingToggles) return;
        try
        {
            StartupService.SetEnabled(StartupToggle.IsChecked == true);
            ShowStatus(StartupToggle.IsChecked == true ? "已开启开机自动启动 ✓" : "已关闭开机自动启动 ✓");
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += (_, _) => { timer.Stop(); HideStatus(); };
            timer.Start();
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            ShowStatus("设置开机启动失败");
        }
    }

    /// <summary>用新内容刷新弹窗（单例模式下复用同一窗口）。</summary>
    public void UpdateText(string text)
    {
        _text = text;
        PreviewText.Text = text;
        TranslationPanel.Visibility = Visibility.Collapsed;
        HideStatus();
        if (_tts.IsSpeaking || _tts.IsPaused) _tts.Stop(); // 新内容来了，彻底停掉旧的
    }

    /// <summary>弹出并放到光标附近，强制拿到前台焦点（空格/回车即播放）。</summary>
    public void ShowNearCursor(Native.Win32.POINT screenPixelPos)
    {
        Show();
        UpdateLayout(); // 强制布局，拿到真实尺寸后再校正边缘
        MoveNearCursor(screenPixelPos);
        ForceActivateAndFocus();
    }

    /// <summary>
    /// 强制激活窗口并把键盘焦点给"朗读"按钮。
    /// 划词/Ctrl+A 时用户正在用别的软件，普通 Activate() 会被 Windows 防抢占机制拦下，
    /// 需要用 AttachThreadInput 借道前台线程。
    /// </summary>
    public void ForceActivateAndFocus()
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            var foreground = Native.Win32.GetForegroundWindow();
            uint fgThread = Native.Win32.GetWindowThreadProcessId(foreground, out _);
            uint ourThread = Native.Win32.GetCurrentThreadId();
            if (fgThread != ourThread)
                Native.Win32.AttachThreadInput(ourThread, fgThread, true);
            try
            {
                Native.Win32.SetForegroundWindow(hwnd);
                Native.Win32.BringWindowToTop(hwnd);
            }
            finally
            {
                if (fgThread != ourThread)
                    Native.Win32.AttachThreadInput(ourThread, fgThread, false);
            }
        }
        catch { /* 拿不到焦点也不影响功能 */ }

        Activate();
        SpeakButton.Focus();
        Keyboard.Focus(SpeakButton);
    }

    /// <summary>移动（已显示的）窗口到光标附近，保证不超出屏幕工作区，并重置焦点到朗读按钮。</summary>
    public void MoveNearCursor(Native.Win32.POINT screenPixelPos)
    {
        double pdi = 1.0;
        if (System.Windows.Application.Current.MainWindow != null)
            pdi = VisualTreeHelper.GetDpi(System.Windows.Application.Current.MainWindow).PixelsPerDip;

        Left = screenPixelPos.x / pdi + 12;
        Top = screenPixelPos.y / pdi + 16;

        var workArea = SystemParameters.WorkArea;
        if (Left + ActualWidth > workArea.Right) Left = workArea.Right - ActualWidth - 8;
        if (Top + ActualHeight > workArea.Bottom) Top = workArea.Bottom - ActualHeight - 8;
        if (Left < workArea.Left) Left = workArea.Left + 8;
        if (Top < workArea.Top) Top = workArea.Top + 8;

        SpeakButton.Focus();
    }

    /// <summary>按住窗口空白处拖动。</summary>
    private void RootBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch { /* 忽略拖动异常 */ }
        }
    }

    private void SpeakButton_Click(object sender, RoutedEventArgs e)
    {
        if (_tts.IsSpeaking)
        {
            _tts.Pause(); // 精确暂停：音频停在原位置
            return;
        }
        if (_tts.IsPaused)
        {
            _tts.Resume(AppSettings.Speed); // 从暂停处一词不差地继续
            SetSpeakingUi(true);
            return;
        }
        _tts.Speak(_text, AppSettings.Speed);
        SetSpeakingUi(true);
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        if (_tts.IsSpeaking) _tts.Pause(); // 停止 = 精确暂停
    }

    private void RestartButton_Click(object sender, RoutedEventArgs e)
    {
        _tts.Restart(_text, AppSettings.Speed);
        SetSpeakingUi(true);
    }

    private void SpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (SpeedLabel != null)
            SpeedLabel.Text = FormatSpeed(SpeedSlider.Value);
        _speedDebounce?.Stop();
        _speedDebounce?.Start();
    }

    private void ApplySpeed()
    {
        AppSettings.Speed = Math.Round(SpeedSlider.Value, 2);
        AppSettings.Save();
        // 正在朗读时用新速度从当前位置原地续读（按播放进度估算，不回句首）
        if (_tts.IsSpeaking)
            _tts.ChangeSpeedLive(AppSettings.Speed);

        // 拖完滑块焦点回到"朗读"按钮，空格/回车即可暂停或继续
        SpeakButton.Focus();
        Keyboard.Focus(SpeakButton);
    }

    private static string FormatSpeed(double v) => $"{v:0.##}x";

    private async void TranslateToEnglish_Click(object sender, RoutedEventArgs e)
        => await TranslateAsync("en", "英文");

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Windows.Clipboard.SetText(_text);
            ShowStatus("已复制全部文字 ✓");
            // 2 秒后清掉提示
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += (_, _) => { timer.Stop(); HideStatus(); };
            timer.Start();
        }
        catch
        {
            ShowStatus("复制失败，剪贴板被占用，请再试一次");
        }
    }

    private async void TranslateToChinese_Click(object sender, RoutedEventArgs e)
        => await TranslateAsync("zh-CN", "中文");

    private async Task TranslateAsync(string targetLang, string label)
    {
        if (_translating) return;
        _translating = true;
        try
        {
            ShowStatus($"正在翻译成{label}…");
            var result = await TranslateService.TranslateAsync(_text, targetLang);
            TranslationLabel.Text = $"译文（{label}）：";
            TranslationText.Text = result;
            TranslationPanel.Visibility = Visibility.Visible;
            HideStatus();
        }
        finally
        {
            _translating = false;
        }
    }

    private void OnSentenceProgress(int current, int total)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => OnSentenceProgress(current, total)); return; }
        _sentenceCurrent = current;
        _sentenceTotal = total;
        if (_tts.IsSpeaking) SetSpeakingUi(true);
    }

    private void OnSpeakEnded()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(OnSpeakEnded);
            return;
        }
        SetSpeakingUi(false);
    }

    private void SetSpeakingUi(bool speaking)
    {
        SpeakButton.Content = speaking ? "⏸ 暂停" : "▶ 朗读";
        if (speaking)
        {
            var resume = _tts.LastStartOffset > 0 ? "从上次位置继续 · " : "";
            var progress = _sentenceTotal > 1 ? $" · 第{_sentenceCurrent}/{_sentenceTotal}句" : "";
            ShowStatus($"{resume}正在朗读（{FormatSpeed(AppSettings.Speed)}）{progress}");
        }
        else
        {
            if (_tts.HasBookmark) ShowStatus("已停止，位置已记录，下次从这里继续");
            else HideStatus();
        }
    }

    private void ShowStatus(string text)
    {
        StatusText.Text = text;
        StatusText.Visibility = Visibility.Visible;
    }

    private void HideStatus() => StatusText.Visibility = Visibility.Collapsed;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => CloseAndStop();

    private void CloseAndStop()
    {
        _tts.Stop();
        Close();
    }
}
