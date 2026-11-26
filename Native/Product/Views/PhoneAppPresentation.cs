#if SONICROUTE_PHONE
using SonicRoute.AndroidAudio;

namespace SonicRoute.Native.Product.Views;

internal static class PhoneAppPresentation
{
    private static int PlatformCategory(AndroidApp app) => app.PlatformCategory >= 0 ? app.PlatformCategory : app.Package switch
    {
        "cn.wenyu.bodian" or "com.netease.cloudmusic" or "com.tencent.qqmusic" => 1,
        "tv.danmaku.bili" or "com.ss.android.ugc.aweme" => 2,
        "com.tencent.mm" or "com.tencent.mobileqq" => 4,
        "com.miHoYo.Yuanshen" => 0,
        _ => -1
    };

    internal static string Category(AndroidApp app) => PlatformCategory(app) switch
    {
        0 => "games",
        1 or 2 or 3 => "media",
        4 => "social",
        _ => app.Category
    };

    internal static string Description(AndroidApp app) => app.IsDeviceAudio ? L10n.T("Phone.DeviceAudioDescription") : L10n.T(PlatformCategory(app) switch
    {
        1 => "Phone.Cat.MediaMusic",
        2 => "Phone.Cat.MediaVideo",
        3 => "Phone.Cat.MediaImage",
        _ => Category(app) switch
        {
            "media" => "Phone.Cat.Media",
            "games" => "Phone.Cat.GameAudio",
            "social" => "Phone.Cat.SocialChat",
            _ => "Phone.Cat.OtherApp"
        }
    });
}
#endif
