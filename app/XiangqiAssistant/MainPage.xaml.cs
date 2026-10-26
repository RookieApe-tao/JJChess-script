namespace XiangqiAssistant;

public partial class MainPage : ContentPage
{
    private GameMode _pendingMode = GameMode.Xiangqi;

    public MainPage()
    {
        InitializeComponent();
    }

    private async void OnStartClicked(object? sender, EventArgs e) => Start(GameMode.Xiangqi);
    private async void OnStartGomokuClicked(object? sender, EventArgs e) => Start(GameMode.Gomoku);

    private async void Start(GameMode mode)
    {
        _pendingMode = mode;
        StartBtn.IsEnabled = false;
        StartGomokuBtn.IsEnabled = false;
        StatusLabel.Text = "正在检查权限…";

        var ok = await AssistantStarter.PrepareAsync(mode);
        if (ok)
        {
            var target = mode == GameMode.Gomoku ? "JJ 五子棋（先点悬浮窗的「画框」框住棋盘，再点「分析」）" : "JJ 象棋";
            StatusLabel.Text = $"悬浮窗已启动。切到 {target} 即可。";
            StartBtn.IsVisible = false;
            StartGomokuBtn.IsVisible = false;
            StopBtn.IsVisible = true;
        }
        else
        {
            StatusLabel.Text = "权限未完成，请重试（悬浮窗/录屏/通知 三项权限）。";
            StartBtn.IsEnabled = true;
            StartGomokuBtn.IsEnabled = true;
        }
    }

    private void OnStopClicked(object? sender, EventArgs e)
    {
        AssistantStarter.Stop();
        StatusLabel.Text = "已停止悬浮窗。";
        StartBtn.IsVisible = true;
        StartGomokuBtn.IsVisible = true;
        StopBtn.IsVisible = false;
        StartBtn.IsEnabled = true;
        StartGomokuBtn.IsEnabled = true;
    }
}
