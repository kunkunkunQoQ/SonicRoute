using System.Windows;

namespace WpfUiSkeleton
{
    public partial class App : Application
    {
        // 普通 WPF 应用用默认 ShutdownMode（OnLastWindowClose）。
        // 如果需要托盘常驻，改为 OnExplicitShutdown 并在托盘退出时调用 Shutdown()。
    }
}
