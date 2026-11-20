using System.Collections;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using SonicRoute;

internal static partial class Program
{
    private static void WindowsFileBrowserUi(PhoneDevicePage page, object local, object remote, string id, string output)
    {
        const BindingFlags statics = BindingFlags.Static | BindingFlags.NonPublic;
        var serviceUi = typeof(App).Assembly.GetType("SonicRoute.WindowsLinkFileUi")!;
        var dialogs = (IDictionary)serviceUi.GetField("BrowsePrompts", statics)!.GetValue(null)!;
        var work = Directory.CreateDirectory(Path.Combine(Environment.GetEnvironmentVariable("SONICROUTE_DATA_DIRECTORY")!, "浏览示例"));
        var inbox = work.CreateSubdirectory("接收");
        var source = work.CreateSubdirectory("资料");
        const string folderName = "会议资料", fileName = "报告.txt", looseFile = "说明.txt";
        var child = source.CreateSubdirectory(folderName);
        File.WriteAllText(Path.Combine(child.FullName, fileName), "测试");
        File.WriteAllText(Path.Combine(source.FullName, looseFile), "单个文件");
        local.GetType().GetMethod("SetReceiveDirectory", Hidden)!.Invoke(local, [inbox.FullName]);
        T Get<T>(string name) where T : FrameworkElement => (T)page.FindName(name);
        static void Click(Button b)
        {
            if (!b.IsVisible || !b.IsEnabled) throw new InvalidOperationException("Unavailable button: " + b.Name);
            var peer = UIElementAutomationPeer.CreatePeerForElement(b)!;
            ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
        }
        var open = Get<Button>("WindowsBrowseFiles");
        var close = Get<Button>("WindowsBrowserClose");
        var list = Get<ListBox>("WindowsBrowserEntries");
        var get = Get<Button>("WindowsBrowserGet");
        var up = Get<Button>("WindowsBrowserUp");
        var area = Get<FrameworkElement>("WindowsFileBrowser");
        var fileCard = Get<FrameworkElement>("WindowsFileCard");
        Window Pending()
        {
            Until(() => dialogs.Count == 1);
            return dialogs.Values.Cast<Window>().Single();
        }
        static object Entry(ListBox entries, string name) => entries.Items.Cast<object>().Single(r =>
            (string?)r.GetType().GetProperty("Name")!.GetValue(r) == name);
        ListBoxItem Row(string name)
        {
            var item = Entry(list, name);
            list.ScrollIntoView(item);
            list.UpdateLayout();
            return (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(item);
        }
        serviceUi.GetMethod("Attach", statics)!.Invoke(null, [remote]);
        try
        {
            typeof(PhoneDevicePage).GetMethod("Activate", Hidden)!.Invoke(page, null);
            typeof(PhoneDevicePage).GetMethod("ShowWindowsLinkPeer", Hidden)!.Invoke(page, [id]);
            fileCard.BringIntoView();
            Until(() => open.IsVisible && open.IsEnabled);
            Check(Get<Button>("WindowsSendFolder").IsEnabled && !area.IsVisible,
                "the real connected device page offers both folder sending and remote browsing");
            Click(open);
            var prompt = Pending();
            Check(area.IsVisible && !list.IsEnabled && list.Items.Count == 0 && !get.IsEnabled
                && ((TextBlock)prompt.FindName("SenderLabel")).Text.Contains("本机")
                && ((Button)prompt.FindName("SaveAsButton")).Visibility == Visibility.Collapsed,
                "the source approval identifies the paired computer while the requester cannot view drives");
            Click((Button)prompt.FindName("DeclineButton"));
            Until(() => !area.IsVisible && open.IsVisible && open.IsEnabled);
            Check(Get<TextBlock>("WindowsFileStatus").IsVisible &&
                Get<TextBlock>("WindowsFileStatus").Text == L10n.T("Phone.LinkBrowseDeclined"),
                "declining closes the picker and shows a localized outcome on the device page");

            Click(open);
            prompt = Pending();
            SaveWindowRegion((FrameworkElement)prompt.Content, Path.Combine(output, "windows-browse-approval.png"));
            Click((Button)prompt.FindName("SaveButton"));
            Until(() => list.IsEnabled && list.Items.Count > 0);
            Check(!up.IsEnabled && !get.IsEnabled && !open.IsVisible, "an approved browser opens at the drive list and only permits navigation");
            SaveWindowRegion(fileCard, Path.Combine(output, "windows-browse-drives.png"));
            // Start navigation directly at our throwaway fixture to avoid enumerating unrelated drive contents.
            var browser = typeof(PhoneDevicePage).GetField("_fileBrowser", Hidden)!.GetValue(page);
            Pump((Task)typeof(PhoneDevicePage).GetMethod("NavigateWindowsFolderAsync", Hidden)!.Invoke(page, [browser, source.FullName, false])!);
            Click(Descendants<Button>(Row(folderName)).Single());
            Until(() => list.IsEnabled && list.Items.Count == 1 && Get<TextBlock>("WindowsBrowserPath").Text == child.FullName);
            Check(up.IsEnabled && Row(fileName).IsVisible, "one press on a folder opens it and makes the parent action available");
            Click(up);
            Until(() => list.IsEnabled && Get<TextBlock>("WindowsBrowserPath").Text == source.FullName);
            var choose = Descendants<CheckBox>(Row(folderName)).Single();
            ((IToggleProvider)UIElementAutomationPeer.CreatePeerForElement(choose)!.GetPattern(PatternInterface.Toggle)).Toggle();
            Click(Descendants<Button>(Row(looseFile)).Single());
            DrainUi();
            Until(() => get.IsEnabled);
            Check(list.SelectedItems.Count == 2, "a folder checkbox and a file row can be selected together");
            Click(get);
            var decisions = new HashSet<string>();
            for (int i = 0; i < 2; i++)
            {
                prompt = Pending();
                var item = ((TextBlock)prompt.FindName("FileLabel")).Text;
                Check(decisions.Add(item) && (item == folderName || item == looseFile)
                    && ((TextBlock)prompt.FindName("FolderLabel")).IsVisible == (item == folderName)
                    && !Path.Exists(Path.Combine(inbox.FullName, item)),
                    "each selected item has its own source approval identifying its name and folder scope before delivery");
                Click((Button)prompt.FindName("SaveButton"));
                Until(() => !prompt.IsVisible);
            }
            var saved = Path.Combine(inbox.FullName, folderName, fileName);
            var single = Path.Combine(inbox.FullName, looseFile);
            Until(() => list.IsEnabled && File.Exists(saved) && File.Exists(single));
            Check(File.ReadAllText(saved) == "测试" && File.ReadAllText(single) == "单个文件",
                "approving the selection receives the standalone file and the directory contents intact");
            Click(close);
            Until(() => !area.IsVisible && open.IsVisible);
            Check(Get<Button>("SendWindowsFileButton").IsVisible && Get<Button>("SendWindowsFileButton").IsEnabled,
                "closing the picker restores the file and folder drop area");
        }
        finally
        {
            serviceUi.GetMethod("Detach", statics)!.Invoke(null, [remote]);
            work.Delete(recursive: true);
        }
    }
}
