using System.Text;

namespace ReadNow.Services;

/// <summary>
/// Edge 在线语音接口的 Sec-MS-GEC v2 令牌生成器。
/// 移植自 edge-tts 的 drm.py：CS64 哈希 + 定制 SHA-256，格式 "&lt;16 hex&gt;-&lt;64 hex&gt;"。
/// 已用 Python 版固定时间戳的测试向量逐字节校验过。
/// </summary>
internal static class EdgeTtsDrm
{
    private const string TrustedClientToken = "6A5AA1D4EAFF4E9FB37E23D68491D6F4";
    private const long WinEpoch = 11644473600L;

    // 标准 SHA-256 K 表
    private static readonly uint[] K =
    {
        0x428A2F98,0x71374491,0xB5C0FBCF,0xE9B5DBA5,0x3956C25B,0x59F111F1,0x923F82A4,0xAB1C5ED5,
        0xD807AA98,0x12835B01,0x243185BE,0x550C7DC3,0x72BE5D74,0x80DEB1FE,0x9BDC06A7,0xC19BF174,
        0xE49B69C1,0xEFBE4786,0x0FC19DC6,0x240CA1CC,0x2DE92C6F,0x4A7484AA,0x5CB0A9DC,0x76F988DA,
        0x983E5152,0xA831C66D,0xB00327C8,0xBF597FC7,0xC6E00BF3,0xD5A79147,0x06CA6351,0x14292967,
        0x27B70A85,0x2E1B2138,0x4D2C6DFC,0x53380D13,0x650A7354,0x766A0ABB,0x81C2C92E,0x92722C85,
        0xA2BFE8A1,0xA81A664B,0xC24B8B70,0xC76C51A3,0xD192E819,0xD6990624,0xF40E3585,0x106AA070,
        0x19A4C116,0x1E376C08,0x2748774C,0x34B0BCB5,0x391C0CB3,0x4ED8AA4A,0x5B9CCA4F,0x682E6FF3,
        0x748F82EE,0x78A5636F,0x84C87814,0x8CC70208,0x90BEFFFA,0xA4506CEB,0xBEF9A3F7,0xC67178F2,
    };
    // 首个消息块用的 K 表：第 14 项被替换（混淆点）
    private static readonly uint[] KBlock;
    private static readonly uint[] Sha256Initial =
        { 0x6A09E667,0xBB67AE85,0x3C6EF372,0xA54FF53A,0x510E527F,0x9B05688C,0x1F83D9AB,0x5BE0CD19 };
    private static readonly uint[] Sha256State =
        { 0xFD9FBEA2,0x91243CE7,0xBE499094,0xCD2E1E0A,0x6C006F49,0x04BD35AB,0xFEB0A7E4,0x03EFFBD2 };
    private static readonly uint[] Sha256Schedule =
        { 0xEB24BF9E,0x1558691F,0xA0C09EE2,0x0CFA32BA,0xA622EE8C,0xD1DD9491,0xC0DD25A0,
          0xF9E70AB6,0xF0C31573,0x7058CE4E,0x7188E606,0x00E5AB33,0xD258B060,0x72766564 };
    private const uint Sha256Adjustment = 0xC9DC06A7;
    private const uint Sigma0Adjustment = 0x059C0B80;
    private const uint Sigma1Adjustment = 0x000B92C0;

    private const uint Cs64Factor = 0x82893933;
    private const uint Cs64Adjustment0 = 0xF2F90B1A;
    private const uint Cs64Adjustment1 = 0x0B1A0000;
    private static readonly (uint Coef, uint Offset)[] Cs64Coefficients =
    {
        (0xE82E91B1,0xB32700D0),(0x44DE24C6,0x88DFF51D),(0x3BEB65BC,0x29514038),(0xC89C5933,0xB328CBDF),
        (0x8D1EC211,0xD405B4D5),(0x314EF5D5,0x26E55F3C),(0xE92491B1,0x607801C6),(0xF0A5B9D5,0x82B2C1FB),
        (0x06064CF3,0xFA51A3DA),(0xDA79FA60,0x207CC464),(0xBED20560,0x3C3C74F3),(0x89F9C9F8,0xD2D14B61),
        (0x35FA38C6,0xBB04A2C1),(0xCB49E3C6,0xC5626167),
    };

    static EdgeTtsDrm()
    {
        KBlock = (uint[])K.Clone();
        KBlock[14] = Sha256Adjustment;
    }

    /// <summary>生成当前时间的 Sec-MS-GEC v2 令牌。</summary>
    public static string GenerateSecMsGec() =>
        GenerateSecMsGec(DateTimeOffset.UtcNow.ToUnixTimeSeconds());

    /// <summary>供测试：用固定 unix 时间戳生成令牌。</summary>
    public static string GenerateSecMsGec(long unixTimestamp)
    {
        long ticks = unixTimestamp + WinEpoch;
        ticks -= ticks % 300;          // 5 分钟取整
        ticks *= 10_000_000L;          // → 100ns
        var data = Encoding.ASCII.GetBytes($"{ticks}{TrustedClientToken}");

        var digest = CustomSha256Digest(data);
        var (w0, w1) = Cs64Digest(data, digest);

        // cs64 = 两个 uint 小端打包后的 hex
        var cs64Bytes = new byte[8];
        BitConverter.GetBytes(w0).CopyTo(cs64Bytes, 0);
        BitConverter.GetBytes(w1).CopyTo(cs64Bytes, 4);
        return Convert.ToHexString(cs64Bytes) + "-" + Convert.ToHexString(digest);
    }

    // ---------------- 定制 SHA-256 ----------------

    private static uint Rol16(uint v) => (v << 16) | (v >> 16);
    private static uint Sigma0(uint v) => (v >> 7) ^ (v << 25) ^ (v >> 18) ^ (v << 14) ^ (v >> 3);
    private static uint Sigma1(uint v) => (v >> 17) ^ (v << 15) ^ (v >> 19) ^ (v << 13) ^ (v >> 10);
    private static uint Bsig0(uint v) => ((v >> 2) | (v << 30)) ^ ((v >> 13) | (v << 19)) ^ ((v >> 22) | (v << 10));
    private static uint Bsig1(uint v) => ((v >> 6) | (v << 26)) ^ ((v >> 11) | (v << 21)) ^ ((v >> 25) | (v << 7));

    private static void ExpandSchedule(uint[] words, int firstWord)
    {
        for (int i = firstWord; i < 64; i++)
        {
            uint x = words[i - 2], y = words[i - 15];
            uint sigma1 = Sigma1(x), sigma0 = Sigma0(y);
            words[i] = sigma1 + words[i - 7] + sigma0 + words[i - 16];
        }
    }

    private static uint[] Rounds(uint[] state, uint[] words, int firstRound)
    {
        var constants = firstRound == 14 ? KBlock : K;
        uint a = state[0], b = state[1], c = state[2], d = state[3],
             e = state[4], f = state[5], g = state[6], h = state[7];
        for (int i = firstRound; i < 64; i++)
        {
            uint t1 = h + Bsig1(e) + (g ^ (e & (f ^ g))) + constants[i] + words[i];
            uint t2 = Bsig0(a) + ((a & b) | (c & (a | b)));
            h = g; g = f; f = e; e = d + t1; d = c; c = b; b = a; a = t1 + t2;
        }
        return new[] { a, b, c, d, e, f, g, h };
    }

    /// <summary>由消息前 7 字节算出"初始状态"（定制 IV）。</summary>
    private static uint[] Sha256Init(byte[] data7)
    {
        var words = new uint[64];
        words[14] = (uint)((data7[0] << 16) | (data7[1] << 8) | data7[2]);
        words[15] = (uint)((data7[3] << 24) | (data7[4] << 16) | (data7[5] << 8) | data7[6]);

        for (int i = 16; i < 30; i++)
        {
            uint value = Sha256Schedule[i - 16];
            // i >= 16 恒成立
            value += i == 16 ? Sigma1(words[i - 2]) ^ Sigma1Adjustment : Sigma1(words[i - 2]);
            if (i >= 21) value += words[i - 7];
            if (i >= 29) value += i == 29 ? Sigma0(words[14]) ^ Sigma0Adjustment : Sigma0(words[i - 15]);
            words[i] = value;
        }
        words[30] = Sigma1(words[28]) + words[23] + Sigma0(words[15]) + words[14]
                    + Sha256Adjustment - K[14];
        ExpandSchedule(words, 31);

        var working = Rounds(Sha256State, words, 14);
        var result = new uint[8];
        for (int i = 0; i < 8; i++) result[i] = Sha256Initial[i] + working[i];
        return result;
    }

    private static byte[] CustomSha256Digest(byte[] data)
    {
        if (data.Length < 7) throw new ArgumentException("Token salt must contain at least seven bytes");

        long length = 57 + data.Length;
        int padZeros = (int)((55 - length) % 64 + 64) % 64;
        int paddedLen = (data.Length - 7) + 1 + padZeros + 8;
        var padded = new byte[paddedLen];
        Array.Copy(data, 7, padded, 0, data.Length - 7);
        padded[data.Length - 7] = 0x80;
        ulong bitLen = (ulong)length * 8;
        for (int i = 0; i < 8; i++)
            padded[paddedLen - 1 - i] = (byte)(bitLen >> (8 * i));

        var state = Sha256Init(data[..7]);
        var words = new uint[64];
        for (int offset = 0; offset < paddedLen; offset += 64)
        {
            Array.Clear(words, 0, 64);
            for (int i = 0; i < 16; i++)
                words[i] = (uint)((padded[offset + i * 4] << 24) | (padded[offset + i * 4 + 1] << 16)
                                | (padded[offset + i * 4 + 2] << 8) | padded[offset + i * 4 + 3]);
            ExpandSchedule(words, 16);
            var working = Rounds(state, words, 0);
            for (int i = 0; i < 8; i++) state[i] += working[i];
        }

        var digest = new byte[32];
        for (int i = 0; i < 8; i++)
        {
            digest[i * 4] = (byte)(state[i] >> 24);
            digest[i * 4 + 1] = (byte)(state[i] >> 16);
            digest[i * 4 + 2] = (byte)(state[i] >> 8);
            digest[i * 4 + 3] = (byte)state[i];
        }
        return digest;
    }

    // ---------------- CS64 ----------------

    private static (uint, uint) Cs64Digest(byte[] data, byte[] digest)
    {
        uint keyA = BitConverter.ToUInt32(digest, 0) | 1;
        uint keyB = BitConverter.ToUInt32(digest, 4) | 1;
        var factors = new[] { keyA * Cs64Factor, keyB * Cs64Factor };

        var terms = new List<uint>();
        var offsets = new List<uint>();
        for (int i = 0; i < Cs64Coefficients.Length; i++)
        {
            terms.Add(Cs64Coefficients[i].Coef * factors[i % 2]);
            offsets.Add(Cs64Coefficients[i].Offset);
        }

        foreach (var (index, word) in WordsLe4Even(data))
        {
            uint term = word * (index % 2 == 0 ? keyA : keyB);
            uint offset = Rol16(word) * (index % 2 == 0 ? 0xEF0569FB : 0xC31713DB);
            if (index == 0)
            {
                term += Cs64Adjustment0 * factors[0];
                offset += Cs64Adjustment1;
            }
            terms.Add(term);
            offsets.Add(offset);
        }

        uint swapAcc = 0, swapLast = 0, state = 0, acc = 0;
        for (int i = 0; i + 1 < terms.Count; i += 2)
        {
            uint x = terms[i], y = terms[i + 1];
            uint t = Rol16(offsets[i] + x);
            uint q = t * 0x76F50F34;
            swapAcc += Rol16(q) * 0xC3EFEA97 + q;
            t = Rol16(offsets[i + 1] + y);
            q = t * 0x1881CE3C;
            swapLast = Rol16(q) * 0x35BD1EC9 + q;
            swapAcc += swapLast;

            state = state * keyA + x;
            state = Rol16(state) * 0xCF98B111;
            state = Rol16(state) * 0x87085B9F;
            state = Rol16(state) * 0x12CEB96D;
            state = Rol16(state) * 0x257E1D83;
            acc += state;
            t = state * keyB + y;
            t = Rol16(t) * 0xA27416F5;
            t = Rol16(t) * 0xD38396FF;
            state = Rol16(Rol16(t) * 0x7C932B89) * 0xBFA49F69;
            acc += state;
        }
        return (swapLast ^ state, swapAcc ^ acc);
    }

    /// <summary>b"\x00"+data 按小端 u32 切词，补零到 4 对齐且偶数个。</summary>
    private static IEnumerable<(int index, uint word)> WordsLe4Even(byte[] data)
    {
        int len = data.Length + 1; // 前缀一个 0x00
        int wordCount = (len + 3) / 4;
        if (wordCount % 2 == 1) wordCount++;
        for (int i = 0; i < wordCount; i++)
        {
            uint w = 0;
            for (int j = 0; j < 4; j++)
            {
                int pos = i * 4 + j; // pos==0 是前缀 0x00
                byte b = (pos == 0 || pos > data.Length) ? (byte)0 : data[pos - 1];
                w |= (uint)b << (8 * j);
            }
            yield return (i, w);
        }
    }
}
