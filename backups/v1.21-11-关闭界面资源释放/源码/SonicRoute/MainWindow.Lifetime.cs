using System;
using System.Windows;
using System.Windows.Controls;
using Application = System.Windows.Application;

namespace SonicRoute
{
    public partial class MainWindow
    {
        private readonly WindowUiLifetime _uiLifetime = new();

        private void OnOsdAdjustFinished()
        {
            if (_isClosed) return;
            _osdAdjusting = false;
            SetOsdAdjustLabel(L10n.T("Exp.OsdAdjust"));
        }

        private void OnPanelPositionAdjustFinished()
        {
            if (_isClosed) return;
            _panelPosAdjusting = false;
            SetPanelPosAdjustLabel(L10n.T("Exp.PanelPosAdjust"));
        }

        private void CleanupClosedWindow()
        {
            if (_isClosed) return;
            _isClosed = true;
            ++_navigationVersion;
            ++_overviewRefreshRequest; ++_overviewStateRequest;
            ++_appsRefreshRequest; ++_appsStateRequest;
            ++_autoRuleRefreshRequest; ++_autoDeviceRefreshRequest;
            _uiLifetime.Dispose();
            FinishPanelAnimations();
            FlushNameChanges(refreshDisplay: false);
            FlushThemeChanges();
            StopAutoRefresh();
            CurrentAppService.CurrentChanged -= OnSharedCurrentChanged;
            AutoRuleService.ExecutionStateChanged -= AutoExecutionStateChanged;
            PreviewKeyDown -= MainWindow_PreviewKeyDown;
            _hwndSource?.RemoveHook(TaskbarMinimizeWndProc);
            _hwndSource = null;

            if (Application.Current is App app)
            {
                if (_osdAdjustSubscribed) app.OsdAdjustFinished -= OnOsdAdjustFinished;
                if (_panelPosAdjustSubscribed) app.QuickPanelAdjustFinished -= OnPanelPositionAdjustFinished;
                _osdAdjustSubscribed = _panelPosAdjustSubscribed = false;
                if (_osdAdjusting) app.CancelOsdAdjust();
                if (_panelPosAdjusting) app.CancelQuickPanelAdjust();
                _osdAdjusting = _panelPosAdjusting = false;
            }

            // Detaching selections must not save configuration or change audio routing.
            _suppressAppCombo = _suppressDevCombo = _suppressVolume = true;
            _suppressSettings = _suppressFilter = _suppressRename = _suppressAutoUi = true;
            ReleaseAutomationEditor();
            foreach (var name in new[] { "OverviewAppCombo", "OverviewOutputCombo", "OverviewInputCombo",
                "AppsOutputCombo", "AppsInputCombo", "AppsListBox", "FixedAppCombo", "AutoTriggerAppCombo",
                "AutoRuleList", "OutputNameList", "InputNameList", "OutputFilterList", "InputFilterList",
                "OverviewOutputQuickPanel", "OverviewInputQuickPanel", "HotkeyList" })
            {
                if (FindName(name) is ItemsControl items)
                {
                    items.ItemsSource = null;
                    items.Items.Clear();
                }
            }
            _outputs = new(); _outputDisplay = new(); _inputs = new(); _inputDisplay = new();
            _overviewApp = _appsSelected = null;
            _appItems = new(); _settingsApps = new(); _autoApps = new();
            _candidateItems = new(); _candidateSnapshot = new(); _actionChoices = null;
            _autoOutputs = _autoInputs = null;
            _autoRows.Clear(); _autoRules = new(); _autoRuleFingerprints.Clear(); _autoRunIds.Clear();
            _nameSaveTimer = _themeSaveTimer = _osdPreviewTimer = null;
            CustomLangList.Content = null;
        }
    }
}
