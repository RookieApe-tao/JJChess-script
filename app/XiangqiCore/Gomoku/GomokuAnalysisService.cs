using SkiaSharp;

namespace XiangqiCore.Gomoku;

/// <summary>五子棋分析对外结果:建议文本 + 建议落点的屏幕坐标(画绿圈用)。</summary>
public sealed class GomokuAdvice
{
    public bool Ok { get; set; }
    public string Text { get; set; } = "";
    /// <summary>建议落点换算回屏幕物理像素的坐标,null = 无(失败/已成五)。</summary>
    public (float X, float Y)? MarkerPoint { get; set; }
    /// <summary>本次识别到的盘面(诊断)。</summary>
    public GomokuReadResult? Read { get; set; }
}

/// <summary>
/// 五子棋编排服务:画框标定 → 全量识别 → 校验 → 引擎建议。
/// 与 TrackingAnalysisService 不同,无需增量跟踪:每次全量重读 225 交叉点,
/// 落子 = 与上一盘面 diff(五子棋只增不减,除非对方提子,罕见)。
/// </summary>
public sealed class GomokuAnalysisService : IDisposable
{
    private const int DefaultBudgetMs = 2500;

    private readonly GomokuRecognizer _recognizer = new();
    private readonly GomokuEngine _engine = new();
    private GomokuRect _rect;
    private bool _hasRect;
    private GomokuPosition? _lastPosition; // 上次确认盘面(手数记账)
    private int _moveCount;

    public bool HasRect => _hasRect;
    public int MoveCount => _moveCount;

    /// <summary>画框标定:检测网格线并缓存格点。返回给用户的中文提示。</summary>
    public string Calibrate(SKBitmap frame, GomokuRect rect)
    {
        _rect = rect;
        _hasRect = true;
        var ok = _recognizer.RefineGrid(frame, rect);
        var info = _recognizer.GridInfo;
        _lastPosition = null;
        _moveCount = 0;
        return ok
            ? $"画框成功:检出 15×15 网格,格距约 {info!.Value.Step:F0}px。可点『分析』。"
            : "画框已保存,但网格线检测失败(已按 15 等分估位)。\n若识别错位,请在棋盘更完整可见时重新画框。";
    }

    /// <summary>新局:清空记账与格点缓存(下次分析重新检测网格)。</summary>
    public string Reset()
    {
        _recognizer.InvalidateGrid();
        _lastPosition = null;
        _moveCount = 0;
        return "已清盘。本局走子后点『分析』获取建议。";
    }

    /// <summary>失效当前标定(切换我方颜色等)。</summary>
    public void Invalidate()
    {
        _recognizer.InvalidateGrid();
        _lastPosition = null;
    }

    /// <summary>分析当前帧。myColor = 我方执子颜色。</summary>
    public GomokuAdvice Analyze(SKBitmap frame, GomokuStone myColor, int timeBudgetMs = DefaultBudgetMs)
    {
        var advice = new GomokuAdvice();
        if (!_hasRect)
        {
            advice.Text = "尚未画框标定棋盘。请先点『画框』框住棋盘。";
            return advice;
        }

        var read = _recognizer.Read(frame, _rect);
        advice.Read = read;
        var pos = read.Position;

        if (read.Warning is not null && pos.BlackCount + pos.WhiteCount > 0)
        {
            advice.Text = read.Warning;
            return advice;
        }

        if (pos.BlackCount + pos.WhiteCount == 0)
        {
            advice.Text = "未识别到棋子。若棋盘上已有子,请重新『画框』(框需盖住整个棋盘)。";
            return advice;
        }

        // 胜负检查
        var winner = pos.FindWinner();
        if (winner is not null)
        {
            var w = winner.Value.Winner == GomokuStone.Black ? "黑" : "白";
            var where = GomokuPosition.CellNameFull(winner.Value.Row, winner.Value.Col);
            advice.Ok = true;
            advice.Text = $"{w}方已连五获胜(胜势起点 {where}),本局结束。\n点『新局』开始下一局。";
            return advice;
        }

        // 手数记账:与上次确认盘面的 diff(只增)
        if (_lastPosition is not null)
        {
            var added = 0;
            for (var r = 0; r < GomokuPosition.Size; r++)
                for (var c = 0; c < GomokuPosition.Size; c++)
                    if (_lastPosition[r, c] is null && pos[r, c] is not null) added++;
            if (added > 0) _moveCount += added;
        }
        _lastPosition = pos;

        var side = pos.SideToMove;
        var sideName = side == GomokuStone.Black ? "黑" : "白";
        var mine = side == myColor;

        var lines = new List<string>();

        // 引擎建议
        var search = _engine.Search(pos, timeBudgetMs);
        if (search.Move is null)
        {
            advice.Text = search.Note ?? "无建议";
            return advice;
        }

        var scoreTxt = Math.Abs(search.Score) >= 1000
            ? (search.ForcedWin ? "必胜" : (search.Score > 0 ? "大优" : "大劣"))
            : $"{search.Score / 1000.0:+0.0;-0.0}";
        lines.Add($"轮到{sideName}方{(mine ? "(你)" : "(对方)")} → 建议 {search.Move.Value.Name}" +
                  (search.Note is null ? "" : $"〔{search.Note}〕") +
                  (search.Depth > 0 ? $" 深度{search.Depth}" : "") +
                  $" 评分{scoreTxt}");

        // 关键威胁提示:对方存在活三/活四(下一手能形成活四/成五威胁)
        var opp = side == GomokuStone.Black ? GomokuStone.White : GomokuStone.Black;
        var threat = WorstThreat(pos, opp);
        if (threat >= 100_000)
            lines.Add("⚠ 对方已有冲四/活四,务必优先处理!");
        else if (threat >= 10_000)
            lines.Add("⚠ 对方已有活三,建议防守!");

        if (_moveCount > 0)
            lines.Add($"已 {_moveCount} 手(黑 {pos.BlackCount} / 白 {pos.WhiteCount})");

        advice.Ok = true;
        advice.Text = string.Join("\n", lines);
        advice.MarkerPoint = _recognizer.ScreenPoint(search.Move.Value.Row, search.Move.Value.Col, _rect);
        return advice;
    }

    /// <summary>对方在最优落点的最高模式分(判断威胁等级)。</summary>
    private static long WorstThreat(GomokuPosition pos, GomokuStone stone)
    {
        long worst = 0;
        for (var r = 0; r < GomokuPosition.Size; r++)
            for (var c = 0; c < GomokuPosition.Size; c++)
            {
                if (pos[r, c] is not null) continue;
                long s = 0;
                foreach (var (dr, dc) in new[] { (0, 1), (1, 0), (1, 1), (1, -1) })
                {
                    // 复用引擎的 LineScore 思路:落子后模式分
                    s += LinePatternScore(pos.Cells, r, c, dr, dc, stone);
                }
                if (s > worst) worst = s;
            }
        return worst;
    }

    private static long LinePatternScore(GomokuStone?[,] cells, int row, int col, int dr, int dc, GomokuStone stone)
    {
        const int size = GomokuPosition.Size;
        var count = 1;
        var openA = false; var openB = false;
        int k = 1;
        while (true)
        {
            var r = row + dr * k; var c = col + dc * k;
            if (r < 0 || r >= size || c < 0 || c >= size) break;
            if (cells[r, c] == stone) { count++; k++; continue; }
            openA = cells[r, c] is null;
            break;
        }
        k = 1;
        while (true)
        {
            var r = row - dr * k; var c = col - dc * k;
            if (r < 0 || r >= size || c < 0 || c >= size) break;
            if (cells[r, c] == stone) { count++; k++; continue; }
            openB = cells[r, c] is null;
            break;
        }
        var opens = (openA ? 1 : 0) + (openB ? 1 : 0);
        return count switch
        {
            >= 5 => 10_000_000L,
            4 => opens == 2 ? 1_000_000L : opens == 1 ? 100_000L : 0,
            3 => opens == 2 ? 10_000L : opens == 1 ? 1_000L : 0,
            2 => opens == 2 ? 100L : 0,
            _ => 0,
        };
    }

    public void Dispose()
    {
        // 引擎是进程内的,无外部资源
    }
}
