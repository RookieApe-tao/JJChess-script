namespace XiangqiAssistant;

public partial class MainPage : ContentPage
{
    public MainPage()
    {
        InitializeComponent();
    }

    private async void OnStartClicked(object? sender, EventArgs e)
    {
        StartBtn.IsEnabled = false;
        StatusLabel.Text = "正在检查权限…";

        var ok = await AssistantStarter.PrepareAsync();
        if (ok)
        {
            StatusLabel.Text = "悬浮窗已启动。切到 JJ 象棋，点悬浮窗的「分析」即可。";
            StartBtn.IsVisible = false;
            StopBtn.IsVisible = true;
        }
        else
        {
            StatusLabel.Text = "权限未完成，请重试（悬浮窗/录屏/通知 三项权限）。";
            StartBtn.IsEnabled = true;
        }
    }

    private void OnStopClicked(object? sender, EventArgs e)
    {
        AssistantStarter.Stop();
        StatusLabel.Text = "已停止悬浮窗。";
        StartBtn.IsVisible = true;
        StopBtn.IsVisible = false;
        StartBtn.IsEnabled = true;
    }
}
