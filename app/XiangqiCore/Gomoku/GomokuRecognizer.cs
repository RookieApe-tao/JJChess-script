using SkiaSharp;
using XiangqiCore.Recognition;

namespace XiangqiCore.Gomoku;

/// <summary>五子棋棋盘标定:用户画的屏幕矩形(物理像素)。</summary>
/// <param name="X1">左上 X</param><param name="Y1">左上 Y</param>
/// <param name="X2">右下 X</param><param name="Y2">右下 Y</param>
public readonly record struct GomokuRect(float X1, float Y1, float X2, float Y2)
{
    public float Width => Math.Abs(X2 - X1);
    public float Height => Math.Abs(Y2 - Y1);
    public bool IsValid => Width > 50 && Height > 50;

    public int IntX1 => (int)Math.Min(X1, X2);
    public int IntY1 => (int)Math.Min(Y1, Y2);
    public int IntW => (int)Math.Abs(X2 - X1);
    public int IntH => (int)Math.Abs(Y2 - Y1);
}

/// <summary>
/// 五子棋识别:画框裁剪 → 投影法检测 15×15 网格线(自动纠偏)→ 交叉点圆盘采样判黑白。
/// 不用 ONNX:规则网格 + 黑白两色,传统 CV 足够。
/// 判色用占比而非中位数:白子带阴影/红标记时中位亮度会掉(实测 175),占比稳。
/// </summary>
public sealed class GomokuRecognizer
{
    private const float SampleRadius = 0.35f;  // 采样圆盘半径(格距的倍数)
    private const int MinLinePeakGap = 6;      // 网格线峰的最小像素间距
    private const float DarkFracThreshold = 0.30f;  // 圆盘内暗像素占比 > 此 → 黑子
    private const float LightFracThreshold = 0.15f; // 圆盘内亮像素占比 > 此 → 白子
    private const int LightMargin = 18;        // 亮阈值 = 底色中位 + 此(实机标定)

    private float[]? _gridXs; // 15 条垂直线 x(裁剪坐标系)
    private float[]? _gridYs; // 15 条水平线 y(裁剪坐标系)
    private bool _gridDetectedFlag;
    private int _darkThreshold; // 判色暗阈值(RefineGrid 时自适应标定)

    /// <summary>是否已缓存格点(「新局」时清空,强制重检)。</summary>
    public bool HasGrid => _gridXs is not null;

    public void InvalidateGrid()
    {
        _gridXs = null;
        _gridYs = null;
    }

    /// <summary>
    /// 精化网格:对 rect 裁剪后的图像做投影法网格线检测。
    /// 成功:缓存 15×15 格点;失败(峰不足):回退为矩形 15 等分,返回 false。
    /// </summary>
    public bool RefineGrid(SKBitmap frame, GomokuRect rect)
    {
        // 外扩 4% 容纳边缘线(用户画框只需大致套住棋盘)
        var (x1, y1, w, h) = CropWindow(rect);
        using var crop = ImageOps.Crop(frame, x1, y1, x1 + w, y1 + h);
        var (rgba, cw, ch) = ImageOps.ToRgba(crop);

        // 自适应阈值:木板底色(全图中位亮度) - 45。皮肤再亮/再暗都能跟上。
        _darkThreshold = (int)(MedianLuma(rgba) - 45);

        var xs = DetectGridLines(rgba, cw, ch, vertical: true, _darkThreshold);
        var ys = DetectGridLines(rgba, cw, ch, vertical: false, _darkThreshold);
        if (xs is not null && ys is not null)
        {
            _gridXs = xs;
            _gridYs = ys;
            _gridDetectedFlag = true;
            return true;
        }
        _gridDetectedFlag = false;
        // 回退:15 等分(低置信度,Read 仍可用)
        var fx = new float[GomokuPosition.Size];
        var fy = new float[GomokuPosition.Size];
        var stepX = cw / 14f;
        var stepY = ch / 14f;
        for (var i = 0; i < GomokuPosition.Size; i++) { fx[i] = i * stepX; fy[i] = i * stepY; }
        _gridXs = fx;
        _gridYs = fy;
        return false;
    }

    private static (int X, int Y, int W, int H) CropWindow(GomokuRect rect)
    {
        var pad = 0.04f;
        var x1 = rect.IntX1 - (int)(rect.Width * pad);
        var y1 = rect.IntY1 - (int)(rect.Height * pad);
        var w = rect.IntW + (int)(rect.Width * pad * 2);
        var h = rect.IntH + (int)(rect.Height * pad * 2);
        return (x1, y1, w, h);
    }

    private static int MedianLuma(byte[] rgba)
    {
        var samples = new List<int>(rgba.Length / 4 / 16);
        for (var i = 0; i < rgba.Length; i += 64) // 每 16 像素采 1 个
            samples.Add((rgba[i] * 299 + rgba[i + 1] * 587 + rgba[i + 2] * 114) / 1000);
        samples.Sort();
        return samples[samples.Count / 2];
    }

    /// <summary>
    /// 投影法:统计每列(或每行)的"暗像素个数"(亮度 < threshold),
    /// 网格线整条穿过 → 计数极高;找 15 个等间距峰。返回 null 表示检测失败。
    /// 棋子也暗但只占局部,计数远低于整条线;棋子遮挡使部分线计数下降,
    /// 由"滑动窗口选等间距方差最小的 15 连峰"兜底。
    /// </summary>
    private static float[]? DetectGridLines(byte[] rgba, int w, int h, bool vertical, int threshold)
    {
        var len = vertical ? w : h;    // 峰分布轴
        var other = vertical ? h : w;  // 求和轴
        if (len < GomokuPosition.Size * 4) return null; // 太小,格距 <4px 不可信

        var dark = new int[len];
        var step = other > 1200 ? 2 : 1; // 求和轴下采样(大图省时)
        for (var i = 0; i < len; i++)
        {
            var cnt = 0;
            for (var j = 0; j < other; j += step)
            {
                var idx = vertical ? (j * w + i) : (i * w + j);
                var p = idx * 4;
                if ((rgba[p] * 299 + rgba[p + 1] * 587 + rgba[p + 2] * 114) / 1000 < threshold)
                    cnt++;
            }
            dark[i] = cnt * step; // 归一到全轴计数
        }

        var expectedStep = len / 14f;
        var minGap = Math.Max(MinLinePeakGap, (int)(expectedStep * 0.6f));
        // 线的计数阈值:至少穿过 1/3 的求和轴(线被棋子遮挡后仍应剩 1/3+)
        var lineCount = other / 3;

        // 找局部极大(计数高谷)
        var peaks = new List<(int Pos, int Count)>();
        for (var i = 1; i < len - 1; i++)
        {
            if (dark[i] < lineCount) continue;
            if (dark[i] >= dark[i - 1] && dark[i] >= dark[i + 1] && IsPeakAboveNeighbors(dark, i, minGap))
                peaks.Add((i, dark[i]));
        }
        // 先去重同一条线内的相邻局部极大(线宽 2-4px 会产生 2-3 个相邻峰),保留最高者
        var dedup = new List<(int Pos, int Count)>();
        foreach (var p in peaks)
        {
            if (dedup.Count > 0 && p.Pos - dedup[^1].Pos <= 4)
            {
                if (p.Count > dedup[^1].Count) dedup[^1] = p;
            }
            else dedup.Add(p);
        }
        // 分簇(间距 < minGap 归同一簇),每簇保留计数前 2 的峰:
        // 棋盘外框粗线与第一条内线只差半格(< minGap),只留最高会把内线吞掉,
        // 导致滑动窗口只能选出"外框+13内线+边缘UI"的假窗口。留 2 个让等间距筛选自己裁决;
        // 但簇内两峰至少隔 minGap/3,防止同一条线的细分成因残留。
        var clusters = new List<List<(int Pos, int Count)>>();
        foreach (var p in dedup)
        {
            if (clusters.Count > 0 && p.Pos - clusters[^1][^1].Pos < minGap)
                clusters[^1].Add(p);
            else
                clusters.Add(new List<(int, int)> { p });
        }
        var minInnerGap = Math.Max(5, minGap / 3);
        var merged = new List<(int Pos, int Count)>();
        foreach (var cl in clusters)
        {
            var top2 = cl.OrderByDescending(x => x.Count).ToList();
            merged.Add(top2[0]);
            if (top2.Count > 1 && top2[1].Pos - top2[0].Pos >= minInnerGap)
                merged.Add(top2[1]);
        }
        merged.Sort((a, b) => a.Pos.CompareTo(b.Pos));
        if (merged.Count < GomokuPosition.Size) return null;

        // 滑动窗口选"相邻间距方差最小"的 15 连峰(自动排除棋盘外框/深色 UI 边缘)
        float[]? best = null;
        double bestVar = double.MaxValue;
        for (var start = 0; start + GomokuPosition.Size - 1 < merged.Count; start++)
        {
            var window = merged.GetRange(start, GomokuPosition.Size);
            double mean = (window[^1].Pos - window[0].Pos) / 14.0;
            double var = 0;
            for (var i = 1; i < window.Count; i++)
            {
                var d = (window[i].Pos - window[i - 1].Pos) - mean;
                var += d * d;
            }
            if (var < bestVar) { bestVar = var; best = window.Select(p => (float)p.Pos).ToArray(); }
        }
        if (best is null) return null;
        // 方差过大(间距不均匀超过步长 30%)判为误检
        var stepMean = (best[^1] - best[0]) / 14f;
        double variance = bestVar / 14;
        if (variance > 0 && Math.Sqrt(variance) > stepMean * 0.30) return null;
        return best;
    }

    private static bool IsPeakAboveNeighbors(int[] dark, int i, int radius)
    {
        var r = Math.Min(radius, 4);
        for (var k = 1; k <= r; k++)
        {
            if (i - k >= 0 && dark[i - k] > dark[i]) return false;
            if (i + k < dark.Length && dark[i + k] > dark[i]) return false;
        }
        return true;
    }

    /// <summary>全量读取盘面。需要先 RefineGrid(或上次已缓存)。</summary>
    public GomokuReadResult Read(SKBitmap frame, GomokuRect rect)
    {
        var result = new GomokuReadResult();
        if (!HasGrid)
            result.GridDetected = RefineGrid(frame, rect);

        var (x1, y1, w, h) = CropWindow(rect);
        using var crop = ImageOps.Crop(frame, x1, y1, x1 + w, y1 + h);
        var (rgba, cw, ch) = ImageOps.ToRgba(crop);

        var pos = new GomokuPosition();
        var step = (_gridXs![1] - _gridXs[0] + (_gridYs![1] - _gridYs[0])) / 2f;
        var radius = Math.Max(2f, step * SampleRadius);
        var darkThr = _darkThreshold;                   // 判黑:亮度 < 暗线阈值(中位-45)
        var lightThr = MedianLuma(rgba) + LightMargin;  // 判白:明显亮于底色
        (int Row, int Col, int Redness)? lastHint = null;

        for (var r = 0; r < GomokuPosition.Size; r++)
        {
            for (var c = 0; c < GomokuPosition.Size; c++)
            {
                var cx = (int)Math.Round(_gridXs[c]);
                var cy = (int)Math.Round(_gridYs[r]);
                var (stone, brightness, redness) = SampleIntersection(rgba, cw, ch, cx, cy, radius, darkThr, lightThr);
                result.Brightness[r, c] = brightness;
                if (stone is not null) pos[r, c] = stone;
                if (redness > 60 && stone is not null &&
                    (lastHint is null || redness > lastHint.Value.Redness))
                    lastHint = (r, c, redness);
            }
        }

        result.Position = pos;
        result.GridDetected = _gridDetectedFlag && HasGrid;
        result.LastMoveHint = lastHint is null ? null : (lastHint.Value.Row, lastHint.Value.Col);
        var diff = Math.Abs(pos.BlackCount - pos.WhiteCount);
        if (diff > 1)
            result.Warning = $"黑白子数异常(黑{pos.BlackCount}/白{pos.WhiteCount}),建议重画框或重新识别";
        return result;
    }

    /// <summary>圆盘采样一个交叉点:
    /// 暗像素占比 > 30% → 黑;亮像素占比 > 30% → 白;否则空。
    /// 返回(棋子色/空、亮度中位数、红度 95 分位)。</summary>
    private static (GomokuStone? Stone, int Brightness, int Redness) SampleIntersection(
        byte[] rgba, int w, int h, int cx, int cy, float radius, int darkThr, int lightThr)
    {
        var brightList = new List<int>(256);
        var redList = new List<int>(256);
        var total = 0;
        var dark = 0;
        var light = 0;
        var ri = (int)Math.Round(radius);
        for (var dy = -ri; dy <= ri; dy++)
        {
            for (var dx = -ri; dx <= ri; dx++)
            {
                if (dx * dx + dy * dy > ri * ri) continue; // 圆盘
                var x = cx + dx; var y = cy + dy;
                if (x < 0 || x >= w || y < 0 || y >= h) continue;
                var p = (y * w + x) * 4;
                var r = rgba[p]; var g = rgba[p + 1]; var b = rgba[p + 2];
                var luma = (r * 299 + g * 587 + b * 114) / 1000;
                brightList.Add(luma);
                redList.Add(r - Math.Max(g, b));
                total++;
                if (luma < darkThr) dark++;
                else if (luma > lightThr) light++;
            }
        }
        if (total == 0) return (null, -1, 0);
        brightList.Sort();
        redList.Sort();

        var median = brightList[brightList.Count / 2];
        var rank = (redList.Count - 1) * 0.95;
        var lo = (int)Math.Floor(rank);
        var hi = Math.Min(redList.Count - 1, lo + 1);
        var redness = redList[lo] + (redList[hi] - redList[lo]) * (float)(rank - lo);

        GomokuStone? stone = (float)dark / total > DarkFracThreshold ? GomokuStone.Black
            : (float)light / total > LightFracThreshold ? GomokuStone.White
            : null;
        return (stone, median, (int)redness);
    }

    /// <summary>缓存格点映射回屏幕坐标(画绿圈用):返回 15×15 屏幕像素坐标。</summary>
    public (float X, float Y) ScreenPoint(int row, int col, GomokuRect rect)
    {
        var (ox, oy, _, _) = CropWindow(rect);
        return (_gridXs![col] + ox, _gridYs![row] + oy);
    }

    /// <summary>诊断:最近一次网格格点与格距。</summary>
    public (float[] Xs, float[] Ys, float Step)? GridInfo =>
        _gridXs is null || _gridYs is null
            ? null
            : (_gridXs, _gridYs, (_gridXs[1] - _gridXs[0] + _gridYs[1] - _gridYs[0]) / 2f);
}
