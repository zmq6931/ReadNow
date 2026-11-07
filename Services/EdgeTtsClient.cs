using System.Globalization;
using System.IO;
using System.Net.WebSockets;
using System.Text;

namespace ReadNow.Services;

/// <summary>
/// Edge 大声朗读在线神经语音接口（免费，需联网）。
/// 协议对齐 edge-tts 开源实现：WSS 连接 + Sec-MS-GEC v2 令牌。
/// 支持连接预热：弹窗出现时后台预连，正式合成时省掉握手时间。
/// 失败时抛异常，由调用方回退到系统离线语音。
/// </summary>
public static class EdgeTtsClient
{
    private const string TrustedClientToken = "6A5AA1D4EAFF4E9FB37E23D68491D6F4";
    private const string SecMsGecVersion = "2-143.0.3650.75";
    private const string WssBase =
        "wss://speech.platform.bing.com/consumer/speech/synthesize/readaloud/edge/v1";

    // ---- 连接预热池（容量 1：合成用掉一个就后台补一个）----
    private static readonly object WarmLock = new();
    private static Task<ClientWebSocket>? _warmConnect;

    /// <summary>后台预连一个 WebSocket，下次合成省掉 TCP+TLS 握手。</summary>
    public static void WarmUp()
    {
        lock (WarmLock)
        {
            if (_warmConnect == null || _warmConnect.IsCompleted)
                _warmConnect = ConnectAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// 合成一段文本为 MP3 音频字节。
    /// rate 形如 "+50%"/"-25%"，pitch 形如 "+0Hz"/"-10Hz"。
    /// </summary>
    public static async Task<byte[]> SynthesizeAsync(
        string text, string voice, string rate, string pitch, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));
        var token = timeoutCts.Token;

        var ws = await TakeWarmOrConnectAsync(token);
        try
        {
            return await SynthesizeOnSocketAsync(ws, text, voice, rate, pitch, token);
        }
        finally
        {
            ws.Dispose();
            WarmUp(); // 用掉一个补一个
        }
    }

    /// <summary>优先拿预热好的连接（还没连好就干等最多 3 秒），拿不到就现场连。</summary>
    private static async Task<ClientWebSocket> TakeWarmOrConnectAsync(CancellationToken token)
    {
        Task<ClientWebSocket>? warm;
        lock (WarmLock) { warm = _warmConnect; _warmConnect = null; }

        if (warm != null)
        {
            try
            {
                var ws = await warm.WaitAsync(TimeSpan.FromSeconds(3), token);
                if (ws.State == WebSocketState.Open) return ws;
                ws.Dispose();
            }
            catch { /* 预热失败就现场连 */ }
        }
        return await ConnectAsync(token);
    }

    private static async Task<ClientWebSocket> ConnectAsync(CancellationToken token)
    {
        var ws = new ClientWebSocket();
        var opt = ws.Options;
        opt.SetRequestHeader("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36" +
            " (KHTML, like Gecko) Chrome/143.0.0.0 Safari/537.36 Edg/143.0.0.0");
        opt.SetRequestHeader("Accept-Encoding", "gzip, deflate, br, zstd");
        opt.SetRequestHeader("Accept-Language", "en-US,en;q=0.9");
        opt.SetRequestHeader("Pragma", "no-cache");
        opt.SetRequestHeader("Cache-Control", "no-cache");
        opt.SetRequestHeader("Origin", "chrome-extension://jdiccldimpdaibmpdkjnbmckianbfold");
        opt.SetRequestHeader("Cookie", "muid=" + Guid.NewGuid().ToString("N").ToUpperInvariant() + ";");

        var url = $"{WssBase}?TrustedClientToken={TrustedClientToken}" +
                  $"&ConnectionId={ConnectId()}" +
                  $"&Sec-MS-GEC={EdgeTtsDrm.GenerateSecMsGec()}" +
                  $"&Sec-MS-GEC-Version={SecMsGecVersion}";
        await ws.ConnectAsync(new Uri(url), token);
        return ws;
    }

    private static async Task<byte[]> SynthesizeOnSocketAsync(
        ClientWebSocket ws, string text, string voice, string rate, string pitch, CancellationToken token)
    {
        string now = DateToString();

        // 1) speech.config：声明输出格式
        string config =
            $"X-Timestamp:{now}\r\n" +
            "Content-Type:application/json; charset=utf-8\r\n" +
            "Path:speech.config\r\n\r\n" +
            "{\"context\":{\"synthesis\":{\"audio\":{\"metadataoptions\":{" +
            "\"sentenceBoundaryEnabled\":\"false\",\"wordBoundaryEnabled\":\"true\"" +
            "},\"outputFormat\":\"audio-24khz-48kbitrate-mono-mp3\"}}}}\r\n";
        await ws.SendAsync(Encoding.UTF8.GetBytes(config), WebSocketMessageType.Text, true, token);

        // 2) SSML 请求
        string ssml =
            "<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='en-US'>" +
            $"<voice name='{voice}'>" +
            $"<prosody pitch='{pitch}' rate='{rate}' volume='+0%'>{EscapeXml(text)}</prosody>" +
            "</voice></speak>";
        string ssmlMsg =
            $"X-RequestId:{ConnectId()}\r\n" +
            "Content-Type:application/ssml+xml\r\n" +
            $"X-Timestamp:{now}Z\r\n" +  // 末尾的 Z 是 Edge 接口的怪癖，必须保留
            "Path:ssml\r\n\r\n" +
            ssml;
        await ws.SendAsync(Encoding.UTF8.GetBytes(ssmlMsg), WebSocketMessageType.Text, true, token);

        // 3) 收音频帧，直到 turn.end
        var audio = new MemoryStream();
        var buf = new byte[64 * 1024];
        while (ws.State == WebSocketState.Open)
        {
            using var frame = new MemoryStream();
            WebSocketMessageType type;
            while (true)
            {
                var r = await ws.ReceiveAsync(buf, token);
                if (r.MessageType == WebSocketMessageType.Close)
                    throw new IOException("服务端关闭了连接");
                type = r.MessageType;
                frame.Write(buf, 0, r.Count);
                if (r.EndOfMessage) break;
            }

            var data = frame.GetBuffer();
            int len = (int)frame.Length;
            if (type == WebSocketMessageType.Text)
            {
                if (Encoding.UTF8.GetString(data, 0, len).Contains("Path:turn.end"))
                    break;
            }
            else
            {
                if (len < 2) continue;
                int headerLen = (data[0] << 8) | data[1];
                if (headerLen + 2 > len) continue;
                string header = Encoding.ASCII.GetString(data, 2, headerLen);
                if (header.Contains("Path:audio"))
                    audio.Write(data, 2 + headerLen, len - 2 - headerLen);
            }
        }

        if (audio.Length == 0)
            throw new IOException("没有收到音频数据");
        return audio.ToArray();
    }

    private static string ConnectId() => Guid.NewGuid().ToString("N");

    /// <summary>Edge 要求的 JS 风格日期串："Fri Nov 07 2026 22:05:00 GMT+0000 (Coordinated Universal Time)"。</summary>
    private static string DateToString() =>
        DateTime.UtcNow.ToString("ddd MMM dd yyyy HH:mm:ss", CultureInfo.InvariantCulture) +
        " GMT+0000 (Coordinated Universal Time)";

    private static string EscapeXml(string s) => s
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
        .Replace("\"", "&quot;").Replace("'", "&apos;");
}
