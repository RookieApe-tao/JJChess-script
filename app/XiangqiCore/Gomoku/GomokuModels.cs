namespace XiangqiCore.Gomoku;

public enum GomokuStone { Black = 0, White = 1 }

/// <summary>15×15 五子棋盘面。row 0 = 屏幕最上行, col 0 = 最左列(与截图同向,不翻转)。</summary>
public sealed class GomokuPosition
{
    public const int Size = 15;

    public GomokuStone?[,] Cells { get; private set; } = new GomokuStone?[Size, Size];

    public int BlackCount { get; private set; }
    public int WhiteCount { get; private set; }

    public GomokuStone? this[int row, int col]
    {
        get => Cells[row, col];
        set
        {
            var old = Cells[row, col];
            if (old == value) return;
            if (old == GomokuStone.Black) BlackCount--;
            else if (old == GomokuStone.White) WhiteCount--;
            if (value == GomokuStone.Black) BlackCount++;
            else if (value == GomokuStone.White) WhiteCount++;
            Cells[row, col] = value;
        }
    }

    /// <summary>黑先:黑白相等轮黑,黑多一则轮白。偏差 >1 视为非法,仍返回黑。</summary>
    public GomokuStone SideToMove => BlackCount == WhiteCount ? GomokuStone.Black : GomokuStone.White;

    public GomokuPosition Clone()
    {
        var p = new GomokuPosition();
        for (var r = 0; r < Size; r++)
            for (var c = 0; c < Size; c++)
                p[r, c] = Cells[r, c];
        return p;
    }

    public GomokuPosition Apply(int row, int col, GomokuStone stone)
    {
        var p = Clone();
        p[row, col] = stone;
        return p;
    }

    private static readonly (int Dr, int Dc)[] Directions = { (0, 1), (1, 0), (1, 1), (1, -1) };

    /// <summary>(row,col) 落 stone 后是否形成 ≥5 连(无禁手,六连也算胜)。</summary>
    public static bool IsWinningAt(GomokuStone?[,] cells, int row, int col, GomokuStone stone)
    {
        foreach (var (dr, dc) in Directions)
        {
            var count = 1;
            for (var k = 1; k < 5; k++)
            {
                var r = row + dr * k; var c = col + dc * k;
                if (r < 0 || r >= Size || c < 0 || c >= Size || cells[r, c] != stone) break;
                count++;
            }
            for (var k = 1; k < 5; k++)
            {
                var r = row - dr * k; var c = col - dc * k;
                if (r < 0 || r >= Size || c < 0 || c >= Size || cells[r, c] != stone) break;
                count++;
            }
            if (count >= 5) return true;
        }
        return false;
    }

    public bool IsWinningAt(int row, int col, GomokuStone stone) => IsWinningAt(Cells, row, col, stone);

    /// <summary>全盘是否已有 ≥5 连(判断对局已结束)。</summary>
    public (bool Over, GomokuStone Winner, int Row, int Col)? FindWinner()
    {
        for (var r = 0; r < Size; r++)
            for (var c = 0; c < Size; c++)
            {
                var s = Cells[r, c];
                if (s is null) continue;
                // 只从连段起点判,避免重复报告
                foreach (var (dr, dc) in Directions)
                {
                    var pr = r - dr; var pc = c - dc;
                    if (pr >= 0 && pr < Size && pc >= 0 && pc < Size && Cells[pr, pc] == s) continue;
                    var count = 1;
                    while (true)
                    {
                        var nr = r + dr * count; var nc = c + dc * count;
                        if (nr < 0 || nr >= Size || nc < 0 || nc >= Size || Cells[nr, nc] != s) break;
                        count++;
                    }
                    if (count >= 5) return (true, s.Value, r, c);
                }
            }
        return null;
    }

    /// <summary>标准记谱:列 A–O(左→右)+ 行 1–15(上→下),如天元 H8。</summary>
    public static string CellName(int row, int col) =>
        $"{(char)('A' + col)}{row + 1}";

    /// <summary>带数字坐标的完整形式,如 "H8(8,8)"(行列均为 1 基)。</summary>
    public static string CellNameFull(int row, int col) =>
        $"{CellName(row, col)}({col + 1},{row + 1})";
}

/// <summary>识别结果:盘面 + 每格采样亮度(诊断/调阈值用,-1 = 空)。</summary>
public sealed class GomokuReadResult
{
    public GomokuPosition Position { get; set; } = new();
    public int[,] Brightness { get; set; } = new int[GomokuPosition.Size, GomokuPosition.Size];
    /// <summary>网格检出置信度:15×15 线都检出 = 高;回退等分 = 低。</summary>
    public bool GridDetected { get; set; }
    /// <summary>最近一手(检测到红标记时),null = 无。</summary>
    public (int Row, int Col)? LastMoveHint { get; set; }
    /// <summary>黑白子数差 >1 时的警告文本,null = 校验通过。</summary>
    public string? Warning { get; set; }
}
