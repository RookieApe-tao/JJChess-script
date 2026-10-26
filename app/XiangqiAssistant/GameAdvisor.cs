using Android.Content;
using SkiaSharp;
using XiangqiCore;
using XiangqiCore.Gomoku;

namespace XiangqiAssistant;

public enum GameMode { Xiangqi, Gomoku }

/// <summary>悬浮窗回复类别:决定状态色(白/黄/绿/红)。</summary>
public enum ReplyKind { Ok, Busy, Err, Retry }

/// <summary>悬浮窗统一回复:文本 + 状态类别。五子棋附绿圈坐标。</summary>
public sealed class OverlayReply
{
    public string Text { get; init; } = "";
    public ReplyKind Kind { get; init; } = ReplyKind.Ok;
    /// <summary>五子棋建议落点的屏幕物理像素坐标(画绿圈),null = 不画。</summary>
    public (float X, float Y)? Marker { get; init; }

    public static OverlayReply OkText(string text) => new() { Text = text, Kind = ReplyKind.Ok };
    public static OverlayReply ErrText(string text) => new() { Text = text, Kind = ReplyKind.Err };
}

/// <summary>
/// 悬浮窗与具体游戏解耦的接口:象棋/五子棋各一个实现,
/// AssistantService 只依赖它(截屏、权限、按钮流程全游戏无关)。
/// </summary>
public interface IGameAdvisor : IDisposable
{
    /// <summary>「分析」:识别当前帧并给建议。</summary>
    OverlayReply Analyze(SKBitmap frame, int timeMs);
    /// <summary>「新局」。</summary>
    OverlayReply NewGame(SKBitmap frame);
    /// <summary>使当前对局状态失效(切换我方颜色后调用)。</summary>
    void Invalidate();
}

/// <summary>象棋适配器:包装 TrackingAnalysisService,翻译 TrackingResult。
/// 「记录」「应急」为象棋专用流程,经属性直接暴露给 AssistantService。</summary>
public sealed class XiangqiAdvisor : IGameAdvisor
{
    private readonly TrackingAnalysisService _svc;
    public bool MySideBlack { get; set; }

    public XiangqiAdvisor(TrackingAnalysisService svc) => _svc = svc;

    public TrackingResult TrackOnly(SKBitmap frame) => _svc.TrackOnly(frame);

    public (bool Ok, string Message) Resync(SKBitmap frame, bool mySideBlack, XiangqiCore.Recognition.Calibration cal, int movetimeMs) =>
        _svc.Resync(frame, mySideBlack, cal, movetimeMs);

    public OverlayReply Analyze(SKBitmap frame, int timeMs)
    {
        var r = _svc.Analyze(frame, timeMs);
        return new OverlayReply { Text = Format(r), Kind = KindFor(r) };
    }

    public OverlayReply NewGame(SKBitmap frame) =>
        new() { Text = _svc.Reset(frame, MySideBlack, JjCalibration.Instance), Kind = ReplyKind.Ok };

    public void Invalidate() => _svc.Invalidate();

    private static string Format(TrackingResult r)
    {
        if (r.Result == TrackerMatch.Kind.LostTrack)
            return r.Reason ?? "跟踪失败，请点『新局』重置后重试。";
        var lines = new List<string>();
        if (!string.IsNullOrEmpty(r.MoveSummary)) lines.Add(r.MoveSummary);
        if (!string.IsNullOrEmpty(r.MoveCountText)) lines.Add(r.MoveCountText);
        if (!string.IsNullOrEmpty(r.Advice)) lines.Add(r.Advice);
        return lines.Count > 0 ? string.Join("\n", lines) : "就绪";
    }

    private static ReplyKind KindFor(TrackingResult r) =>
        r.Result == TrackerMatch.Kind.LostTrack
            ? (r.Reason?.Contains("移动") ?? false ? ReplyKind.Retry : ReplyKind.Err)
            : ReplyKind.Ok;

    public void Dispose() => _svc.Dispose();
}

/// <summary>五子棋适配器:包装 GomokuAnalysisService,管理画框矩形持久化。</summary>
public sealed class GomokuAdvisor : IGameAdvisor
{
    private readonly GomokuAnalysisService _svc = new();
    private readonly Context _ctx;
    public GomokuStone MyColor { get; set; } = GomokuStone.Black;

    public GomokuAdvisor(Context ctx) => _ctx = ctx;

    /// <summary>画框完成:保存矩形并标定。返回提示文本。</summary>
    public OverlayReply Calibrate(SKBitmap frame, float x1, float y1, float x2, float y2)
    {
        var rect = new GomokuRect(x1, y1, x2, y2);
        if (!rect.IsValid)
            return OverlayReply.ErrText("画的框太小,已放弃。请重新点『画框』,框住整个棋盘。");
        SaveRect(rect);
        return new() { Text = _svc.Calibrate(frame, rect), Kind = ReplyKind.Ok };
    }

    public OverlayReply Analyze(SKBitmap frame, int timeMs)
    {
        var a = _svc.Analyze(frame, MyColor, timeMs);
        return new OverlayReply { Text = a.Text, Kind = a.Ok ? ReplyKind.Ok : ReplyKind.Err, Marker = a.MarkerPoint };
    }

    public OverlayReply NewGame(SKBitmap frame) =>
        new() { Text = _svc.Reset(), Kind = ReplyKind.Ok };

    public void Invalidate() => _svc.Invalidate();

    /// <summary>已保存的画框矩形,null = 尚未画框。</summary>
    public GomokuRect? SavedRect()
    {
        var prefs = _ctx.GetSharedPreferences("gomoku", FileCreationMode.Private);
        var has = prefs.GetBoolean("rect_set", false);
        if (!has) return null;
        return new GomokuRect(
            prefs.GetFloat("rect_x1", 0), prefs.GetFloat("rect_y1", 0),
            prefs.GetFloat("rect_x2", 0), prefs.GetFloat("rect_y2", 0));
    }

    private void SaveRect(GomokuRect rect)
    {
        var prefs = _ctx.GetSharedPreferences("gomoku", FileCreationMode.Private);
        prefs.Edit()
            .PutBoolean("rect_set", true)
            .PutFloat("rect_x1", rect.X1).PutFloat("rect_y1", rect.Y1)
            .PutFloat("rect_x2", rect.X2).PutFloat("rect_y2", rect.Y2)
            .Apply();
    }

    public void Dispose() => _svc.Dispose();
}
