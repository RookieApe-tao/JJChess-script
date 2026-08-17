namespace XiangqiCore.Recognition;

public class Detection
{
    public float X { get; set; }
    public float Y { get; set; }
    public float W { get; set; }
    public float H { get; set; }
    public float Cx { get; set; }
    public float Cy { get; set; }
    public float Conf { get; set; }
    public string ClassName { get; set; } = "";
    public string Piece { get; set; } = "";
    public string Color { get; set; } = "";
    public float Prob { get; set; }
    public List<(string Piece, float Prob)> Alts { get; set; } = new();
}

public class CellInfo
{
    public string Piece { get; set; } = "";
    public float Prob { get; set; }
    public List<(string Piece, float Prob)> Alts { get; set; } = new();
    public bool Corrected { get; set; }
}

public class RecognizedBoard
{
    public string?[,] Board { get; set; } = new string?[10, 9];
    public Dictionary<(int Row, int Col), CellInfo> Cells { get; set; } = new();
    public string Fen { get; set; } = "";
}

public record Calibration(float[] Xs, float[] Ys, int CropMargin);

/// <summary>一帧的棋盘占位（只问“格上有无子”，不分类棋子类型）。board 约定同 <see cref="Notation"/>：row 0 = 黑方底线。</summary>
public sealed class Occupancy
{
    public bool[,] Filled = new bool[10, 9];
    /// <summary>水平列校正（由中路最近的子推得，或由”新局”帧锁定）。用于消除标定横向小偏移。</summary>
    public int ColOffset;
    /// <summary>垂直行校正（由最靠近标定上缘的棋子推得）。用于消除标定纵向小偏移。</summary>
    public int RowOffset;
    /// <summary>0..0.5：检测框中心离最近格点的最大分数距离，>0.35 视为棋子仍在移动（动画中途）。</summary>
    public float OffGridFraction;
}

