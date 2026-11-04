namespace Agent.Common;

/// <summary>
/// GF(256) 系统 Reed-Solomon 纠删码（2026-10-14，对标 UU dckcp 的 RS-FEC——
/// uu-reverse/docs/transport/kcp.md：KCP 数据通道层内 FEC，冗余随实测丢包率调整）。
///
/// 生成矩阵 G = [I | C]，C 是 k×r 的 **Cauchy 矩阵**（c[i][j] = 1/(x_i ⊕ y_j)，
/// x_i=i、y_j=MaxGroupSize+j，取值域不相交保证分母非零）。Cauchy 矩阵任意方阵可逆，
/// 因此 [I|C] 是 MDS 码：**任意 t 个存活分片（t=数据片数）可恢复全部数据**，
/// 单组最多容忍 r 片丢失、零重传零延迟——这是相对旧 XOR 校验片（每 8 片只能救 1 片）的本质提升。
///
/// 线性代数部分只有乘法和高斯消元，全部 GF(256) 查表（log/exp），无 SIMD 依赖
/// （UU 用 AVX2 加速，我们按 1080p30 的量级用标量即可：165 片/r=4 一帧约 1.4M 次乘法 ~3ms）。
/// </summary>
public static unsafe class RsFec
{
    /// <summary>每组数据片数上限（k）</summary>
    public const int MaxGroupSize = 16;
    /// <summary>校验片数上限（r）</summary>
    public const int MaxParity = 6;

    // ---------------- GF(256) 查表（生成多项式 0x11D，与常见 RS 实现一致） ----------------

    private static readonly byte[] Exp = new byte[512];
    private static readonly byte[] Log = new byte[256];

    static RsFec()
    {
        int x = 1;
        for (int i = 0; i < 255; i++)
        {
            Exp[i] = (byte)x;
            Log[x] = (byte)i;
            x <<= 1;
            if ((x & 0x100) != 0) x ^= 0x11D;
        }
        for (int i = 255; i < 512; i++) Exp[i] = Exp[i - 255];
    }

    private static byte GfMul(byte a, byte b)
    {
        if (a == 0 || b == 0) return 0;
        return Exp[Log[a] + Log[b]];
    }

    private static byte GfInv(byte a) => Exp[255 - Log[a]];

    /// <summary>Cauchy 系数 1/(i ⊕ (MaxGroupSize + j)) —— 组内任意 t 时的子矩阵仍具 Cauchy 可逆性</summary>
    private static byte Cauchy(int i, int j) => GfInv((byte)(i ^ (MaxGroupSize + j)));

    // ---------------- 编码 ----------------

    /// <summary>
    /// 生成 r 个校验分片。data 是 t 个**等长**分片（t ≤ MaxGroupSize，r ≤ MaxParity）。
    /// parity[j][b] = Σ_i data[i][b] * Cauchy(i, j)。
    /// 返回长度 r 的数组，每个与输入分片等长。
    /// </summary>
    public static byte[][] Encode(byte[][] data, int r)
    {
        int t = data.Length;
        if (t < 1 || t > MaxGroupSize) throw new ArgumentOutOfRangeException(nameof(data));
        if (r < 1 || r > MaxParity) throw new ArgumentOutOfRangeException(nameof(r));
        int len = data[0].Length;
        var parity = new byte[r][];
        var coeff = new byte[t];   // 每个数据片对当前校验片的系数
        for (int j = 0; j < r; j++)
        {
            for (int i = 0; i < t; i++) coeff[i] = Cauchy(i, j);
            var p = new byte[len];
            for (int i = 0; i < t; i++)
            {
                byte c = coeff[i];
                if (c == 0) continue;
                fixed (byte* pp = p, dd = data[i])
                {
                    for (int b = 0; b < len; b++)
                        pp[b] ^= GfMul(dd[b], c);
                }
            }
            parity[j] = p;
        }
        return parity;
    }

    // ---------------- 解码 ----------------

    /// <summary>
    /// 尝试恢复丢失的数据分片。shards 长 t+r：[0..t)=数据片、[t..t+r)=校验片，丢失位传 null。
    /// 存活分片数 ≥ t 即可恢复（Cauchy MDS 性质）。recovered 只填丢失的数据片位，已存在的不动。
    /// 返回 false = 存活不足，恢复失败。
    /// </summary>
    public static bool TryDecode(byte[]?[] shards, int r, byte[][] recovered)
    {
        int t = shards.Length - r;
        if (t < 1 || t > MaxGroupSize || r < 0 || r > MaxParity) throw new ArgumentOutOfRangeException(nameof(shards));
        if (r == 0) return false;

        // 需要恢复的数据片
        var missing = new List<int>();
        for (int i = 0; i < t; i++)
            if (shards[i] == null) missing.Add(i);
        if (missing.Count == 0) return true;
        if (missing.Count > r) return false;   // 丢失超冗余能力

        // 挑 t 个存活分片（数据片优先、不足补校验片；Cauchy 保证任意 t 列可逆）
        var rows = new List<int>();
        for (int i = 0; i < t && rows.Count < t; i++)
            if (shards[i] != null) rows.Add(i);
        for (int j = t; j < t + r && rows.Count < t; j++)
            if (shards[j] != null) rows.Add(j);
        if (rows.Count < t) return false;

        int len = shards[rows[0]]!.Length;

        // A[m][i] = 生成矩阵第 rows[m] 列、第 i 行（G=[I|C]^T 视角：列 m 的第 i 个分量）
        //   rows[m] < t  → 单位列 e_{rows[m]}
        //   rows[m] ≥ t  → Cauchy 列（i, rows[m]-t）
        var a = new byte[t][];
        for (int m = 0; m < t; m++)
        {
            a[m] = new byte[t];
            if (rows[m] < t) a[m][rows[m]] = 1;
            else for (int i = 0; i < t; i++) a[m][i] = Cauchy(i, rows[m] - t);
        }
        // 高斯消元把 A 化为单位阵，同步对 y（分片字节列向量）做行变换
        var y = new byte[t][];
        for (int m = 0; m < t; m++)
        {
            var src = shards[rows[m]]!;
            y[m] = new byte[len];
            Array.Copy(src, y[m], len);
        }

        for (int col = 0; col < t; col++)
        {
            // 找主元
            int piv = -1;
            for (int m = col; m < t; m++)
                if (a[m][col] != 0) { piv = m; break; }
            if (piv < 0) return false;   // 理论上不会发生（Cauchy 可逆）
            if (piv != col)
            {
                (a[col], a[piv]) = (a[piv], a[col]);
                (y[col], y[piv]) = (y[piv], y[col]);
            }
            byte inv = GfInv(a[col][col]);
            if (inv != 1)
            {
                for (int c2 = 0; c2 < t; c2++) a[col][c2] = GfMul(a[col][c2], inv);
                for (int b = 0; b < len; b++) y[col][b] = GfMul(y[col][b], inv);
            }
            for (int m = 0; m < t; m++)
            {
                if (m == col || a[m][col] == 0) continue;
                byte f = a[m][col];
                for (int c2 = 0; c2 < t; c2++) a[m][c2] ^= GfMul(f, a[col][c2]);
                for (int b = 0; b < len; b++) y[m][b] ^= GfMul(f, y[col][b]);
            }
        }
        // 此时 y[i] = 原始数据片 i
        foreach (int idx in missing)
        {
            recovered[idx] ??= y[idx];
        }
        return true;
    }

    /// <summary>
    /// 自适应冗余度（对标 UU DcKcpFecGenerator::UpdateNetWorkInfo(loss_ratio)）：
    /// 丢包率 → 每组校验片数 r。lossFrac 取"NACK 重传占比"与"ping 丢包率"的较大者。
    /// 丢包 0~2% → r=1（6% 冗余兜底单丢）；5% → r=2；10% → r=3；22% → r=6 封顶。
    /// </summary>
    public static int ParityForLoss(double lossFrac)
    {
        if (lossFrac <= 0) return 1;
        int r = 1 + (int)Math.Round(lossFrac * MaxGroupSize * 1.5, MidpointRounding.AwayFromZero);
        return Math.Min(MaxParity, Math.Max(1, r));
    }
}
