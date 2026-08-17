using Android.Content;
using XiangqiCore;
using XiangqiCore.Recognition;

namespace XiangqiAssistant;

public static class AndroidAnalysisFactory
{
    private const string EngineLibName = "libpikafish.so";
    private const string NnueAsset = "Resources/Raw/pikafish.nnue";
    private const string DetectorAsset = "Resources/Raw/online_xiangqi_piece_detector.onnx";
    private const string ClassifierAsset = "Resources/Raw/online_xiangqi_classifier.onnx";

    public static AnalysisService Create(Context context) => new AnalysisService(BuildRecognizer(context, out var ep, out var nnue), ep, nnue);

    /// <summary>增量跟踪 + 引擎建议服务（主路径）。</summary>
    public static TrackingAnalysisService CreateTracking(Context context) =>
        new TrackingAnalysisService(BuildRecognizer(context, out var enginePath, out var nnuePath), enginePath, nnuePath);

    private static BoardRecognizer BuildRecognizer(Context context, out string enginePath, out string nnuePath)
    {
        // The engine is shipped as a native library (libpikafish.so) so it lives in the
        // read-only, executable nativeLibraryDir. Android 10+ forbids running binaries
        // from the writable files dir (SELinux W^X -> EACCES), so we must NOT extract it.
        enginePath = Path.Combine(context.ApplicationInfo!.NativeLibraryDir!, EngineLibName);
        if (!File.Exists(enginePath))
            throw new InvalidOperationException($"引擎未找到: {enginePath}（未打包为 arm64-v8a native 库）");
        nnuePath = Extract(context, NnueAsset, executable: false);
        var detector = ReadAsset(context, DetectorAsset);
        var classifier = ReadAsset(context, ClassifierAsset);
        return new BoardRecognizer(detector, classifier);
    }

    private static string Extract(Context context, string assetName, bool executable)
    {
        var dest = Path.Combine(context.FilesDir!.AbsolutePath!, assetName);
        if (!File.Exists(dest))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            using var input = context.Assets!.Open(assetName);
            using var output = File.Create(dest);
            input.CopyTo(output);
        }
        if (executable)
            File.SetUnixFileMode(dest, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                                        UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                                        UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        return dest;
    }

    private static byte[] ReadAsset(Context context, string assetName)
    {
        using var input = context.Assets!.Open(assetName);
        using var ms = new MemoryStream();
        input.CopyTo(ms);
        return ms.ToArray();
    }
}
