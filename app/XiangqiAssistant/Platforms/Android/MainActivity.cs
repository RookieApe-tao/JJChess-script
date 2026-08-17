using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Media.Projection;
using Android.OS;

namespace XiangqiAssistant;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    private const int CaptureRequestCode = 1001;

    public void RequestScreenCapture()
    {
        var mpm = (MediaProjectionManager)GetSystemService(MediaProjectionService)!;
        StartActivityForResult(mpm.CreateScreenCaptureIntent(), CaptureRequestCode);
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        if (requestCode == CaptureRequestCode && resultCode == Result.Ok && data is not null)
        {
            var intent = new Intent(this, typeof(AssistantService));
            intent.SetAction(AssistantService.ActionStart);
            intent.PutExtra(AssistantService.ExtraCode, (int)resultCode);
            intent.PutExtra(AssistantService.ExtraData, data);
            StartForegroundService(intent);
        }
        base.OnActivityResult(requestCode, resultCode, data);
    }
}
