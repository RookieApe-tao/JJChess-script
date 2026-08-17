using Android.App;
using Android.Content;
using Android.OS;
using Android.Provider;
using AndroidX.Core.App;

namespace XiangqiAssistant;

public static class AssistantStarter
{
    public static async Task<bool> PrepareAsync()
    {
        var activity = Platform.CurrentActivity;
        if (activity is null) return false;

        if (!Settings.CanDrawOverlays(activity))
        {
            var intent = new Intent(Settings.ActionManageOverlayPermission,
                Android.Net.Uri.Parse("package:" + activity.PackageName));
            activity.StartActivity(intent);
            return false;
        }

        if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu &&
            ActivityCompat.CheckSelfPermission(activity, Android.Manifest.Permission.PostNotifications)
                != Android.Content.PM.Permission.Granted)
        {
            ActivityCompat.RequestPermissions(activity,
                new[] { Android.Manifest.Permission.PostNotifications }, 2002);
        }

        if (activity is MainActivity main)
            main.RequestScreenCapture();
        return true;
    }

    public static void Stop()
    {
        var ctx = Platform.AppContext;
        ctx.StopService(new Intent(ctx, typeof(AssistantService)));
    }
}
