using SkiaSharp;
using XiangqiCore.Recognition;

namespace XiangqiCore;

/// <summary>一个候选解释：1 或 2 步合法走子序列，及其到达的局面。</summary>
public sealed class MoveCandidate
{
    public List<Move> Moves { get; } = new();           // 1 或 2 步
    public string ResultingFen { get; set; } = "";      // 走完该序列后的 FEN（标准朝向）
    public List<string> MovesChinese { get; } = new();  // 每步中文记谱
    public bool HasCapture { get; set; }
}

/// <summary>跟踪结果。</summary>
public sealed class TrackerMatch
{
    public enum Kind { NoChange, LostTrack, Matched }
    public Kind Result { get; set; } = Kind.LostTrack;
    public string? Reason { get; set; }                // LostTrack 时的中文原因
    public List<MoveCandidate> Candidates { get; } = new();
    public MoveCandidate? Top => Candidates.Count > 0 ? Candidates[0] : null;
}

/// <summary>
/// 增量走子跟踪：把「两帧之间的占位变化」用象棋合法走法解释为 1 或 2 步。
/// 只用检测器拿占位（不分类棋子类型），棋子身份来自已知 FEN。
/// </summary>
public sealed class MoveTracker
{
    /// <summary>吃子判定的中心像素 diff 阈值（每通道平均 |Δ|）。同子不动 < ~6，换子 > ~50。</summary>
    public float InkThreshold { get; set; } = 28f;

    private readonly BoardRecognizer _recognizer;
    public MoveTracker(BoardRecognizer recognizer) => _recognizer = recognizer;

    public Occupancy BuildOccupancy(SKBitmap img, Calibration cal, bool reverseRows, int? pinnedColOffset, int? pinnedRowOffset = null) =>
        _recognizer.BuildOccupancy(img, cal, reverseRows, pinnedColOffset, pinnedRowOffset);

    /// <summary>仅运行 YOLO 检测器，返回原始检测框（未过滤未分类），用于标定等诊断。</summary>
    public List<Detection> DetectPieces(SKBitmap img, float minConf = 0.5f) =>
        _recognizer.RunDetector(img).Where(b => b.Conf >= minConf).ToList();

    /// <summary>主入口。knownFen 为上一确认局面（标准朝向）。</summary>
    public TrackerMatch Track(string knownFen, Occupancy prevOcc, Occupancy curOcc,
        SKBitmap prevFrame, SKBitmap curFrame, Calibration cal, bool reverseRows)
    {
        var match = new TrackerMatch();
        var (board0, side0) = Notation.ParseFen(knownFen);

        var sources = new List<(int R, int C)>();   // prev 有、cur 空
        var newDests = new List<(int R, int C)>();  // prev 空、cur 有
        for (var r = 0; r < 10; r++)
            for (var c = 0; c < 9; c++)
            {
                var p = prevOcc.Filled[r, c];
                var n = curOcc.Filled[r, c];
                if (p && !n) sources.Add((r, c));
                else if (!p && n) newDests.Add((r, c));
            }

        var capDests = ChangedInkCells(prevFrame, curFrame, cal, reverseRows, prevOcc, curOcc, prevOcc.ColOffset, prevOcc.RowOffset);
        var allDestCount = newDests.Count + capDests.Count;

        if (sources.Count == 0 && allDestCount == 0)
        {
            match.Result = TrackerMatch.Kind.NoChange;
            return match;
        }
        if (sources.Count > 2 || allDestCount > 2)
        {
            match.Result = TrackerMatch.Kind.LostTrack;
            match.Reason = "距上次『分析/新局』已超过 2 步，无法切分中间走法。请更频繁地点『分析』，或重新点『新局』。";
            return match;
        }

        var obs = new Observed(prevOcc.Filled, curOcc.Filled, capDests);
        var cands = new List<MoveCandidate>();
        var moverRed = side0 == "w";

        // 1-ply
        foreach (var m in XiangqiRules.LegalMoves(board0, moverRed))
        {
            var b1 = XiangqiRules.CloneAndApply(board0, m);
            if (OccupancyDiffMatches(b1, board0, obs))
                cands.Add(MakeCandidate(new[] { m }, knownFen, board0));
        }
        // 2-ply（我方一手 + 对方应招）。即便 1-ply 已命中也计算，供「修正」切换（同形异步、仅轮到方不同）
        foreach (var m1 in XiangqiRules.LegalMoves(board0, moverRed))
        {
            var b1 = XiangqiRules.CloneAndApply(board0, m1);
            foreach (var m2 in XiangqiRules.LegalMoves(b1, !moverRed))
            {
                var b2 = XiangqiRules.CloneAndApply(b1, m2);
                if (OccupancyDiffMatches(b2, board0, obs))
                    cands.Add(MakeCandidate(new[] { m1, m2 }, knownFen, board0));
            }
        }

        if (cands.Count == 0)
        {
            match.Result = TrackerMatch.Kind.LostTrack;
            match.Reason = "无法把画面变化解释为合法的 1–2 步走子（可能是识别抖动或棋子仍在移动）。";
            return match;
        }

        // 排序：步数少 → 非吃子优先（吃子更罕见，歧义时倾向非吃子解释）
        match.Candidates.Clear();
        match.Candidates.AddRange(cands
            .OrderBy(c => c.Moves.Count)
            .ThenBy(c => c.HasCapture ? 1 : 0));
        match.Result = TrackerMatch.Kind.Matched;
        return match;
    }

    private static MoveCandidate MakeCandidate(Move[] moves, string startFen, char?[,] board0)
    {
        var cand = new MoveCandidate();
        var fen = startFen;
        var b = (char?[,])board0.Clone();
        foreach (var m in moves)
        {
            if (!cand.HasCapture && b[m.ToRow, m.ToCol] is not null) cand.HasCapture = true;
            cand.Moves.Add(m);
            cand.MovesChinese.Add(Notation.MoveToChinese(fen, m.ToUci()));
            b = XiangqiRules.CloneAndApply(b, m);
            fen = Notation.ApplyMove(fen, m.ToUci());
        }
        cand.ResultingFen = fen;
        return cand;
    }

    /// <summary>bAfter 相对 bBefore 的占位变化是否与观测一致。</summary>
    private static bool OccupancyDiffMatches(char?[,] bAfter, char?[,] bBefore, Observed obs)
    {
        for (var r = 0; r < 10; r++)
            for (var c = 0; c < 9; c++)
            {
                var was = bBefore[r, c] is not null;
                var now = bAfter[r, c] is not null;
                var oPrev = obs.Prev[r, c];
                var oCur = obs.Cur[r, c];
                if ((was != now) != (oPrev != oCur)) return false;       // 占位转变须一致
                if (was && now && oPrev && oCur)                          // 两帧都有子：换子标志须一致
                {
                    var occupantChanged = bBefore[r, c] != bAfter[r, c];
                    if (occupantChanged != obs.Cap.Contains((r, c))) return false;
                }
            }
        return true;
    }

    /// <summary>对”两帧都占”的格做中心 50% 像素 diff，超过阈值视为换子（吃子落点）。</summary>
    private HashSet<(int R, int C)> ChangedInkCells(SKBitmap prev, SKBitmap cur, Calibration cal,
        bool reverseRows, Occupancy prevOcc, Occupancy curOcc, int colOffset, int rowOffset)
    {
        var result = new HashSet<(int, int)>();
        var minX = (double)cal.Xs.Min(); var maxX = (double)cal.Xs.Max();
        var minY = (double)cal.Ys.Min(); var maxY = (double)cal.Ys.Max();
        var cellw = (maxX - minX) / 8;
        var cellh = (maxY - minY) / 9;
        var half = cellw / 2;

        for (var r = 0; r < 10; r++)
            for (var c = 0; c < 9; c++)
            {
                if (!prevOcc.Filled[r, c] || !curOcc.Filled[r, c]) continue; // 只看两帧都有子的格
                var rawCol = reverseRows ? 8 - c - colOffset : c - colOffset; // 我方黑列也翻转
                var screenRow = reverseRows ? 9 - r - rowOffset : r - rowOffset;
                var cx = minX + rawCol * cellw;
                var cy = minY + screenRow * cellh;
                var diff = CellInkDiff(prev, cur,
                    (int)(cx - half), (int)(cy - half), (int)(cx + half), (int)(cy + half));
                if (diff > InkThreshold) result.Add((r, c));
            }
        return result;
    }

    private static double CellInkDiff(SKBitmap prev, SKBitmap cur, int x1, int y1, int x2, int y2)
    {
        // 中心 50% 区域（避开外圈红绿光晕）
        var cx1 = x1 + (x2 - x1) / 4;
        var cy1 = y1 + (y2 - y1) / 4;
        var cx2 = x2 - (x2 - x1) / 4;
        var cy2 = y2 - (y2 - y1) / 4;
        using var a = ImageOps.Crop(prev, cx1, cy1, cx2, cy2);
        using var b = ImageOps.Crop(cur, cx1, cy1, cx2, cy2);
        if (a is null || b is null || a.Width == 0 || a.Height == 0 || b.Width == 0 || b.Height == 0)
            return 0; // 裁剪越界（格子在画面外）——无信号，按无变化处理
        var pa = ImageOps.GetRgbaBytes(a);
        var pb = ImageOps.GetRgbaBytes(b);
        long sum = 0;
        var n = 0;
        for (var i = 0; i < pa.Length; i += 4)
        {
            sum += Math.Abs(pa[i] - pb[i]) + Math.Abs(pa[i + 1] - pb[i + 1]) + Math.Abs(pa[i + 2] - pb[i + 2]);
            n += 3;
        }
        return n == 0 ? 0 : (double)sum / n;
    }

    private sealed record Observed(bool[,] Prev, bool[,] Cur, HashSet<(int R, int C)> Cap);
}
