using System.Collections.Generic;
using System.Windows;
using SonicRoute.Core.Models;
using Application = System.Windows.Application;

namespace SonicRoute
{
    public partial class QuickPanelWindow
    {
        private readonly WindowUiLifetime _uiLifetime = new();
        private int _appStateRequest;

        private void CleanupClosedWindow()
        {
            if (_isClosed) return;
            _isClosed = true;
            ++_loadVersion; ++_appStateRequest;
            _uiLifetime.Dispose();
            _entranceMotion.Stop();
            CurrentAppService.CurrentChanged -= OnSharedCurrentChanged;
            SystemParameters.StaticPropertyChanged -= OnSystemParamChanged;
            _suppressAppCombo = true;
            _volumeReady = false;
            AppCombo.ItemsSource = null;
            AppCombo.Items.Clear();
            OutputButtonsPanel.Items.Clear();
            InputButtonsPanel.Items.Clear();
            _outputs = new(); _outputDisplay = new(); _inputs = new(); _inputDisplay = new();
            _currentApp = null; _currentOutId = _currentInId = null;
            _loadTask = null;
            if (_adjustMode)
            {
                _adjustMode = _adjustDragging = false;
                ReleaseMouseCapture();
                ((App)Application.Current).NotifyQuickPanelAdjustFinished();
            }
        }
    }
}
