using SkiaSharp;
using XiangqiCore.Recognition;

namespace XiangqiCore;

/// <summary>跟踪分析对外的展示结果。</summary>
public sealed class TrackingResult
{
    public TrackerMatch.Kind Result { get; set; } = TrackerMatch.Kind.LostTrack;
    /// <summary>需要提示用户的中文原因（未新局 / 动画中途 / 脱 Track）。</summary>
    public string? Reason { get; set; }
    /// <summary>引擎建议（NoChange 与 Matched 时有值）。</summary>
    public string? Advice { get; set; }
    /// <summary>本次走子序列摘要，如“你 兵五进一 → 对方 炮8平5”。NoChange 时为 null。</summary>
    public string? MoveSummary { get; set; }
    /// <summary>本局累计手数文本，如“已 6 手（你 3 / 对方 3）”。跟踪成功（NoChange/Matched）时有值。</summary>
    public string? MoveCountText { get; set; }
    public List<MoveCandidate> Candidates { get; } = new();
}

/// <summary>
/// 增量跟踪 + 引擎建议的编排服务。维护一份已知 FEN，每次只确认 1–2 步，
/// 然后让 Pikafish 给当前轮到方的最优着法。仅缓存上一确认帧的棋盘裁剪，不持久化。
/// </summary>
public sealed class TrackingAnalysisService : IDisposable
{
    public const string StandardStart = "rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w";

    private const float SettleThreshold = 0.35f; // 检测框中心离格点超过此分数 -> 视为棋子仍在移动

    private readonly MoveTracker _tracker;
    private readonly BoardRecognizer _recognizer; // 应急重校需绕过跟踪直接全量识别
    private XiangqiEngine _engine; // 引擎异常退出后需重启，故非 readonly
    private readonly string _engineExe;
    private readonly string _nnuePath;

    private Calibration? _cal;
    private bool _mySideBlack;
    private int _pinnedColOffset;
    private int _pinnedRowOffset;

    private string _fen = StandardStart;
    private string _preMatchFen = StandardStart;
    private SKBitmap? _confirmedFrame;   // 仅棋盘区域裁剪，供下一帧像素 diff
    private Occupancy? _confirmedOcc;
    private List<MoveCandidate> _lastCandidates = new();
    private readonly List<string> _moveLog = new(); // 本局每步记谱（中文，含 你/对方 前缀）
    private int _moveCount;
    private int _preMatchLogCount; // 上次提交前的日志长度，供 Undo 整体回退（一次可确认 1–2 步）

    public bool Tracking { get; private set; }

    public TrackingAnalysisService(BoardRecognizer recognizer, string engineExe, string nnuePath)
    {
        _tracker = new MoveTracker(recognizer);
        _recognizer = recognizer;
        _engineExe = engineExe;
        _nnuePath = nnuePath;
        _engine = XiangqiEngine.Start(engineExe, nnuePath);
    }

    /// <summary>新局：抓参考帧，并与标准开局占位比对，自动识别开局后已走的 1–2 步
    /// （例如我方黑时对方秒走的首招）。只用检测器占位、不用分类器，故 我方黑 朝向翻转下也可靠。
    /// 返回给用户的中文提示。</summary>
    public string Reset(SKBitmap startFrame, bool mySideBlack, Calibration cal)
    {
        _mySideBlack = mySideBlack;
        _cal = cal;
        _lastCandidates = new List<MoveCandidate>();
        _moveLog.Clear();
        _moveCount = 0;
        // null 让 BuildOccupancy 从当前帧自动计算 colOffset/rowOffset，避免校准偏移时行列持续错位。
        var cur = _tracker.BuildOccupancy(startFrame, _cal, mySideBlack, null, null);
        _pinnedColOffset = cur.ColOffset;
        _pinnedRowOffset = cur.RowOffset;

        // 引导：把当前帧占位与标准开局比对，自动解释已走的步数。开局首招几乎不可能是吃子，
        // 故 prev/cur 用同一帧（ChangedInkCells 得 0、无 capDest），仅靠占位 diff 匹配。
        var match = _tracker.Track(StandardStart, OccupancyFromFen(StandardStart), cur, startFrame, startFrame, _cal, mySideBlack);
        string msg;
        if (match.Result == TrackerMatch.Kind.Matched && match.Top is not null)
        {
            _fen = match.Top.ResultingFen;
            _preMatchFen = _fen;
            AppendMovesToLog(match.Top, 'w');
            var who = SideChar(_fen) == (mySideBlack ? 'b' : 'w') ? "你" : "对方";
            msg = $"已设为开局，识别到：{FormatMoves(match.Top, 'w')}\n轮到{who}走，点『分析』获取建议。";
        }
        else if (match.Result == TrackerMatch.Kind.NoChange)
        {
            _fen = StandardStart;
            _preMatchFen = StandardStart;
            msg = "已设为开局（尚无走子）。走子后点『分析』获取建议。";
        }
        else
        {
            _fen = StandardStart;
            _preMatchFen = StandardStart;
            // 诊断：数差异 + 输出校准网格与占位 FEN，帮助判断是标定不准还是坐标转换问题
            var ssOcc = OccupancyFromFen(StandardStart);
            var src = 0; var dst = 0;
            for (var r = 0; r < 10; r++) for (var c = 0; c < 9; c++)
            {
                if (ssOcc.Filled[r, c] && !cur.Filled[r, c]) src++;
                if (cur.Filled[r, c] && !ssOcc.Filled[r, c]) dst++;
            }
            var cellw = (cal.Xs.Max() - cal.Xs.Min()) / 8.0;
            var cellh = (cal.Ys.Max() - cal.Ys.Min()) / 9.0;
            var curFen = OccupancyToFenGhost(cur.Filled);
            msg = $"已偏离开局过多（{src}出+{dst}入，预期1–2步）。\n" +
                  $"校准格距: {cellw:F0}×{cellh:F0} px, colOff={cur.ColOffset} rowOff={cur.RowOffset}\n" +
                  $"开局占位: {StandardStart.Split(' ')[0]}\n" +
                  $"当前占位: {curFen}\n" +
                  $"↑两行差异↑ 若整行/整列错位 → 校准偏移；随机错 → 识别干扰";
        }

        _confirmedOcc = cur;
        StoreFrame(startFrame);
        Tracking = true;
        return msg;
    }

    /// <summary>应急重校：记录混乱时，放弃增量跟踪历史，对当前帧做全量识别直接重建基线。
    /// 手数清零重计。轮到方设为我方（发现乱账时通常轮到自己走）。
    /// 返回 (是否成功, 提示文本)。</summary>
    public (bool Ok, string Message) Resync(SKBitmap frame, bool mySideBlack, Calibration cal, int movetimeMs)
    {
        // 与 Reset 相同的朝向/占位参数，保证后续 Analyze 在同一坐标系下继续。
        _mySideBlack = mySideBlack;
        _cal = cal;
        _lastCandidates = new List<MoveCandidate>();

        var occ = _tracker.BuildOccupancy(frame, cal, mySideBlack, null, null);
        _pinnedColOffset = occ.ColOffset;
        _pinnedRowOffset = occ.RowOffset;

        // 全量识别（检测 + 分类 + 着色 + 合法性修正），不依赖任何历史 FEN。
        var side = mySideBlack ? "b" : "w";
        var rec = _recognizer.Analyze(frame, cal, side, reverseRows: mySideBlack);
        var fen = rec.Fen;

        // 基本校验：双方将/帅各 1 个，且与检测占位一致（分类错误通常表现为占位对不上或双将）。
        var kings = CountKings(fen);
        if (kings != (1, 1) || !PlacementMatches(fen, occ))
        {
            var reason = kings != (1, 1)
                ? $"将/帅识别异常（红{kings.Item1}黑{kings.Item2}）"
                : "识别结果与棋子占位不一致";
            return (false, $"应急重校失败：{reason}。\n请确保棋子静止、棋盘完整可见后重试，或点『新局』。");
        }

        _fen = fen;
        _preMatchFen = fen;
        _moveLog.Clear();
        _moveCount = 0;
        _preMatchLogCount = 0;
        _confirmedOcc = occ;
        StoreFrame(frame);
        Tracking = true;

        var advice = EngineAdvice(_fen, movetimeMs);
        return (true, $"应急重校成功，手数已清零。\n{advice}");
    }

    /// <summary>数 FEN 里红帅/黑将的个数。</summary>
    private static (int Red, int Black) CountKings(string fen)
    {
        var red = 0; var black = 0;
        foreach (var ch in fen)
        {
            if (ch == 'K') red++;
            else if (ch == 'k') black++;
        }
        return (red, black);
    }

    /// <summary>全量识别出的 FEN 占位是否与检测器占位完全一致（分类异常时的兜底校验）。</summary>
    private static bool PlacementMatches(string fen, Occupancy occ)
    {
        var (b, _) = Notation.ParseFen(fen);
        for (var r = 0; r < 10; r++)
            for (var c = 0; c < 9; c++)
                if ((b[r, c] is not null) != occ.Filled[r, c])
                    return false;
        return true;
    }

    private static Occupancy OccupancyFromFen(string fen)
    {
        var (b, _) = Notation.ParseFen(fen);
        var o = new Occupancy();
        for (var r = 0; r < 10; r++)
            for (var c = 0; c < 9; c++)
                o.Filled[r, c] = b[r, c] is not null;
        return o;
    }

    /// <summary>把占位 bool[10,9] 转成 FEN 样式的字符串（用 ? 代替具体棋子），便于与标准开局逐行对照。</summary>
    private static string OccupancyToFenGhost(bool[,] filled)
    {
        var rows = new System.Collections.Generic.List<string>();
        for (var r = 0; r < 10; r++)
        {
            var s = "";
            var empty = 0;
            for (var c = 0; c < 9; c++)
            {
                if (filled[r, c]) { if (empty > 0) { s += empty; empty = 0; } s += "?"; }
                else empty++;
            }
            if (empty > 0) s += empty;
            rows.Add(s);
        }
        return string.Join("/", rows);
    }

    /// <summary>分析当前帧：跟踪 1–2 步 -> 更新 FEN -> 引擎建议。</summary>
    public TrackingResult Analyze(SKBitmap frame, int movetimeMs)
    {
        var result = TrackCore(frame);
        if (result.Result == TrackerMatch.Kind.NoChange || result.Result == TrackerMatch.Kind.Matched)
            result.Advice = EngineAdvice(_fen, movetimeMs);
        return result;
    }

    /// <summary>只跟踪走子、不跑引擎（用于「记录」按钮：快速记下手数，不需要建议时用）。</summary>
    public TrackingResult TrackOnly(SKBitmap frame) => TrackCore(frame);

    /// <summary>“已 N 手（你 M / 对方 K）”格式的当前手数文本。</summary>
    public string MoveCountText
    {
        get
        {
            var my = 0; var opp = 0;
            foreach (var s in _moveLog) { if (s.StartsWith("你")) my++; else opp++; }
            return _moveCount == 0
                ? "本局尚未走子"
                : $"已 {_moveCount} 手（你 {my} / 对方 {opp}）";
        }
    }

    /// <summary>跟踪当前帧并更新 FEN/记录；引擎调用由调用方决定。</summary>
    private TrackingResult TrackCore(SKBitmap frame)
    {
        var result = new TrackingResult();
        if (!Tracking || _cal is null || _confirmedOcc is null || _confirmedFrame is null)
        {
            result.Result = TrackerMatch.Kind.LostTrack;
            result.Reason = "请先点『新局』开始跟踪（开局时点一次即可）。";
            return result;
        }

        var cur = _tracker.BuildOccupancy(frame, _cal, _mySideBlack, _pinnedColOffset, _pinnedRowOffset);
        if (cur.OffGridFraction > SettleThreshold)
        {
            result.Result = TrackerMatch.Kind.LostTrack;
            result.Reason = "棋子仍在移动，请稍候再点『分析』。";
            return result;
        }

        var match = _tracker.Track(_fen, _confirmedOcc, cur, _confirmedFrame, frame, _cal, _mySideBlack);
        switch (match.Result)
        {
            case TrackerMatch.Kind.NoChange:
                result.Result = TrackerMatch.Kind.NoChange;
                result.MoveCountText = MoveCountText;
                return result;

            case TrackerMatch.Kind.LostTrack:
                result.Result = TrackerMatch.Kind.LostTrack;
                result.Reason = match.Reason;
                return result;

            default: // Matched
                var top = match.Top!;
                _preMatchFen = _fen;
                _preMatchLogCount = _moveLog.Count;
                var side0 = SideChar(_fen);
                _fen = top.ResultingFen;
                _confirmedOcc = cur;
                StoreFrame(frame);
                _lastCandidates = match.Candidates;
                AppendMovesToLog(top, side0);
                result.Result = TrackerMatch.Kind.Matched;
                result.Candidates.AddRange(match.Candidates);
                result.MoveSummary = FormatMoves(top, side0);
                result.MoveCountText = MoveCountText;
                return result;
        }
    }

    /// <summary>把候选走子序列按 你/对方 追加进本局记录。</summary>
    private void AppendMovesToLog(MoveCandidate cand, char side0)
    {
        var my = _mySideBlack ? 'b' : 'w';
        for (var i = 0; i < cand.Moves.Count; i++)
        {
            var s = (i % 2 == 0) ? side0 : (side0 == 'w' ? 'b' : 'w');
            _moveLog.Add($"{(s == my ? "你" : "对方")} {cand.MovesChinese[i]}");
            _moveCount++;
        }
    }

    /// <summary>撤销最近一次提交（恢复到匹配前的 FEN，并回退本次提交的全部手数）。帧缓存不回退（仍在最新一帧上继续）。</summary>
    public void Undo()
    {
        _fen = _preMatchFen;
        _lastCandidates = new List<MoveCandidate>();
        _moveCount -= _moveLog.Count - _preMatchLogCount;
        _moveLog.RemoveRange(_preMatchLogCount, _moveLog.Count - _preMatchLogCount);
    }

    /// <summary>使跟踪失效（切换我方红/黑、或重新标定后调用）：下一次 Analyze 会提示需要重新『新局』。</summary>
    public void Invalidate()
    {
        Tracking = false;
        _confirmedOcc = null;
        _confirmedFrame?.Dispose();
        _confirmedFrame = null;
        _lastCandidates = new List<MoveCandidate>();
    }

    /// <summary>选用第 idx 个候选（修正），返回其建议文本。</summary>
    public string ApplyCandidate(int idx, int movetimeMs)
    {
        if ((uint)idx >= (uint)_lastCandidates.Count) return "";
        var cand = _lastCandidates[idx];
        _fen = cand.ResultingFen;
        return EngineAdvice(_fen, movetimeMs);
    }

    public int CandidateCount => _lastCandidates.Count;
    public string CurrentFen => _fen;
    /// <summary>本局至今的中文记谱（每项形如“你 炮二平五”）。</summary>
    public IReadOnlyList<string> MoveLog => _moveLog;
    /// <summary>本局累计手数（半手计）。</summary>
    public int MoveCount => _moveCount;

    /// <summary>仅运行 YOLO 检测器，返回置信度 >= minConf 的原始检测框。用于标定等诊断用途。</summary>
    public List<Detection> DetectPieces(SKBitmap frame, float minConf = 0.5f) =>
        _tracker.DetectPieces(frame, minConf);

    private string EngineAdvice(string fen, int movetimeMs)
    {
        EngineAnalysis res;
        try { res = _engine.Analyze(fen, movetimeMs); }
        catch (IOException)
        {
            _engine.Dispose();
            _engine = XiangqiEngine.Start(_engineExe, _nnuePath); // 复用字段：引擎异常退出后重启
            res = _engine.Analyze(fen, movetimeMs);
        }

        if (res.BestMove is null) return "无合法着法（可能已被将死或和棋）";
        var best = res.BestMove.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (best.Length < 2) return "无合法着法";
        var bestCh = Notation.MoveToChinese(fen, best[1]);
        var side = SideChar(fen) == 'b' ? "黑" : "红";
        return $"{side}：{bestCh}";
    }

    private string FormatMoves(MoveCandidate cand, char side0)
    {
        var my = _mySideBlack ? 'b' : 'w';
        var parts = new List<string>();
        for (var i = 0; i < cand.Moves.Count; i++)
        {
            var s = (i % 2 == 0) ? side0 : (side0 == 'w' ? 'b' : 'w');
            var who = s == my ? "你" : "对方";
            parts.Add($"{who} {cand.MovesChinese[i]}");
        }
        return string.Join(" → ", parts);
    }

    private static char SideChar(string fen) => fen.TrimEnd().EndsWith(" b", StringComparison.Ordinal) ? 'b' : 'w';

    /// <summary>缓存上一确认帧的整帧副本（约 18MB，仅一张，有界）。必须与当前帧同坐标系，
    /// 否则 ChangedInkCells 的像素裁剪会越界——故不能用棋盘区域裁剪。</summary>
    private void StoreFrame(SKBitmap frame)
    {
        _confirmedFrame?.Dispose();
        _confirmedFrame = frame.Copy(SKColorType.Rgba8888);
    }

    public void Dispose()
    {
        _engine.Dispose();
        _confirmedFrame?.Dispose();
        _confirmedFrame = null;
    }
}
