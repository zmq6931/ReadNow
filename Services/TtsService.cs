using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Media;
using Windows.Media.SpeechSynthesis;
using WinRTSynth = Windows.Media.SpeechSynthesis.SpeechSynthesizer;

namespace ReadNow.Services;

/// <summary>
/// 朗读服务 v2：基于 Windows 新一代语音引擎（OneCore 自然语音，如"晓晓"），离线可用。
/// - 原生数字倍速（0.5x~6.0x）
/// - 按句子队列播放，停止时记住句号，下次从该句继续
/// - 支持声音列表选择（女声标注）
/// </summary>
public sealed class TtsService : IDisposable
{
    public const double MinSpeed = 0.5;
    public const double MaxSpeed = 4.0;

    private readonly MediaPlayer _player = new();
    private readonly WinRTSynth _synth = new();
    private readonly SemaphoreSlim _synthLock = new(1, 1);
    private CancellationTokenSource? _cts;

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "ReadNowTTS");

    private List<string> _sentences = new();
    private string? _lastText;
    private int _bookmarkSentence;   // 停止时记住的句索引
    private int _currentSentence;
    private bool _speaking;
    private double _speed = 1.0;
    private VoiceInformation? _activeVoice;

    // ---- 声音列表 ----
    public sealed record VoiceChoice(string Id, string Display, bool IsFemale, string Language);
    public IReadOnlyList<VoiceChoice> Voices { get; }

    /// <summary>用户选定的声音 Id；null 表示自动（按文本语言优先选女声）。</summary>
    public string? SelectedVoiceId { get; set; }

    public bool IsSpeaking => _speaking;
    /// <summary>本次播放起点句索引（>0 表示续播），供 UI 提示。</summary>
    public int LastStartOffset { get; private set; }
    public bool HasBookmark => _bookmarkSentence > 0;

    public event Action? SpeakEnded;
    /// <summary>播放进度：(当前句 1-based, 总句数)。</summary>
    public event Action<int, int>? SentenceProgress;

    public TtsService()
    {
        Voices = WinRTSynth.AllVoices
            .Select(v => new VoiceChoice(
                v.Id,
                v.DisplayName.Replace("Microsoft ", "").Replace(" Desktop", ""),
                v.Gender == VoiceGender.Female,
                v.Language))
            .ToList();

        Directory.CreateDirectory(_tempDir);
        CleanOldTempFiles();
    }

    public void Speak(string text, double speed)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        _speed = Math.Clamp(speed, MinSpeed, MaxSpeed);

        int start;
        if (text != _lastText)
        {
            _lastText = text;
            _sentences = SplitSentences(text);
            _bookmarkSentence = 0;
            start = 0;
        }
        else
        {
            // 同一文本：正在播 → 从当前句重读（用于变速/换声）；停了 → 从书签续播
            start = _speaking
                ? _currentSentence
                : Math.Min(_bookmarkSentence, Math.Max(0, _sentences.Count - 1));
        }
        LastStartOffset = start;
        _activeVoice = ResolveVoice(text);

        CancelPlayback();
        _cts = new CancellationTokenSource();
        _speaking = true;
        _currentSentence = start;
        _ = PlayLoopAsync(start, _cts.Token);
    }

    /// <summary>停止朗读。remember=true 时记住句位置（默认），下次同文本续播。</summary>
    public void Stop(bool remember = true)
    {
        if (_speaking && remember)
            _bookmarkSentence = _currentSentence;
        CancelPlayback();
        SpeakEnded?.Invoke();
    }

    /// <summary>从头播放（忽略书签）。</summary>
    public void Restart(string text, double speed)
    {
        _bookmarkSentence = 0;
        if (text == _lastText) _lastText = null; // 强制重建句子队列
        Speak(text, speed);
    }

    private void CancelPlayback()
    {
        _cts?.Cancel();
        _cts = null;
        try { _player.Close(); } catch { } // Close 释放文件句柄
        _speaking = false;
    }

    private async Task PlayLoopAsync(int start, CancellationToken ct)
    {
        try
        {
            for (int i = start; i < _sentences.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                _currentSentence = i;
                SentenceProgress?.Invoke(i + 1, _sentences.Count);

                var file = await SynthesizeSentenceAsync(i, ct);
                ct.ThrowIfCancellationRequested();

                var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                void onEnded(object? s, EventArgs e) => tcs.TrySetResult(true);
                _player.MediaEnded += onEnded;
                try
                {
                    await using var reg = ct.Register(() => tcs.TrySetCanceled());
                    _player.Open(new Uri(file));
                    _player.Play();
                    await tcs.Task;
                }
                finally
                {
                    _player.MediaEnded -= onEnded;
                    _player.Close(); // Close 才会释放音频文件句柄，Stop 不会
                }
            }

            // 自然读完 → 书签清零
            _bookmarkSentence = 0;
            _speaking = false;
            SpeakEnded?.Invoke();
        }
        catch (OperationCanceledException)
        {
            // 被停止：书签已在 Stop() 里记录
        }
        catch (Exception ex)
        {
            App.LogError(ex); // 不再静默吞错
            _speaking = false;
            SpeakEnded?.Invoke();
        }
    }

    private async Task<string> SynthesizeSentenceAsync(int index, CancellationToken ct)
    {
        await _synthLock.WaitAsync(ct);
        try
        {
            if (_activeVoice != null && _synth.Voice.Id != _activeVoice.Id)
                _synth.Voice = _activeVoice;
            _synth.Options.SpeakingRate = _speed;

            using var stream = await _synth.SynthesizeTextToStreamAsync(_sentences[index]);
            // 每次合成用唯一文件名，避免与仍被播放器占用的旧文件冲突
            var file = Path.Combine(_tempDir, $"tts_{Guid.NewGuid():N}.wav");
            await using (var fs = File.Create(file))
            {
                await stream.AsStreamForRead().CopyToAsync(fs, 81920, ct);
            }
            return file;
        }
        finally
        {
            _synthLock.Release();
        }
    }

    /// <summary>声音决策：用户指定优先；否则按文本语言优先选女声。</summary>
    private VoiceInformation? ResolveVoice(string text)
    {
        var all = WinRTSynth.AllVoices;
        if (!string.IsNullOrEmpty(SelectedVoiceId))
        {
            var chosen = all.FirstOrDefault(v => v.Id == SelectedVoiceId);
            if (chosen != null) return chosen;
        }

        bool wantChinese = text.Any(c => c >= 0x4E00 && c <= 0x9FFF);
        string lang = wantChinese ? "zh" : "en";
        return all.FirstOrDefault(v => v.Language.StartsWith(lang) && v.Gender == VoiceGender.Female)
               ?? all.FirstOrDefault(v => v.Language.StartsWith(lang))
               ?? WinRTSynth.DefaultVoice;
    }

    /// <summary>按句子切分长文（句读/bookmark 的粒度）。</summary>
    private static List<string> SplitSentences(string text)
    {
        var rough = Regex.Split(text, @"(?<=[。！？!?；;…\n])");
        var result = new List<string>();
        foreach (var part in rough)
        {
            var p = part.Trim();
            if (p.Length == 0) continue;
            if (p.Length <= 180)
            {
                result.Add(p);
            }
            else
            {
                // 超长句按逗号再切
                var fine = Regex.Split(p, @"(?<=[，,、：:])");
                var buf = "";
                foreach (var f in fine)
                {
                    if ((buf + f).Length > 180 && buf.Length > 0)
                    {
                        result.Add(buf.Trim());
                        buf = f;
                    }
                    else buf += f;
                }
                if (buf.Trim().Length > 0) result.Add(buf.Trim());
            }
        }
        if (result.Count == 0 && text.Trim().Length > 0) result.Add(text.Trim());
        return result;
    }

    private void CleanOldTempFiles()
    {
        try
        {
            // 唯一文件名会累积，启动时清掉旧的（被占用就跳过）
            foreach (var f in Directory.GetFiles(_tempDir, "tts_*.wav"))
            {
                try { File.Delete(f); } catch { }
            }
        }
        catch { }
    }

    public void Dispose()
    {
        CancelPlayback();
        try { _player.Close(); } catch { }
        _synth.Dispose();
    }
}
