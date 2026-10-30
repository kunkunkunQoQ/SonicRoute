using SonicRoute.Core;

namespace SonicRoute
{
    public partial class MainWindow
    {
        private bool _configImportCommitted;
        private void SaveSettings(AppConfig config)
        {
            if (!_configImportCommitted) ConfigService.QueueSave(config);
        }
    }
}
