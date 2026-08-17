using XiangqiCore.Recognition;

namespace XiangqiAssistant;

/// <summary>JJ 象棋棋盘标定（1440x3200 截图坐标，与 phase0/jj_layout.json 一致）。</summary>
public static class JjCalibration
{
    public static Calibration Instance { get; } = new(
        new[] { 80f, 1360f, 1360f, 80f },
        new[] { 900f, 900f, 2290f, 2290f },
        CropMargin: 15);
}
