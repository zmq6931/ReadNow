using System.IO;
using System.Text.RegularExpressions;
using Windows.Foundation;
using Windows.Media.SpeechSynthesis;
using WinRTSynth = Windows.Media.SpeechSynthesis.SpeechSynthesizer;
using WinRTPlayer = Windows.Media.Playback.MediaPlayer;

namespace ReadNow.Services;

/// <summary>
/// 朗读服务 v5：Edge 在线神经语音（晓曼/晓晓，需联网）+ 本地自然语音包 + 离线兜底。
/// - 弹窗出现时即预合成：当前声音前三句 + 另一个声音前三句，切声音零等待
/// - 朗读中备选声音同步预取当前句和后两句，中途切声音也近乎零等待
/// - 倍速用播放器变速（音调校正），拖滑块零延迟、不回句首
/// - 播放中后台预取下一句，句间无等待
/// - 精确暂停：音频停在原位置，一词不差地继续
/// </summary>
public sealed class TtsService : IDisposable
{
    public const double MinSpeed = 0.5;
    public const double MaxSpeed = 4.0;

    private readonly WinRTPlayer _player = new(); // WinRT 播放器：变速带音调校正（不变尖）
    private readonly WinRTSynth _synth = new(); // 离线兜底
    private readonly SemaphoreSlim _synthLock = new(1, 1);
    private CancellationTokenSource? _cts;

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "ReadNowTTS");

    private List<string> _sentences = new();
    private string? _lastText;
    private int _bookmarkSentence;   // 停止时记住的句索引（兜底用）
    private int _currentSentence;
    private bool _speaking;
    private bool _paused;            // 精确暂停状态：音频停在原位置
    private bool _playerOpen;        // 播放器里是否已加载音频
    private double _speed = 1.0;

    // ---- 弹窗预合成：key = (声音, 句索引)，两个声音都备 ----
    private readonly Dictionary<(string Voice, int Index), Task<string>> _prepared = new();
    private string? _preparedText;
    private string? _preparedVoiceId;
    private CancellationTokenSource? _preparedCts;

    // ---- 声音列表（Edge 在线神经语音）----
    public sealed record VoiceChoice(string Id, string Display, string Pitch);

    public IReadOnlyList<VoiceChoice> Voices { get; } = new List<VoiceChoice>
    {
        new("zh-CN-XiaoxiaoNeural", "晓晓 · 普通话+英语（标准女声）", "+0Hz"),
        new("zh-HK-HiuMaanNeural", "晓曼 · 粤语+英语（港味女声）", "-10Hz"),
    };

    /// <summary>用户选定的声音 Id（Edge 语音名）。</summary>
    public string SelectedVoiceId { get; set; }

    public bool IsSpeaking => _speaking;
    /// <summary>精确暂停中：音频停在原位置，Resume 可一词不差地继续。</summary>
    public bool IsPaused => _paused;
    /// <summary>正在联网合成语音（还没开始出声）。此时点"暂停"会把合成取消，UI 应拦截。</summary>
    public bool IsBuffering => _speaking && !_playerOpen && !_paused;
    /// <summary>本次播放起点句索引（>0 表示续播），供 UI 提示。</summary>
    public int LastStartOffset { get; private set; }
    public bool HasBookmark => _bookmarkSentence > 0;

    public event Action? SpeakEnded;
    /// <summary>播放进度：(当前句 1-based, 总句数)。</summary>
    public event Action<int, int>? SentenceProgress;

    public TtsService()
    {
        // 读取用户记忆的声音；非法值回退默认（晓晓·标准普通话）
        SelectedVoiceId = Voices.Any(v => v.Id == AppSettings.VoiceId)
            ? AppSettings.VoiceId
            : Voices[0].Id;

        Directory.CreateDirectory(_tempDir);
        CleanOldTempFiles();
    }

    /// <summary>
    /// 弹窗展示文本时调用：预先合成当前声音的前三句 + 另一个声音的前三句。
    /// 用户点播放 / 切声音时直接用现成的，近乎零等待。
    /// </summary>
    public void PrepareText(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || _speaking || _paused) return;
        if (text == _lastText) return; // 续播场景无需预合成
        if (text == _preparedText && _preparedVoiceId == SelectedVoiceId) return; // 已备好

        if (text != _preparedText)
        {
            ClearPrepared();
            EdgeTtsClient.WarmUp(); // 顺手把网络连接也热了
            _preparedCts = new CancellationTokenSource();
            _preparedText = text;
        }
        _preparedVoiceId = SelectedVoiceId;
        var ct = _preparedCts!.Token;
        var sentences = SplitSentences(text);

        void Prepare(string voiceId, int index)
        {
            if (index < sentences.Count && !_prepared.ContainsKey((voiceId, index)))
                _prepared[(voiceId, index)] = SynthesizeCoreAsync(sentences[index], voiceId, ct);
        }

        Prepare(SelectedVoiceId, 0);
        Prepare(SelectedVoiceId, 1);
        Prepare(SelectedVoiceId, 2);
        // 备选声音排在主声音第一句之后，避免抢带宽拖慢正在用的声音
        var alt = Voices.FirstOrDefault(v => v.Id != SelectedVoiceId);
        if (alt != null && _prepared.TryGetValue((SelectedVoiceId, 0), out var firstSel))
        {
            _ = Task.Run(async () =>
            {
                try { await firstSel; } catch { }
                if (_preparedText != text) return; // 文本已换新，别污染新的预合成
                Prepare(alt.Id, 0);
                Prepare(alt.Id, 1);
                Prepare(alt.Id, 2);
            });
        }
    }

    private void ClearPrepared()
    {
        try { _preparedCts?.Cancel(); } catch { }
        _preparedCts = null;
        _preparedText = null;
        _prepared.Clear();
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
            // 同一文本：正在播 → 换声：记住当前句的播放进度比例，新声音从同一位置接着播
            // （不切句子，这样预合成的备选声音整句音频直接能用，切换近乎零等待）
            if (_speaking)
            {
                double frac = GetCurrentFraction();
                start = _currentSentence;
                if (frac >= 0.97) { start++; _pendingSeekFrac = -1; }      // 句尾就当这句读完了
                else if (frac is > 0.03 and < 0.97) _pendingSeekFrac = frac; // 句中：定位续播
                else _pendingSeekFrac = -1;                                 // 句首：从头读
                if (start >= _sentences.Count)
                {
                    // 已经在最后一句的末尾：视为读完，不再重读
                    CancelPlayback();
                    _bookmarkSentence = 0;
                    SpeakEnded?.Invoke();
                    return;
                }
            }
            else
            {
                start = Math.Min(_bookmarkSentence, Math.Max(0, _sentences.Count - 1));
            }
        }
        LastStartOffset = start;

        CancelPlayback();
        _cts = new CancellationTokenSource();
        _speaking = true;
        _currentSentence = start;
        _ = PlayLoopAsync(start, _cts.Token);
    }

    /// <summary>
    /// 朗读中变速：直接调播放器速度（带音调校正），零延迟、位置一丝不动。
    /// </summary>
    public void ChangeSpeedLive(double speed)
    {
        _speed = Math.Clamp(speed, MinSpeed, MaxSpeed);
        ApplyPlaybackRate();
    }

    /// <summary>把当前倍速应用到播放器（每句开始播放、恢复播放、变速时调用）。</summary>
    private void ApplyPlaybackRate()
    {
        if (!_playerOpen) return;
        try { _player.PlaybackSession.PlaybackRate = _speed; } catch { }
    }

    /// <summary>换声时：读取当前句的播放进度比例（0~1），读不到返回 -1。</summary>
    private double GetCurrentFraction()
    {
        try
        {
            var session = _player.PlaybackSession;
            if (_playerOpen && session.NaturalDuration.TotalMilliseconds > 0)
                return session.Position.TotalMilliseconds / session.NaturalDuration.TotalMilliseconds;
        }
        catch { }
        return -1;
    }

    /// <summary>换声续播：PlayLoopAsync 打开第一句音频后要跳到的进度比例（-1 表示不跳转）。</summary>
    private double _pendingSeekFrac = -1;

    /// <summary>释放当前音频（WinRT 播放器置空 Source 即释放文件句柄）。</summary>
    private void ClosePlayer()
    {
        try { _player.Source = null; } catch { }
        _playerOpen = false;
    }

    /// <summary>精确暂停：音频停在原位置，Resume() 从同一位置继续。</summary>
    public void Pause()
    {
        if (!_speaking) return;
        _bookmarkSentence = _currentSentence; // 兜底书签照记
        _paused = _playerOpen; // 只有播放器里已加载音频才有"精确暂停"可言
        _cts?.Cancel();
        _cts = null;
        _speaking = false;
        if (_paused)
        {
            try { _player.Pause(); } catch { }
        }
        SpeakEnded?.Invoke();
    }

    /// <summary>从精确暂停处继续（一词不差）。</summary>
    public void Resume(double speed)
    {
        if (!_paused) return;
        _paused = false;
        _speaking = true;
        _speed = Math.Clamp(speed, MinSpeed, MaxSpeed);
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        // 当前句播完后，从下一句接着走
        TypedEventHandler<WinRTPlayer, object?>? onEnded = null;
        onEnded = (s, e) =>
        {
            _player.MediaEnded -= onEnded;
            _playerOpen = false;
            if (!ct.IsCancellationRequested)
                _ = PlayLoopAsync(_currentSentence + 1, ct);
        };
        _player.MediaEnded += onEnded;
        try
        {
            ApplyPlaybackRate(); // 播放器速度要在 Play 前恢复（_playerOpen 仍为 true）
            _player.Play();
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            // 恢复失败就退化为从当前句重读
            Speak(_lastText ?? "", speed);
        }
    }

    /// <summary>停止朗读。remember=true 时记住句位置（默认），下次同文本续播。</summary>
    public void Stop(bool remember = true)
    {
        if (_speaking && remember)
            _bookmarkSentence = _currentSentence;
        _paused = false;
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
        _paused = false;
        ClosePlayer(); // 释放文件句柄
        _speaking = false;
    }

    private async Task PlayLoopAsync(int start, CancellationToken ct)
    {
        var myCts = _cts; // 本循环的身份标记：旧循环被取消后，不得碰新循环加载的音频
        try
        {
            var pending = new Dictionary<int, Task<string>>(); // 深度预取：高倍速下播放比合成快，一句提前量不够
            for (int i = start; i < _sentences.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                _currentSentence = i;
                SentenceProgress?.Invoke(i + 1, _sentences.Count);

                // 不等当前句，先把后面两句也发出去合成（三句并行流水线）
                for (int j = i + 1; j <= i + 2 && j < _sentences.Count; j++)
                    if (!pending.ContainsKey(j))
                        pending[j] = SynthesizeSentenceAsync(j, ct);

                // 备选声音同步预取当前句和后面两句：朗读中切声音也能秒切换
                PrefetchAltVoice(i, ct);

                string file;
                try
                {
                    file = pending.Remove(i, out var task)
                        ? await task
                        : await SynthesizeSentenceAsync(i, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // 预取失败（如断网后恢复）→ 现场重新合成一次（内部有离线兜底）
                    App.LogError(ex);
                    file = await SynthesizeCoreAsync(_sentences[i], SelectedVoiceId, ct);
                }
                ct.ThrowIfCancellationRequested();

                var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                TypedEventHandler<WinRTPlayer, object?> onEnded = (s, e) => tcs.TrySetResult(true);
                _player.MediaEnded += onEnded;

                // 换声续播：第一句音频打开后跳到和旧声音相同的进度位置
                double seekFrac = i == start ? _pendingSeekFrac : -1;
                _pendingSeekFrac = -1;
                TypedEventHandler<WinRTPlayer, object?>? onOpened = null;
                if (seekFrac > 0)
                {
                    onOpened = (s, e) =>
                    {
                        _player.MediaOpened -= onOpened;
                        try
                        {
                            var dur = _player.PlaybackSession.NaturalDuration;
                            if (dur.TotalMilliseconds > 0)
                                _player.PlaybackSession.Position =
                                    TimeSpan.FromMilliseconds(dur.TotalMilliseconds * seekFrac);
                        }
                        catch { }
                    };
                    _player.MediaOpened += onOpened;
                }

                try
                {
                    await using var reg = ct.Register(() => tcs.TrySetCanceled());
                    _player.Source = Windows.Media.Core.MediaSource.CreateFromUri(new Uri(file));
                    _playerOpen = true;
                    ApplyPlaybackRate();
                    _player.Play();
                    SentenceProgress?.Invoke(i + 1, _sentences.Count); // 出声了，刷新"加载中"状态

                    await tcs.Task;
                }
                finally
                {
                    _player.MediaEnded -= onEnded;
                    if (onOpened != null) _player.MediaOpened -= onOpened;
                    // 暂停时不释放：Resume 还要从原位置继续。
                    // 令牌已换（切声音/重播）时也不释放：播放器里的音频已经是新循环
                    // 刚加载的，旧循环若关掉它，新循环就永远等不到播完事件——全程无声
                    if (!_paused && ReferenceEquals(_cts, myCts))
                        ClosePlayer();
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

    /// <summary>合成第 index 句：优先吃弹窗预合成的现成结果（两个声音都可能有）。</summary>
    private async Task<string> SynthesizeSentenceAsync(int index, CancellationToken ct)
    {
        if (_preparedText == _lastText
            && _prepared.Remove((SelectedVoiceId, index), out var ready))
        {
            try { return await ready; }
            catch (Exception ex)
            {
                // 预合成失败/被取消（如切声音时的令牌切换）→ 现场重来。
                // 注意连 OperationCanceledException 也要兜住：那是旧令牌的取消，
                // 不是当前播放循环的取消，不能让它误杀整个播放
                if (ex is not OperationCanceledException) App.LogError(ex);
            }
        }
        return await SynthesizeCoreAsync(_sentences[index], SelectedVoiceId, ct);
    }

    /// <summary>朗读中后台合成备选声音的当前句和后三句，存进 _prepared。
    /// 用户中途切声音时直接有现成音频，不用等联网合成。
    /// 注意：故意用预合成的取消令牌而不是播放令牌——切声音时播放令牌会被取消，
    /// 若用播放令牌，备选声音的预取会在最需要它的那一刻被全部杀掉。</summary>
    private void PrefetchAltVoice(int currentIndex, CancellationToken ct)
    {
        if (_preparedText != _lastText) return; // 预合成表对不上当前文本，存了也用不上
        var alt = Voices.FirstOrDefault(v => v.Id != SelectedVoiceId);
        if (alt == null) return;
        var altCt = _preparedCts?.Token ?? ct;
        for (int j = currentIndex; j <= currentIndex + 3 && j < _sentences.Count; j++)
        {
            if (_prepared.ContainsKey((alt.Id, j))) continue;
            int idx = j; // 闭包捕获
            _prepared[(alt.Id, idx)] = SynthesizeCoreAsync(_sentences[idx], alt.Id, altCt);
        }
    }

    /// <summary>英文声音：整句无汉字时由她读（数字、单位原生英文念法）。
    /// Aria +20Hz 高音调配方（用户试听后选定）。</summary>
    private const string EnglishVoiceId = "en-US-AriaNeural";
    private const string EnglishVoicePitch = "+20Hz";

    /// <summary>合成一句：中英文自动分路（无汉字 → Aria 英文声音）；
    /// 中文句有本地自然语音包就本地合成；否则走 Edge 在线；失败回退系统离线语音。
    /// 注意：永远按 1 倍速合成，倍速由播放器变速实现。</summary>
    private async Task<string> SynthesizeCoreAsync(string sentence, string voiceId, CancellationToken ct)
    {
        var voice = Voices.FirstOrDefault(v => v.Id == voiceId) ?? Voices[0];
        bool hasCjk = sentence.Any(c => c >= 0x4E00 && c <= 0x9FFF);

        // 中文句：本地自然语音（Win11 讲述人下载的 Natural 语音包）优先，零网络延迟
        if (hasCjk)
        {
            string localName = voice.Id switch
            {
                "zh-CN-XiaoxiaoNeural" => "Xiaoxiao",
                "zh-HK-HiuMaanNeural" => "HiuMaan",
                _ => ""
            };
            if (localName.Length > 0)
            {
                var local = FindLocalNaturalVoice(localName);
                if (local != null)
                    return await SynthesizeOfflineAsync(sentence, ct, local);
            }
        }

        string useVoice = hasCjk ? voice.Id : EnglishVoiceId;
        string usePitch = hasCjk ? voice.Pitch : EnglishVoicePitch;

        // 在线合成：失败时换一条连接重试一次（偶发的网络抖动/服务器无响应，
        // 重试大多能成功）。两次都失败才回退离线语音——晓曼若直接回退会被
        // 系统普通话声音接管，听起来就像"晓曼突然变成普通话"
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var mp3 = await EdgeTtsClient.SynthesizeAsync(sentence, useVoice, "+0%", usePitch, ct);
                var file = Path.Combine(_tempDir, $"tts_{Guid.NewGuid():N}.mp3");
                await File.WriteAllBytesAsync(file, mp3, ct);
                return file;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                App.LogError(ex); // 记录原因，重试或走离线兜底
            }
        }
        return await SynthesizeOfflineAsync(sentence, ct, preferredLang: LangOf(useVoice));
    }

    /// <summary>从 Edge 语音名提取语言标记（zh-HK-HiuMaanNeural → zh-HK），供离线兜底选同语言声音。</summary>
    private static string LangOf(string edgeVoiceId)
    {
        var parts = edgeVoiceId.Split('-');
        return parts.Length >= 2 ? parts[0] + "-" + parts[1] : edgeVoiceId;
    }

    /// <summary>查找已安装的本地自然语音（如讲述人里下载的"晓晓 Natural"）。</summary>
    private static VoiceInformation? FindLocalNaturalVoice(string namePart) =>
        WinRTSynth.AllVoices.FirstOrDefault(v =>
            v.DisplayName.Contains(namePart, StringComparison.OrdinalIgnoreCase) &&
            (v.DisplayName.Contains("Natural", StringComparison.OrdinalIgnoreCase) ||
             v.Id.Contains("Natural", StringComparison.OrdinalIgnoreCase)));

    /// <summary>离线兜底：OneCore 系统语音（断网时用，音质一般但保证可用）。1 倍速合成。
    /// preferredLang：优先选该语言的系统声音（如 zh-HK），避免晓曼失败时被普通话声音顶替。</summary>
    private async Task<string> SynthesizeOfflineAsync(string sentence, CancellationToken ct,
        VoiceInformation? forceVoice = null, string? preferredLang = null)
    {
        await _synthLock.WaitAsync(ct);
        try
        {
            var voice = forceVoice ?? ResolveOfflineVoice(sentence, preferredLang);
            if (voice != null && _synth.Voice.Id != voice.Id)
                _synth.Voice = voice;
            _synth.Options.SpeakingRate = 1.0; // 倍速统一由播放器实现

            using var stream = await _synth.SynthesizeTextToStreamAsync(sentence);
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

    /// <summary>离线语音决策：优先 preferredLang（如晓曼→zh-HK）的女声，其次按文本语言选女声。</summary>
    private VoiceInformation? ResolveOfflineVoice(string text, string? preferredLang = null)
    {
        var all = WinRTSynth.AllVoices;
        if (!string.IsNullOrEmpty(preferredLang))
        {
            var exact = all.FirstOrDefault(v =>
                            v.Language.Equals(preferredLang, StringComparison.OrdinalIgnoreCase)
                            && v.Gender == VoiceGender.Female)
                        ?? all.FirstOrDefault(v =>
                            v.Language.Equals(preferredLang, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;
        }
        bool wantChinese = text.Any(c => c >= 0x4E00 && c <= 0x9FFF);
        string lang = wantChinese ? "zh" : "en";
        return all.FirstOrDefault(v => v.Language.StartsWith(lang) && v.Gender == VoiceGender.Female)
               ?? all.FirstOrDefault(v => v.Language.StartsWith(lang))
               ?? WinRTSynth.DefaultVoice;
    }

    /// <summary>按句子切分长文（句读/bookmark 的粒度）。超过约 48 字就按逗号再切：
    /// 短句合成更快、首句出声更早，后续靠预取保持连贯。</summary>
    private static List<string> SplitSentences(string text)
    {
        var rough = Regex.Split(text, @"(?<=[。！？!?；;…\n])");
        var result = new List<string>();
        foreach (var part in rough)
        {
            var p = part.Trim();
            if (p.Length == 0) continue;
            if (p.Length <= 48)
            {
                result.Add(p);
            }
            else
            {
                // 超长句按逗号再切。注意保护数字千分位："23,000" 不能切成 "23"+"000"，
                // 否则单独的数字段会被中文语音按中文数字念出来
                var protectedText = Regex.Replace(p, @"(\d),(?=\d{3}\b)", "$1◆");
                var fine = Regex.Split(protectedText, @"(?<=[，,、：:])");
                var buf = "";
                foreach (var f in fine)
                {
                    if ((buf + f).Length > 48 && buf.Length > 0)
                    {
                        result.Add(buf.Trim().Replace("◆", ","));
                        buf = f;
                    }
                    else buf += f;
                }
                if (buf.Trim().Length > 0) result.Add(buf.Trim().Replace("◆", ","));
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
            foreach (var f in Directory.GetFiles(_tempDir, "tts_*.*"))
            {
                try { File.Delete(f); } catch { }
            }
        }
        catch { }
    }

    public void Dispose()
    {
        ClearPrepared();
        CancelPlayback();
        try { _player.Dispose(); } catch { }
        _synth.Dispose();
    }
}
