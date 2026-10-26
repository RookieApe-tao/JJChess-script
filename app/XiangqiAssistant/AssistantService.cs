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
using XiangqiCore.Gomoku;
using XiangqiCore.Recognition;
using Android.Graphics;
using AButton = Android.Widget.Button;
using AColor = Android.Graphics.Color;
using ALinearLayout = Android.Widget.LinearLayout;
using APaint = Android.Graphics.Paint;
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
    public const string ExtraMode = "mode";

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
    private IGameAdvisor? _advisor;
    private volatile bool _busy;
    private volatile bool _initializing;
    private Action? _onAnalysisReady;
    private string _mySide = "w";        // 象棋: "w"=红/我方, "b"=黑/我方;五子棋: "w"=白, "b"=黑
    private bool _collapsed;
    private ALinearLayout? _ctrlRow;
    private AButton? _sideBtn;

    private GameMode _mode = GameMode.Xiangqi;
    private AView? _fullScreenView;      // 画框/绿圈等全屏 overlay(同一时刻最多一个)

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
            _mode = intent.GetStringExtra(ExtraMode) == "gomoku" ? GameMode.Gomoku : GameMode.Xiangqi;
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
        if (_advisor is not null)
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
                var svc = AndroidAnalysisFactory.CreateAdvisor(this, _mode);
                _handler?.Post(() =>
                {
                    _advisor = svc;
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
        var title = _mode == GameMode.Gomoku ? "五子棋助手" : "象棋助手";
        var channel = new NotificationChannel("assistant", title, NotificationImportance.Low);
        var nm = (NotificationManager)GetSystemService(NotificationService)!;
        nm.CreateNotificationChannel(channel);
        return new Notification.Builder(this, "assistant")
            .SetContentTitle(title)
            .SetContentText("悬浮窗运行中，点「分析」获取最优着法")
            .SetSmallIcon(Android.Resource.Drawable.SymDefAppIcon)
            .SetOngoing(true)
            .Build();
    }

    private void ShowOverlay()
    {
        if (_overlay is not null || _wm is null) return;
        var density = Resources.DisplayMetrics.Density;
        var titleText = _mode == GameMode.Gomoku ? "五子棋助手 ▾" : "象棋助手 ▾";

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

        // 按钮区:象棋 = 新局/我方:红 | 应急 | 分析/记录;五子棋 = 画框/我方:黑 | 新局(清盘) | 分析。
        AButton newGame;
        AButton mid;   // 中列大按钮:象棋=应急,五子棋=新局(跨两行)
        AButton analyze;
        ALinearLayout leftCol;
        ALinearLayout rightCol;

        if (_mode == GameMode.Gomoku)
        {
            var drawRect = new AButton(this) { Text = "画框", TextSize = 12 };
            drawRect.SetAllCaps(false);
            drawRect.Click += (_, _) => _handler?.Post(() => StartDrawRect());

            _sideBtn = new AButton(this) { Text = "我方:黑", TextSize = 12 };
            _sideBtn.SetAllCaps(false);
            _sideBtn.Click += (_, _) =>
            {
                _mySide = _mySide == "w" ? "b" : "w";
                _sideBtn.Text = _mySide == "w" ? "我方:白" : "我方:黑";
                if (_advisor is GomokuAdvisor g)
                    g.MyColor = _mySide == "w" ? GomokuStone.White : GomokuStone.Black;
                _advisor?.Invalidate();
                PostResult("已切换我方执子。");
            };

            mid = new AButton(this) { Text = "新局", TextSize = 14 };
            mid.SetAllCaps(false);
            mid.Click += (_, _) => _handler?.Post(() => NewGame());

            analyze = new AButton(this) { Text = "分析", TextSize = 14 };
            analyze.SetAllCaps(false);
            analyze.Click += (_, _) => _handler?.Post(() => CaptureAndAnalyze());

            leftCol = new ALinearLayout(this) { Orientation = Orientation.Vertical };
            leftCol.AddView(drawRect, new ALinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f));
            leftCol.AddView(_sideBtn, new ALinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f));

            rightCol = new ALinearLayout(this) { Orientation = Orientation.Vertical };
            rightCol.AddView(analyze, new ALinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f));
            rightCol.AddView(new AView(this), new ALinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f));
        }
        else
        {
            newGame = new AButton(this) { Text = "新局", TextSize = 12 };
            newGame.SetAllCaps(false);
            newGame.Click += (_, _) => _handler?.Post(() => NewGame());

            _sideBtn = new AButton(this) { Text = "我方:红", TextSize = 12 };
            _sideBtn.SetAllCaps(false);
            _sideBtn.Click += (_, _) =>
            {
                _mySide = _mySide == "w" ? "b" : "w";
                _sideBtn.Text = _mySide == "w" ? "我方:红" : "我方:黑";
                if (_advisor is XiangqiAdvisor x) x.MySideBlack = _mySide == "b";
                _advisor?.Invalidate();
                PostResult("已切换我方，请重新点『新局』。");
            };

            analyze = new AButton(this) { Text = "分析", TextSize = 14 };
            analyze.SetAllCaps(false);
            analyze.Click += (_, _) => _handler?.Post(() => CaptureAndAnalyze());

            var record = new AButton(this) { Text = "记录", TextSize = 14 };
            record.SetAllCaps(false);
            record.Click += (_, _) => _handler?.Post(() => CaptureAndRecord());

            mid = new AButton(this) { Text = "应急", TextSize = 14 };
            mid.SetAllCaps(false);
            mid.Click += (_, _) => _handler?.Post(() => CaptureAndResync());

            leftCol = new ALinearLayout(this) { Orientation = Orientation.Vertical };
            leftCol.AddView(newGame, new ALinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f));
            leftCol.AddView(_sideBtn, new ALinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f));

            rightCol = new ALinearLayout(this) { Orientation = Orientation.Vertical };
            rightCol.AddView(analyze, new ALinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f));
            rightCol.AddView(record, new ALinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f));
        }

        _ctrlRow = new ALinearLayout(this) { Orientation = Orientation.Horizontal };
        _ctrlRow.AddView(leftCol, new ALinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        _ctrlRow.AddView(mid, new ALinearLayout.LayoutParams(0, ViewGroup.LayoutParams.MatchParent, 1f));
        _ctrlRow.AddView(rightCol, new ALinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

        var bg = new GradientDrawable();
        bg.SetColor(AColor.ParseColor(ColorIdle));
        bg.SetCornerRadius(12 * density);

        _overlay = new ALinearLayout(this) { Orientation = Orientation.Vertical, Background = bg };
        _overlay.SetPadding((int)(10 * density), (int)(8 * density), (int)(10 * density), (int)(8 * density));

        // 标题行（拖动 / 短按折叠）。
        var titleRow = new ALinearLayout(this) { Orientation = Orientation.Horizontal };
        titleRow.SetGravity(GravityFlags.CenterVertical);
        var title = new ATextView(this) { Text = titleText, TextSize = 14 };
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
            title.Text = _collapsed
                ? (_mode == GameMode.Gomoku ? "五子棋助手 ▸" : "象棋助手 ▸")
                : (_mode == GameMode.Gomoku ? "五子棋助手 ▾" : "象棋助手 ▾");
            if (_ctrlRow is not null) _ctrlRow.Visibility = _collapsed ? ViewStates.Gone : ViewStates.Visible;
        }));

        var lp = new WindowManagerLayoutParams(
            (int)(320 * density),
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
        if (_advisor is null)
        {
            // 引擎初始化中：背景变黄，等初始化完成自动补一次分析
            EnsureAnalysis(() => _handler?.Post(() => { if (!_busy) CaptureAndAnalyze(); }));
            SetOverlayColor(ColorBusy);
            PostResult("初始化中....");
            return;
        }

        _busy = true;
        SetOverlayColor(ColorBusy);
        PostResult("分析中...."); // 提示正在分析，避免用户以为没点上
        Task.Run(async () =>
        {
            try
            {
                using var bmp = CaptureFrame();
                var reply = _advisor.Analyze(bmp, timeMs: 5000);

                // 失败自动重试一次（延迟 400ms 吃掉瞬态动画/落子动画）
                if (reply.Kind == ReplyKind.Retry)
                {
                    await Task.Delay(400);
                    using var bmp2 = CaptureFrame();
                    reply = _advisor.Analyze(bmp2, timeMs: 5000);
                }

                PostResult(reply.Text);
                SetOverlayColor(ColorFor(reply));
                if (reply.Marker is { } m && _mode == GameMode.Gomoku)
                    ShowMarkerCircle(m.X, m.Y);
            }
            catch (Exception ex)
            {
                PostResult($"失败: {ex.Message}");
                SetOverlayColor(ColorErr);
            }
            finally { _busy = false; }
        });
    }

    /// <summary>「记录」（仅象棋）：只跟踪走子、累计手数，不跑引擎。</summary>
    private void CaptureAndRecord()
    {
        if (_busy) return;
        if (_reader is null || _virtualDisplay is null)
        {
            PostResult("录屏权限未就绪");
            SetOverlayColor(ColorErr);
            return;
        }
        if (_advisor is null)
        {
            EnsureAnalysis(() => _handler?.Post(() => { if (!_busy) CaptureAndRecord(); }));
            SetOverlayColor(ColorBusy);
            return;
        }
        if (_advisor is not XiangqiAdvisor xiangqi) return;

        _busy = true;
        SetOverlayColor(ColorBusy);
        Task.Run(async () =>
        {
            try
            {
                using var bmp = CaptureFrame();
                var result = xiangqi.TrackOnly(bmp);

                // 失败自动重试一次（延迟 400ms 吃掉瞬态动画）
                if (result.Result == XiangqiCore.TrackerMatch.Kind.LostTrack)
                {
                    await Task.Delay(400);
                    using var bmp2 = CaptureFrame();
                    result = xiangqi.TrackOnly(bmp2);
                }

                PostResult(FormatTracking(result));
                SetOverlayColor(ColorFor(result));
            }
            catch (Exception ex)
            {
                PostResult($"记录失败: {ex.Message}");
                SetOverlayColor(ColorErr);
            }
            finally { _busy = false; }
        });
    }

    /// <summary>「应急」（仅象棋）：记录混乱时全量识别当前画面，重建跟踪基线（手数清零）。</summary>
    private void CaptureAndResync()
    {
        if (_busy) return;
        if (_reader is null || _virtualDisplay is null)
        {
            PostResult("录屏权限未就绪");
            SetOverlayColor(ColorErr);
            return;
        }
        if (_advisor is null)
        {
            EnsureAnalysis(() => _handler?.Post(() => { if (!_busy) CaptureAndResync(); }));
            SetOverlayColor(ColorBusy);
            PostResult("初始化中....");
            return;
        }
        if (_advisor is not XiangqiAdvisor xiangqi) return;

        _busy = true;
        SetOverlayColor(ColorBusy);
        PostResult("应急重校中...."); // 全量识别 + 引擎，需数秒
        Task.Run(() =>
        {
            try
            {
                using var bmp = CaptureFrame();
                var (ok, msg) = xiangqi.Resync(bmp, _mySide == "b", JjCalibration.Instance, movetimeMs: 5000);
                PostResult(msg);
                SetOverlayColor(ok ? ColorOk : ColorErr);
            }
            catch (Exception ex)
            {
                PostResult($"应急重校失败: {ex.Message}");
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
        if (!string.IsNullOrEmpty(r.MoveCountText)) lines.Add(r.MoveCountText);
        if (!string.IsNullOrEmpty(r.Advice)) lines.Add(r.Advice);
        return lines.Count > 0 ? string.Join("\n", lines) : "就绪";
    }

    private static string ColorFor(TrackingResult r)
    {
        if (r.Result == TrackerMatch.Kind.LostTrack)
            return (r.Reason?.Contains("移动") ?? false) ? ColorBusy : ColorErr;
        return ColorOk;
    }

    private static string ColorFor(OverlayReply r) => r.Kind switch
    {
        ReplyKind.Ok => ColorOk,
        ReplyKind.Busy or ReplyKind.Retry => ColorBusy,
        _ => ColorErr,
    };

    private void NewGame()
    {
        if (_busy) return;
        if (_reader is null || _virtualDisplay is null)
        {
            PostResult("录屏权限未就绪");
            SetOverlayColor(ColorErr);
            return;
        }
        if (_advisor is null)
        {
            EnsureAnalysis(() => _handler?.Post(() => NewGame()));
            SetOverlayColor(ColorBusy);
            return;
        }

        if (_mode == GameMode.Gomoku)
        {
            // 五子棋「新局」= 清盘记账(无需截图,但保持异步形态一致)
            _busy = true;
            SetOverlayColor(ColorBusy);
            Task.Run(() =>
            {
                try
                {
                    using var bmp = CaptureFrame();
                    var reply = _advisor.NewGame(bmp);
                    PostResult(reply.Text);
                    SetOverlayColor(ColorOk);
                }
                catch (Exception ex)
                {
                    PostResult($"新局失败: {ex.Message}");
                    SetOverlayColor(ColorErr);
                }
                finally { _busy = false; }
            });
            return;
        }

        _busy = true;
        SetOverlayColor(ColorBusy);
        Task.Run(() =>
        {
            try
            {
                using var bmp = CaptureFrame();
                var reply = _advisor.NewGame(bmp);
                PostResult(reply.Text);
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

    // ---------- 五子棋画框 ----------

    /// <summary>进入画框模式:隐藏悬浮窗,全屏拖矩形。</summary>
    private void StartDrawRect()
    {
        if (_wm is null || _fullScreenView is not null) return;
        RemoveOverlay();

        var view = new DrawRectView(this);
        var lp = new WindowManagerLayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent,
            WindowManagerTypes.ApplicationOverlay,
            WindowManagerFlags.NotFocusable | WindowManagerFlags.LayoutInScreen,
            Android.Graphics.Format.Translucent);
        view.RectDrawn += rect => _handler?.Post(() => FinishDrawRect(rect));
        _fullScreenView = view;
        try
        {
            _wm.AddView(view, lp);
            PostResultToStatus("在屏幕上拖一个矩形框住五子棋棋盘…");
        }
        catch (Exception ex)
        {
            _fullScreenView = null;
            ShowOverlay();
            PostResult($"画框失败: {ex.Message}");
            SetOverlayColor(ColorErr);
        }
    }

    private void FinishDrawRect((float X1, float Y1, float X2, float Y2)? rect)
    {
        RemoveFullScreenView();
        ShowOverlay();
        if (rect is null)
        {
            PostResult("已取消画框。");
            return;
        }
        var r = rect.Value;
        if (Math.Abs(r.X2 - r.X1) < _captureW * 0.2f || Math.Abs(r.Y2 - r.Y1) < _captureW * 0.2f)
        {
            PostResult("画的框太小,已放弃。请重新点『画框』,框住整个棋盘。");
            SetOverlayColor(ColorErr);
            return;
        }

        if (_advisor is null)
        {
            EnsureAnalysis(() => _handler?.Post(() => FinishDrawRect(rect)));
            SetOverlayColor(ColorBusy);
            PostResult("初始化中....");
            return;
        }
        if (_advisor is not GomokuAdvisor gomoku) return;

        _busy = true;
        SetOverlayColor(ColorBusy);
        PostResult("标定中....");
        Task.Run(() =>
        {
            try
            {
                using var bmp = CaptureFrame();
                var reply = gomoku.Calibrate(bmp, r.X1, r.Y1, r.X2, r.Y2);
                PostResult(reply.Text);
                SetOverlayColor(reply.Kind == ReplyKind.Ok ? ColorOk : ColorErr);
            }
            catch (Exception ex)
            {
                PostResult($"标定失败: {ex.Message}");
                SetOverlayColor(ColorErr);
            }
            finally { _busy = false; }
        });
    }

    private void RemoveFullScreenView()
    {
        if (_fullScreenView is not null && _wm is not null)
        {
            try { _wm.RemoveView(_fullScreenView); } catch { /* already gone */ }
            _fullScreenView = null;
        }
    }

    // ---------- 五子棋绿圈标记 ----------

    /// <summary>在建议落点画绿圈,约 5 秒后自动消失。</summary>
    private void ShowMarkerCircle(float x, float y)
    {
        _handler?.Post(() =>
        {
            if (_wm is null) return;
            // 与画框共用 _fullScreenView 槽位:绿圈期间不允许画框(几乎不会冲突)
            RemoveFullScreenView();
            var view = new MarkerView(this, x, y);
            var lp = new WindowManagerLayoutParams(
                ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent,
                WindowManagerTypes.ApplicationOverlay,
                WindowManagerFlags.NotFocusable | WindowManagerFlags.NotTouchable | WindowManagerFlags.LayoutInScreen,
                Android.Graphics.Format.Translucent);
            _fullScreenView = view;
            try
            {
                _wm.AddView(view, lp);
                _handler?.PostDelayed(() => { if (_fullScreenView == view) RemoveFullScreenView(); }, 5000);
            }
            catch { /* 显示失败不影响建议文本 */ }
        });
    }

    /// <summary>画框期间悬浮窗已隐藏,结果文本暂存,恢复后显示。</summary>
    private void PostResultToStatus(string text) => PostResult(text);

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
        RemoveFullScreenView();
        RemoveOverlay();
        _advisor?.Dispose();
        _advisor = null;
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

    /// <summary>全屏画框视图:按下→拖动→松开得到矩形;轻点 = 取消。</summary>
    private sealed class DrawRectView : AView
    {
        private float? _startX, _startY;
        private float _curX, _curY;
        private readonly APaint _stroke = new() { Color = AColor.Rgb(0, 200, 83), StrokeWidth = 6f, AntiAlias = true };
        private readonly APaint _fill = new() { Color = AColor.Argb(40, 0, 200, 83) };
        private bool _dragging;

        public event Action<(float X1, float Y1, float X2, float Y2)?>? RectDrawn;

        public DrawRectView(Context context) : base(context)
        {
            _stroke.SetStyle(APaint.Style.Stroke);
            _fill.SetStyle(APaint.Style.Fill);
        }

        public override bool OnTouchEvent(MotionEvent? e)
        {
            if (e is null) return false;
            switch (e.Action)
            {
                case MotionEventActions.Down:
                    _startX = e.RawX; _startY = e.RawY;
                    _curX = e.RawX; _curY = e.RawY;
                    _dragging = false;
                    return true;
                case MotionEventActions.Move:
                    _curX = e.RawX; _curY = e.RawY;
                    if (Math.Abs(_curX - _startX!.Value) > 20 || Math.Abs(_curY - _startY!.Value) > 20)
                        _dragging = true;
                    Invalidate();
                    return true;
                case MotionEventActions.Up:
                    if (!_dragging)
                        RectDrawn?.Invoke(null); // 轻点取消
                    else
                        RectDrawn?.Invoke((_startX!.Value, _startY!.Value, _curX, _curY));
                    return true;
                default:
                    return false;
            }
        }

        protected override void OnDraw(Canvas? canvas)
        {
            base.OnDraw(canvas);
            if (_startX is null || !_dragging) return;
            var left = Math.Min(_startX.Value, _curX);
            var top = Math.Min(_startY!.Value, _curY);
            var right = Math.Max(_startX.Value, _curX);
            var bottom = Math.Max(_startY.Value, _curY);
            canvas?.DrawRect(left, top, right, bottom, _fill);
            canvas?.DrawRect(left, top, right, bottom, _stroke);
        }
    }

    /// <summary>全屏绿圈标记视图(不拦截触摸)。</summary>
    private sealed class MarkerView : AView
    {
        private readonly float _x, _y;
        private readonly APaint _stroke = new() { Color = AColor.Rgb(0, 200, 83), StrokeWidth = 8f, AntiAlias = true };

        public MarkerView(Context context, float x, float y) : base(context)
        {
            _x = x; _y = y;
            _stroke.SetStyle(APaint.Style.Stroke);
        }

        protected override void OnDraw(Canvas? canvas)
        {
            base.OnDraw(canvas);
            canvas?.DrawCircle(_x, _y, 34f, _stroke);
        }
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
