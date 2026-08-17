using System.Runtime.InteropServices;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics.Drawables;
using Android.Hardware.Display;
using Android.Media.Projection;
using Android.OS;
using Android.Runtime;
using Android.Util;
using Android.Views;
using Android.Widget;
using SkiaSharp;
using XiangqiCore;
using XiangqiCore.Recognition;
using Android.Graphics;
using AButton = Android.Widget.Button;
using AColor = Android.Graphics.Color;
using ALinearLayout = Android.Widget.LinearLayout;
using ATextView = Android.Widget.TextView;
using AView = Android.Views.View;
using MImage = Android.Media.Image;

namespace XiangqiAssistant;

[Service(ForegroundServiceType = ForegroundService.TypeMediaProjection, Exported = false)]
public class AssistantService : Service
{
    public const string ActionStart = "com.xiangqiassistant.action.START";
    public const string ActionCapture = "com.xiangqiassistant.action.CAPTURE";
    public const string ExtraCode = "resultCode";
    public const string ExtraData = "data";

    private const int NotificationId = 1001;

    private MediaProjection? _projection;
    private readonly MediaProjection.Callback _projectionCallback = new ProjectionCallback();
    private Android.Media.ImageReader? _reader;
    private Android.Hardware.Display.VirtualDisplay? _virtualDisplay;
    private int _captureW, _captureH;
    private IWindowManager? _wm;
    private ALinearLayout? _overlay;
    private ATextView? _resultText;
    private Handler? _handler;
    private TrackingAnalysisService? _analysis;
    private volatile bool _busy;
    private volatile bool _initializing;
    private Action? _onAnalysisReady;
    private string _mySide = "w";        // "w"=红/我方, "b"=黑/我方
    private bool _collapsed;
    private ALinearLayout? _ctrlRow;
    private AButton? _sideBtn;

    private const string ColorIdle = "#FFFFFF";   // 空闲/未识别（白）
    private const string ColorBusy = "#FFF59D";   // 识别中（黄）
    private const string ColorOk = "#C8E6C9";     // 识别完成（绿）
    private const string ColorErr = "#FFCDD2";    // 出错（红）

    public override void OnCreate()
    {
        base.OnCreate();
        _handler = new Handler(Looper.MainLooper!);
        var svc = GetSystemService(WindowService);
        if (svc is not null)
            _wm = Java.Lang.Object.GetObject<IWindowManager>(svc.Handle, JniHandleOwnership.DoNotTransfer);
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (intent?.Action == ActionStart)
        {
            StartForeground(NotificationId, BuildNotification());
            var code = intent.GetIntExtra(ExtraCode, 0);
            var data = intent.GetParcelableExtra(ExtraData, Java.Lang.Class.FromType(typeof(Intent))) as Intent;
            if (data is not null)
            {
                _projection = ((MediaProjectionManager)GetSystemService(MediaProjectionService)!)
                    .GetMediaProjection(code, data);
                // Android 14+ (API 34) requires a registered callback before capture,
                // otherwise createVirtualDisplay throws SecurityException:
                // "Must register a callback before starting capture".
                _projection?.RegisterCallback(_projectionCallback, _handler);
                SetupCapture();
            }
            EnsureAnalysis();
            ShowOverlay();
        }
        else if (intent?.Action == ActionCapture)
        {
            _handler?.Post(() => CaptureAndAnalyze());
        }
        return StartCommandResult.Sticky;
    }

    private void EnsureAnalysis(Action? onReady = null)
    {
        if (_analysis is not null)
        {
            onReady?.Invoke();
            return;
        }
        _onAnalysisReady += onReady;
        if (_initializing) return;
        _initializing = true;
        Task.Run(() =>
        {
            try
            {
                var svc = AndroidAnalysisFactory.CreateTracking(this);
                _handler?.Post(() =>
                {
                    _analysis = svc;
                    _initializing = false;
                    var ready = _onAnalysisReady;
                    _onAnalysisReady = null;
                    ready?.Invoke();
                });
            }
            catch (Exception ex)
            {
                _handler?.Post(() =>
                {
                    _initializing = false;
                    _onAnalysisReady = null;
                    PostResult($"初始化失败: {ex.Message}");
                    SetOverlayColor(ColorErr);
                });
            }
        });
    }

    private Notification BuildNotification()
    {
        var channel = new NotificationChannel("assistant", "象棋助手", NotificationImportance.Low);
        var nm = (NotificationManager)GetSystemService(NotificationService)!;
        nm.CreateNotificationChannel(channel);
        return new Notification.Builder(this, "assistant")
            .SetContentTitle("象棋助手")
            .SetContentText("悬浮窗运行中，点「分析」获取最优着法")
            .SetSmallIcon(Android.Resource.Drawable.SymDefAppIcon)
            .SetOngoing(true)
            .Build();
    }

    private void ShowOverlay()
    {
        if (_overlay is not null || _wm is null) return;
        var density = Resources.DisplayMetrics.Density;

        _resultText = new ATextView(this)
        {
            Text = "就绪",
            TextSize = 16,
            Gravity = GravityFlags.Start,
        };
        _resultText.SetTextColor(AColor.ParseColor("#1a1a1a"));

        var close = new ATextView(this) { Text = "×", TextSize = 18, Gravity = GravityFlags.Center };
        close.SetTextColor(AColor.ParseColor("#666666"));
        close.Click += (_, _) => { StopSelf(); RemoveOverlay(); };

        // 按钮区：2×2 表格，左列（新局、我方），右列（分析，跨两行）。
        var newGame = new AButton(this) { Text = "新局", TextSize = 12 };
        newGame.SetAllCaps(false);
        newGame.Click += (_, _) => _handler?.Post(() => NewGame());

        _sideBtn = new AButton(this) { Text = "我方:红", TextSize = 12 };
        _sideBtn.SetAllCaps(false);
        _sideBtn.Click += (_, _) =>
        {
            _mySide = _mySide == "w" ? "b" : "w";
            _sideBtn.Text = _mySide == "w" ? "我方:红" : "我方:黑";
            _analysis?.Invalidate();
            PostResult("已切换我方，请重新点『新局』。");
        };

        var analyze = new AButton(this) { Text = "分析", TextSize = 20 };
        analyze.SetAllCaps(false);
        analyze.Click += (_, _) => _handler?.Post(() => CaptureAndAnalyze());

        var leftCol = new ALinearLayout(this) { Orientation = Orientation.Vertical };
        leftCol.AddView(newGame, new ALinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f));
        leftCol.AddView(_sideBtn, new ALinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f));

        _ctrlRow = new ALinearLayout(this) { Orientation = Orientation.Horizontal };
        _ctrlRow.AddView(leftCol, new ALinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        _ctrlRow.AddView(analyze, new ALinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

        var bg = new GradientDrawable();
        bg.SetColor(AColor.ParseColor(ColorIdle));
        bg.SetCornerRadius(12 * density);

        _overlay = new ALinearLayout(this) { Orientation = Orientation.Vertical, Background = bg };
        _overlay.SetPadding((int)(10 * density), (int)(8 * density), (int)(10 * density), (int)(8 * density));

        // 标题行（拖动 / 短按折叠）。
        var titleRow = new ALinearLayout(this) { Orientation = Orientation.Horizontal };
        titleRow.SetGravity(GravityFlags.CenterVertical);
        var title = new ATextView(this) { Text = "象棋助手 ▾", TextSize = 14 };
        title.SetTextColor(AColor.ParseColor("#444444"));
        titleRow.AddView(title, new ALinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        titleRow.AddView(close, new ALinearLayout.LayoutParams((int)(28 * density), (int)(28 * density)));
        _overlay.AddView(titleRow);

        // 分析结果（折叠时保留）。
        _overlay.AddView(_resultText);

        // 按钮区（折叠时整体隐藏）。
        _overlay.AddView(_ctrlRow);

        // 标题栏：拖动悬浮窗；松手时若未拖动则折叠/展开按钮区。
        titleRow.SetOnTouchListener(new DragOrTapListener(_wm!, _overlay!, () =>
        {
            _collapsed = !_collapsed;
            title.Text = _collapsed ? "象棋助手 ▸" : "象棋助手 ▾";
            if (_ctrlRow is not null) _ctrlRow.Visibility = _collapsed ? ViewStates.Gone : ViewStates.Visible;
        }));

        var lp = new WindowManagerLayoutParams(
            (int)(250 * density),
            ViewGroup.LayoutParams.WrapContent,
            WindowManagerTypes.ApplicationOverlay,
            WindowManagerFlags.NotFocusable | WindowManagerFlags.NotTouchModal | WindowManagerFlags.LayoutInScreen,
            Android.Graphics.Format.Translucent)
        {
            Gravity = GravityFlags.Top | GravityFlags.Start,
            X = (int)(16 * density),
            Y = (int)(60 * density),
        };
        try
        {
            _wm.AddView(_overlay, lp);
        }
        catch (Exception ex)
        {
            PostResult($"悬浮窗失败: {ex.Message}");
        }
    }

    private void RemoveOverlay()
    {
        if (_overlay is not null && _wm is not null)
        {
            try { _wm.RemoveView(_overlay); } catch { /* already gone */ }
            _overlay = null;
        }
    }

    private void CaptureAndAnalyze()
    {
        if (_busy) return;
        if (_reader is null || _virtualDisplay is null)
        {
            PostResult("录屏权限未就绪");
            SetOverlayColor(ColorErr);
            return;
        }
        if (_analysis is null)
        {
            // 引擎初始化中：背景变黄，等初始化完成自动补一次分析（不显示“识别中”文本）
            EnsureAnalysis(() => _handler?.Post(() => { if (!_busy) CaptureAndAnalyze(); }));
            SetOverlayColor(ColorBusy);
            return;
        }

        _busy = true;
        SetOverlayColor(ColorBusy); // 识别中：仅背景变黄，不改文本
        Task.Run(() =>
        {
            try
            {
                using var bmp = CaptureFrame();
                var result = _analysis.Analyze(bmp, movetimeMs: 5000);
                PostResult(FormatTracking(result));
                SetOverlayColor(ColorFor(result));
            }
            catch (Exception ex)
            {
                PostResult($"失败: {ex.Message}");
                SetOverlayColor(ColorErr);
            }
            finally { _busy = false; }
        });
    }

    private static string FormatTracking(TrackingResult r)
    {
        if (r.Result == TrackerMatch.Kind.LostTrack)
            return r.Reason ?? "跟踪失败，请点『新局』重置后重试。";
        var lines = new List<string>();
        if (!string.IsNullOrEmpty(r.MoveSummary)) lines.Add(r.MoveSummary);
        if (!string.IsNullOrEmpty(r.Advice)) lines.Add(r.Advice);
        return lines.Count > 0 ? string.Join("\n", lines) : "就绪";
    }

    private static string ColorFor(TrackingResult r)
    {
        if (r.Result == TrackerMatch.Kind.LostTrack)
            return (r.Reason?.Contains("移动") ?? false) ? ColorBusy : ColorErr;
        return ColorOk;
    }

    private void NewGame()
    {
        if (_busy) return;
        if (_reader is null || _virtualDisplay is null)
        {
            PostResult("录屏权限未就绪");
            SetOverlayColor(ColorErr);
            return;
        }
        if (_analysis is null)
        {
            EnsureAnalysis(() => _handler?.Post(() => NewGame()));
            SetOverlayColor(ColorBusy);
            return;
        }
        _busy = true;
        SetOverlayColor(ColorBusy);
        Task.Run(() =>
        {
            try
            {
                using var bmp = CaptureFrame();
                var msg = _analysis.Reset(bmp, _mySide == "b", JjCalibration.Instance);
                PostResult(msg);
                SetOverlayColor(ColorOk);
            }
            catch (Exception ex)
            {
                PostResult($"新局失败: {ex.Message}");
                SetOverlayColor(ColorErr);
            }
            finally { _busy = false; }
        });
    }

    private void SetupCapture()
    {
        // Android 14+ allows createVirtualDisplay only ONCE per MediaProjection instance,
        // so create a long-lived VirtualDisplay + ImageReader up front and reuse them for
        // every capture (instead of re-creating per frame, which throws
        // "Don't take multiple captures by invoking createVirtualDisplay multiple times").
        if (_projection is null || _wm is null || _virtualDisplay is not null) return;
        var display = _wm.DefaultDisplay!;
        var metrics = new DisplayMetrics();
        display.GetRealMetrics(metrics);
        _captureW = metrics.WidthPixels;
        _captureH = metrics.HeightPixels;
        var dpi = (int)metrics.DensityDpi;
        _reader = Android.Media.ImageReader.NewInstance(
            _captureW, _captureH, (Android.Graphics.ImageFormatType)1 /* PixelFormat.RGBA_8888 */, 4);
        _virtualDisplay = _projection.CreateVirtualDisplay(
            "assistant-capture", _captureW, _captureH, dpi, (DisplayFlags)0, _reader.Surface, null, null);
    }

    private SKBitmap CaptureFrame()
    {
        if (_reader is null || _virtualDisplay is null)
            throw new InvalidOperationException("录屏未就绪");
        var w = _captureW;
        var h = _captureH;

        MImage? image = null;
        try
        {
            var deadline = Java.Lang.JavaSystem.CurrentTimeMillis() + 800;
            while (Java.Lang.JavaSystem.CurrentTimeMillis() < deadline)
            {
                image = _reader.AcquireLatestImage();
                if (image is not null) break;
                Thread.Sleep(50);
            }
            if (image is null)
                throw new InvalidOperationException("截图超时");
            var plane = image.GetPlanes()[0];
            var buffer = plane.Buffer;
            var rowStride = plane.RowStride;
            var pixelStride = plane.PixelStride;
            var bytes = new byte[w * h * 4];
            var buf = new byte[rowStride];
            for (var y = 0; y < h; y++)
            {
                buffer.Position(y * rowStride);
                buffer.Get(buf, 0, rowStride);
                if (pixelStride == 4 && rowStride == w * 4)
                {
                    Array.Copy(buf, 0, bytes, y * w * 4, w * 4);
                }
                else
                {
                    for (var x = 0; x < w; x++)
                    {
                        var si = x * pixelStride;
                        bytes[(y * w + x) * 4] = buf[si];
                        bytes[(y * w + x) * 4 + 1] = buf[si + 1];
                        bytes[(y * w + x) * 4 + 2] = buf[si + 2];
                        bytes[(y * w + x) * 4 + 3] = 255;
                    }
                }
            }

            var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            var bmp = new SKBitmap(info);
            var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            bmp.InstallPixels(info, handle.AddrOfPinnedObject(), info.RowBytes,
                (addr, ctx) => handle.Free(), null);
            return bmp;
        }
        finally
        {
            // 关键：显式 Close 释放 ImageReader 槽位，否则第 3 次获取报
            // "maximages(4) has already been acquired, call #close before acquiring more"
            try { image?.Close(); } catch { /* ignore */ }
            try { image?.Dispose(); } catch { /* ignore */ }
        }
    }

    private void PostResult(string text)
    {
        _handler?.Post(() =>
        {
            if (_resultText is not null)
                _resultText.Text = text;
        });
    }

    private void SetOverlayColor(string hex)
    {
        _handler?.Post(() =>
        {
            if (_overlay?.Background is GradientDrawable g)
                g.SetColor(AColor.ParseColor(hex));
        });
    }

    public override IBinder? OnBind(Intent? intent) => null;

    public override void OnDestroy()
    {
        _initializing = false;
        _onAnalysisReady = null;
        RemoveOverlay();
        _analysis?.Dispose();
        _analysis = null;
        try { _virtualDisplay?.Release(); } catch { /* ignore */ }
        try { _reader?.Close(); } catch { /* ignore */ }
        _virtualDisplay = null;
        _reader = null;
        try { _projection?.UnregisterCallback(_projectionCallback); } catch { /* ignore */ }
        _projection?.Stop();
        _projection = null;
        base.OnDestroy();
    }

    /// <summary>Android 14+ requires a registered callback before MediaProjection capture.</summary>
    private sealed class ProjectionCallback : MediaProjection.Callback
    {
        public override void OnStop() { }
    }

    /// <summary>标题栏触摸：拖动悬浮窗；松手时若未发生拖动则视为短按，触发回调。</summary>
    private sealed class DragOrTapListener : Java.Lang.Object, AView.IOnTouchListener
    {
        private readonly IWindowManager _wm;
        private readonly AView _view;
        private readonly Action _onTap;
        private int _initX, _initY;
        private float _initX0, _initY0;
        private bool _dragged;
        private const int Slop = 12;

        public DragOrTapListener(IWindowManager wm, AView view, Action onTap)
        {
            _wm = wm; _view = view; _onTap = onTap;
        }

        public bool OnTouch(AView? v, MotionEvent? e)
        {
            if (e is null) return false;
            switch (e.Action)
            {
                case MotionEventActions.Down:
                    _initX = ((WindowManagerLayoutParams)_view.LayoutParameters!).X;
                    _initY = ((WindowManagerLayoutParams)_view.LayoutParameters!).Y;
                    _initX0 = e.RawX;
                    _initY0 = e.RawY;
                    _dragged = false;
                    return true;
                case MotionEventActions.Move:
                    var dx = e.RawX - _initX0;
                    var dy = e.RawY - _initY0;
                    if (!_dragged && (Math.Abs(dx) > Slop || Math.Abs(dy) > Slop))
                        _dragged = true;
                    if (_dragged)
                    {
                        var lp = (WindowManagerLayoutParams)_view.LayoutParameters!;
                        lp.X = _initX + (int)dx;
                        lp.Y = _initY + (int)dy;
                        try { _wm.UpdateViewLayout(_view, lp); } catch { /* ignore */ }
                    }
                    return true;
                case MotionEventActions.Up:
                    if (!_dragged) _onTap();
                    return true;
                default:
                    return false;
            }
        }
    }
}
