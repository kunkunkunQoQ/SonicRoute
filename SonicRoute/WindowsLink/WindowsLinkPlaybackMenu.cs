#if SONICROUTE_PHONE
using System;
using System.Threading.Tasks;
using SonicRoute.WindowsLink;
using ContextMenu = System.Windows.Controls.ContextMenu;

namespace SonicRoute;

internal static class WindowsLinkPlaybackMenu
{
    internal static ContextMenu Create(string destinationOnlyTitle, WindowsLinkExclusiveMode mode, bool shared,
        Func<bool, bool, Task> play, Func<Task<WindowsLinkExclusiveMode>>? refreshExclusive = null)
    {
        var menu = ConvenienceMenus.Create();
        var only = ConvenienceMenus.Item(destinationOnlyTitle);
        void SetExclusive(WindowsLinkExclusiveMode current)
        {
            mode = current;
            only.IsEnabled = current != WindowsLinkExclusiveMode.Unavailable;
            only.Header = current switch
            {
                WindowsLinkExclusiveMode.DedicatedOrSpeaker => string.Format(L10n.T("Phone.WindowsLinkMuteIfNeededAction"), destinationOnlyTitle),
                WindowsLinkExclusiveMode.MuteSourceSpeaker => string.Format(L10n.T("Phone.WindowsLinkMuteSpeakerAction"), destinationOnlyTitle),
                _ => destinationOnlyTitle
            };
            only.ToolTip = current switch
            {
                WindowsLinkExclusiveMode.Unavailable => L10n.T("Phone.WindowsLinkExclusiveUnavailable"),
                WindowsLinkExclusiveMode.MuteSourceSpeaker => L10n.T("Phone.WindowsLinkMuteSpeakerDesc"),
                WindowsLinkExclusiveMode.DedicatedOrSpeaker => L10n.T("Phone.WindowsLinkMuteIfNeededDesc"),
                _ => null
            };
        }

        SetExclusive(mode);
        System.Windows.Controls.ToolTipService.SetShowOnDisabled(only, true);
        if (refreshExclusive != null)
            menu.Opened += async (_, _) => SetExclusive(await refreshExclusive());
        only.Click += async (_, _) => await play(true, mode.HasFlag(WindowsLinkExclusiveMode.MuteSourceSpeaker));
        menu.Items.Add(only);
        var both = ConvenienceMenus.Item(L10n.T("Phone.WindowsLinkDropBothComputers"));
        both.IsEnabled = shared;
        both.Click += async (_, _) => await play(false, false);
        menu.Items.Add(both);
        return menu;
    }
}
#endif
