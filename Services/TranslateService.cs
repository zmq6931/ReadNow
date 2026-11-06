using System.Net.Http;
using System.Text.Json;
using System.Web;

namespace ReadNow.Services;

/// <summary>
/// 中英互译。首选 Google 翻译免费端点，失败时回退 MyMemory 免费 API。均需要联网。
/// </summary>
public static class TranslateService
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    static TranslateService()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) ReadNow/0.1");
    }

    /// <summary>
    /// 翻译文本。targetLang: "en" 或 "zh-CN"。失败返回以 [翻译失败] 开头的说明。
    /// </summary>
    public static async Task<string> TranslateAsync(string text, string targetLang)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        // 过长文本截断，免费接口对长度有限制
        if (text.Length > 3000) text = text[..3000];

        string sourceLang = ContainsChinese(text) ? "zh-CN" : "en";
        if (sourceLang == targetLang) return text; // 源语言与目标一致，直接返回

        var viaGoogle = await TryGoogleAsync(text, sourceLang, targetLang);
        if (viaGoogle != null) return viaGoogle;

        var viaMyMemory = await TryMyMemoryAsync(text, sourceLang, targetLang);
        if (viaMyMemory != null) return viaMyMemory;

        return "[翻译失败] 两个翻译源都不可用，请检查网络连接。";
    }

    public static bool ContainsChinese(string text)
        => text.Any(c => c >= 0x4E00 && c <= 0x9FFF);

    private static async Task<string?> TryGoogleAsync(string text, string src, string dst)
    {
        try
        {
            var url = "https://translate.googleapis.com/translate_a/single"
                      + $"?client=gtx&sl={src}&tl={dst}&dt=t&q={HttpUtility.UrlEncode(text)}";
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
            // 返回结构: [[["译文","原文",...],["译文2",...]], ...]
            var sb = new System.Text.StringBuilder();
            foreach (var seg in doc.RootElement[0].EnumerateArray())
            {
                var t = seg[0].GetString();
                if (!string.IsNullOrEmpty(t)) sb.Append(t);
            }
            var result = sb.ToString().Trim();
            return string.IsNullOrEmpty(result) ? null : result;
        }
        catch { return null; }
    }

    private static async Task<string?> TryMyMemoryAsync(string text, string src, string dst)
    {
        try
        {
            // MyMemory langpair 用两位码
            string pair = $"{(src == "zh-CN" ? "zh-CN" : src)}|{(dst == "zh-CN" ? "zh-CN" : dst)}";
            var url = "https://api.mymemory.translated.net/get"
                      + $"?q={HttpUtility.UrlEncode(text)}&langpair={pair}";
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
            var result = doc.RootElement
                .GetProperty("responseData")
                .GetProperty("translatedText").GetString()?.Trim();
            return string.IsNullOrEmpty(result) ? null : result;
        }
        catch { return null; }
    }
}
