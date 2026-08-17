using SkiaSharp;
using XiangqiCore.Recognition;

namespace XiangqiCore;

public class AnalysisResult
{
    public string Fen { get; set; } = "";
    public string? BestMove { get; set; }
    public string? BestMoveChinese { get; set; }
    public int? ScoreCp { get; set; }
    public int? Depth { get; set; }
    public string? Pv { get; set; }
    public List<string> PvChinese { get; set; } = new();
    public int PieceCount { get; set; }
    public string? Error { get; set; }
}

public sealed class AnalysisService : IDisposable
{
    private readonly BoardRecognizer _recognizer;
    private XiangqiEngine _engine;
    private readonly string _engineExe;
    private readonly string _nnuePath;

    public AnalysisService(string modelDir, string engineExe, string nnuePath)
    {
        _recognizer = new BoardRecognizer(modelDir);
        _engineExe = engineExe;
        _nnuePath = nnuePath;
        _engine = XiangqiEngine.Start(engineExe, nnuePath);
    }

    public AnalysisService(BoardRecognizer recognizer, string engineExe, string nnuePath)
    {
        _recognizer = recognizer;
        _engineExe = engineExe;
        _nnuePath = nnuePath;
        _engine = XiangqiEngine.Start(engineExe, nnuePath);
    }

    public AnalysisResult Analyze(SKBitmap image, Calibration? cal, string side = "w",
        int movetimeMs = 2500, bool reverseRows = false)
    {
        var result = new AnalysisResult();
        try
        {
            var board = _recognizer.Analyze(image, cal, side, reverseRows);
            result.Fen = board.Fen;
            result.PieceCount = board.Cells.Count;

            EngineAnalysis res;
            try
            {
                res = _engine.Analyze(board.Fen, movetimeMs);
            }
            catch (IOException)
            {
                // 引擎进程异常退出（如 broken pipe）：重启后重试一次
                _engine.Dispose();
                _engine = XiangqiEngine.Start(_engineExe, _nnuePath);
                res = _engine.Analyze(board.Fen, movetimeMs);
            }
            result.ScoreCp = res.ScoreCp;
            result.Depth = res.Depth;
            result.Pv = res.Pv;
            if (res.BestMove is not null)
            {
                result.BestMove = res.BestMove.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1];
                result.BestMoveChinese = Notation.BestMoveToChinese(board.Fen, res.BestMove);
            }
            if (res.Pv is not null)
            {
                var moves = res.Pv.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var cur = board.Fen;
                foreach (var mv in moves.Take(8))
                {
                    result.PvChinese.Add(Notation.MoveToChinese(cur, mv));
                    cur = Notation.ApplyMove(cur, mv);
                }
            }
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
        }
        return result;
    }

    public void Dispose()
    {
        _engine.Dispose();
        _recognizer.Dispose();
    }
}
