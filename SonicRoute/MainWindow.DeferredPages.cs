using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using RadioButton = System.Windows.Controls.RadioButton;
using TextBox = System.Windows.Controls.TextBox;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;

namespace SonicRoute
{
    public partial class MainWindow
    {
        private readonly List<string> _deferredPageNames = new();
        internal Grid ThemePage = null!;
        internal RadioButton ThemeSystem = null!;
        internal RadioButton ThemeLight = null!;
        internal RadioButton ThemeDark = null!;
        internal Border ThemeColorPreview = null!;
        internal TextBox ThemeColorHex = null!;
        internal Button ThemeChooseColor = null!;
        internal TextBlock OpacityText = null!;
        internal Slider OpacitySlider = null!;
        internal Button OsdAdjustBtnTheme = null!;
        internal Button OsdResetButtonTheme = null!;
        internal CheckBox OsdVolumeVisualCheck = null!;
        internal CheckBox MicMutePersistCheck = null!;
        internal Grid MicMuteTrackInputRow = null!;
        internal CheckBox MicMuteTrackInputCheck = null!;
        internal ToggleButton OsdMoreToggle = null!;
        internal StackPanel OsdSlidersPanel = null!;
        internal Slider OsdWidthSlider = null!;
        internal TextBlock OsdWidthValue = null!;
        internal Slider OsdFontSlider = null!;
        internal TextBlock OsdFontValue = null!;
        internal Slider OsdFadeInSlider = null!;
        internal TextBlock OsdFadeInValue = null!;
        internal Slider OsdFadeOutSlider = null!;
        internal TextBlock OsdFadeOutValue = null!;
        internal Button PanelPosAdjustBtn = null!;
        internal Button PanelPosResetBtn = null!;
        internal StackPanel PanelHeightSection = null!;
        internal Slider PanelHeightSlider = null!;
        internal TextBlock PanelHeightValue = null!;
        internal CheckBox AppPeakMeterCheck = null!;
        internal Grid SettingsPage = null!;
        internal ToggleButton KeepDevicesMoreToggle = null!;
        internal StackPanel KeepDevicesBody = null!;
        internal Button SelectAllOutputButton = null!;
        internal ItemsControl OutputFilterList = null!;
        internal DockPanel InputFilterHeader = null!;
        internal Button SelectAllInputButton = null!;
        internal ItemsControl InputFilterList = null!;
        internal ToggleButton DeviceNamesMoreToggle = null!;
        internal StackPanel DeviceNamesBody = null!;
        internal ItemsControl OutputNameList = null!;
        internal TextBlock InputNameHeader = null!;
        internal ItemsControl InputNameList = null!;
        internal RadioButton DefaultAppRecent = null!;
        internal RadioButton DefaultAppLast = null!;
        internal RadioButton DefaultAppFixed = null!;
        internal ComboBox FixedAppCombo = null!;
        internal CheckBox SettingsStartMinimized = null!;
        internal CheckBox SettingsAutoStart = null!;
        internal ToggleButton SettingsMoreToggle = null!;
        internal StackPanel SettingsMorePanel = null!;
        internal Button CleanAutoStartBtn = null!;
        internal CheckBox SettingsShowPanelOnStart = null!;
        internal TextBox VolumeStepBox = null!;
        internal CheckBox SettingsTrayWheelEverywhere = null!;
        internal CheckBox ExpMicOptionCheck = null!;
        internal CheckBox ExpMicPanelCheck = null!;
        internal ComboBox QuickPanelStyleCombo = null!;
        internal StackPanel PanelChangeSysDefSection = null!;
        internal CheckBox SettingsPanelChangeSysDef = null!;
        internal CheckBox ExpCollapseCheck = null!;
        internal CheckBox SettingsExperimentalMode = null!;
        internal TextBlock ExperimentalModeHint = null!;
        internal ComboBox LangCombo = null!;
        internal ToggleButton LangMoreToggle = null!;
        internal StackPanel LangMorePanel = null!;
        internal Button ExpExportLangButton = null!;
        internal Button ExpImportLangButton = null!;
        internal Button ExpOpenLangDirButton = null!;
        internal Button ExpRestoreLangButton = null!;
        internal StackPanel CustomLangSection = null!;
        internal ContentControl CustomLangList = null!;
        internal TextBlock AboutAuthorText = null!;
        internal Hyperlink BiliLink = null!;
        internal Hyperlink IfdianLink = null!;
        internal Hyperlink GithubLink = null!;

        internal Grid HotkeysPage = null!;
        internal ItemsControl HotkeyList = null!;
        internal Grid ExperimentalPage = null!;
        internal CheckBox ExpBackgroundPollingCheck = null!;
        internal CheckBox ExpHistoryCheck = null!;
        internal ContentControl ExpHistoryOptionsHost = null!;
        internal Button ExpResetAllButton = null!;
        internal Button ExpClearConfigButton = null!;
        internal Button ExpExportConfigButton = null!;
        internal Button ExpImportConfigButton = null!;

        private T FindDeferredControl<T>(Grid page, string name) where T : DependencyObject
        {
            var control = (page.FindName(name) ?? LogicalTreeHelper.FindLogicalNode(page, name)) as T
                ?? throw new InvalidOperationException("Missing deferred page control: " + name);
            RegisterName(name, control);
            _deferredPageNames.Add(name);
            return control;
        }

        private void EnsureThemePage()
        {
            if (_isClosed || ThemePageHost.Child != null) return;
            bool suppressed = _suppressSettings;
            bool syncing = _syncingOsdSliders;
            _suppressSettings = _syncingOsdSliders = true;
            try
            {
                var content = (Grid)Resources["ThemePageContent"];
                ThemePage = FindDeferredControl<Grid>(content, nameof(ThemePage));
                ThemeSystem = FindDeferredControl<RadioButton>(content, nameof(ThemeSystem));
                ThemeLight = FindDeferredControl<RadioButton>(content, nameof(ThemeLight));
                ThemeDark = FindDeferredControl<RadioButton>(content, nameof(ThemeDark));
                ThemeColorPreview = FindDeferredControl<Border>(content, nameof(ThemeColorPreview));
                ThemeColorHex = FindDeferredControl<TextBox>(content, nameof(ThemeColorHex));
                ThemeChooseColor = FindDeferredControl<Button>(content, nameof(ThemeChooseColor));
                OpacityText = FindDeferredControl<TextBlock>(content, nameof(OpacityText));
                OpacitySlider = FindDeferredControl<Slider>(content, nameof(OpacitySlider));
                OsdAdjustBtnTheme = FindDeferredControl<Button>(content, nameof(OsdAdjustBtnTheme));
                OsdResetButtonTheme = FindDeferredControl<Button>(content, nameof(OsdResetButtonTheme));
                OsdVolumeVisualCheck = FindDeferredControl<CheckBox>(content, nameof(OsdVolumeVisualCheck));
                MicMutePersistCheck = FindDeferredControl<CheckBox>(content, nameof(MicMutePersistCheck));
                MicMuteTrackInputRow = FindDeferredControl<Grid>(content, nameof(MicMuteTrackInputRow));
                MicMuteTrackInputCheck = FindDeferredControl<CheckBox>(content, nameof(MicMuteTrackInputCheck));
                OsdMoreToggle = FindDeferredControl<ToggleButton>(content, nameof(OsdMoreToggle));
                OsdSlidersPanel = FindDeferredControl<StackPanel>(content, nameof(OsdSlidersPanel));
                OsdWidthSlider = FindDeferredControl<Slider>(content, nameof(OsdWidthSlider));
                OsdWidthValue = FindDeferredControl<TextBlock>(content, nameof(OsdWidthValue));
                OsdFontSlider = FindDeferredControl<Slider>(content, nameof(OsdFontSlider));
                OsdFontValue = FindDeferredControl<TextBlock>(content, nameof(OsdFontValue));
                OsdFadeInSlider = FindDeferredControl<Slider>(content, nameof(OsdFadeInSlider));
                OsdFadeInValue = FindDeferredControl<TextBlock>(content, nameof(OsdFadeInValue));
                OsdFadeOutSlider = FindDeferredControl<Slider>(content, nameof(OsdFadeOutSlider));
                OsdFadeOutValue = FindDeferredControl<TextBlock>(content, nameof(OsdFadeOutValue));
                PanelPosAdjustBtn = FindDeferredControl<Button>(content, nameof(PanelPosAdjustBtn));
                PanelPosResetBtn = FindDeferredControl<Button>(content, nameof(PanelPosResetBtn));
                PanelHeightSection = FindDeferredControl<StackPanel>(content, nameof(PanelHeightSection));
                PanelHeightSlider = FindDeferredControl<Slider>(content, nameof(PanelHeightSlider));
                PanelHeightValue = FindDeferredControl<TextBlock>(content, nameof(PanelHeightValue));
                AppPeakMeterCheck = FindDeferredControl<CheckBox>(content, nameof(AppPeakMeterCheck));
                ThemePageHost.Child = content;
            }
            finally
            {
                _suppressSettings = suppressed;
                _syncingOsdSliders = syncing;
            }
        }

        private void EnsureSettingsPage()
        {
            if (_isClosed || SettingsPageHost.Child != null) return;
            bool suppressed = _suppressSettings;
            bool syncing = _syncingOsdSliders;
            _suppressSettings = _syncingOsdSliders = true;
            try
            {
                var content = (Grid)Resources["SettingsPageContent"];
                SettingsPage = FindDeferredControl<Grid>(content, nameof(SettingsPage));
                KeepDevicesMoreToggle = FindDeferredControl<ToggleButton>(content, nameof(KeepDevicesMoreToggle));
                KeepDevicesBody = FindDeferredControl<StackPanel>(content, nameof(KeepDevicesBody));
                SelectAllOutputButton = FindDeferredControl<Button>(content, nameof(SelectAllOutputButton));
                OutputFilterList = FindDeferredControl<ItemsControl>(content, nameof(OutputFilterList));
                InputFilterHeader = FindDeferredControl<DockPanel>(content, nameof(InputFilterHeader));
                SelectAllInputButton = FindDeferredControl<Button>(content, nameof(SelectAllInputButton));
                InputFilterList = FindDeferredControl<ItemsControl>(content, nameof(InputFilterList));
                DeviceNamesMoreToggle = FindDeferredControl<ToggleButton>(content, nameof(DeviceNamesMoreToggle));
                DeviceNamesBody = FindDeferredControl<StackPanel>(content, nameof(DeviceNamesBody));
                OutputNameList = FindDeferredControl<ItemsControl>(content, nameof(OutputNameList));
                InputNameHeader = FindDeferredControl<TextBlock>(content, nameof(InputNameHeader));
                InputNameList = FindDeferredControl<ItemsControl>(content, nameof(InputNameList));
                DefaultAppRecent = FindDeferredControl<RadioButton>(content, nameof(DefaultAppRecent));
                DefaultAppLast = FindDeferredControl<RadioButton>(content, nameof(DefaultAppLast));
                DefaultAppFixed = FindDeferredControl<RadioButton>(content, nameof(DefaultAppFixed));
                FixedAppCombo = FindDeferredControl<ComboBox>(content, nameof(FixedAppCombo));
                SettingsStartMinimized = FindDeferredControl<CheckBox>(content, nameof(SettingsStartMinimized));
                SettingsAutoStart = FindDeferredControl<CheckBox>(content, nameof(SettingsAutoStart));
                SettingsMoreToggle = FindDeferredControl<ToggleButton>(content, nameof(SettingsMoreToggle));
                SettingsMorePanel = FindDeferredControl<StackPanel>(content, nameof(SettingsMorePanel));
                CleanAutoStartBtn = FindDeferredControl<Button>(content, nameof(CleanAutoStartBtn));
                SettingsShowPanelOnStart = FindDeferredControl<CheckBox>(content, nameof(SettingsShowPanelOnStart));
                VolumeStepBox = FindDeferredControl<TextBox>(content, nameof(VolumeStepBox));
                SettingsTrayWheelEverywhere = FindDeferredControl<CheckBox>(content, nameof(SettingsTrayWheelEverywhere));
                ExpMicOptionCheck = FindDeferredControl<CheckBox>(content, nameof(ExpMicOptionCheck));
                ExpMicPanelCheck = FindDeferredControl<CheckBox>(content, nameof(ExpMicPanelCheck));
                QuickPanelStyleCombo = FindDeferredControl<ComboBox>(content, nameof(QuickPanelStyleCombo));
                PanelChangeSysDefSection = FindDeferredControl<StackPanel>(content, nameof(PanelChangeSysDefSection));
                SettingsPanelChangeSysDef = FindDeferredControl<CheckBox>(content, nameof(SettingsPanelChangeSysDef));
                ExpCollapseCheck = FindDeferredControl<CheckBox>(content, nameof(ExpCollapseCheck));
                SettingsExperimentalMode = FindDeferredControl<CheckBox>(content, nameof(SettingsExperimentalMode));
                ExperimentalModeHint = FindDeferredControl<TextBlock>(content, nameof(ExperimentalModeHint));
                LangCombo = FindDeferredControl<ComboBox>(content, nameof(LangCombo));
                LangMoreToggle = FindDeferredControl<ToggleButton>(content, nameof(LangMoreToggle));
                LangMorePanel = FindDeferredControl<StackPanel>(content, nameof(LangMorePanel));
                ExpExportLangButton = FindDeferredControl<Button>(content, nameof(ExpExportLangButton));
                ExpImportLangButton = FindDeferredControl<Button>(content, nameof(ExpImportLangButton));
                ExpOpenLangDirButton = FindDeferredControl<Button>(content, nameof(ExpOpenLangDirButton));
                ExpRestoreLangButton = FindDeferredControl<Button>(content, nameof(ExpRestoreLangButton));
                CustomLangSection = FindDeferredControl<StackPanel>(content, nameof(CustomLangSection));
                CustomLangList = FindDeferredControl<ContentControl>(content, nameof(CustomLangList));
                AboutAuthorText = FindDeferredControl<TextBlock>(content, nameof(AboutAuthorText));
                BiliLink = FindDeferredControl<Hyperlink>(content, nameof(BiliLink));
                IfdianLink = FindDeferredControl<Hyperlink>(content, nameof(IfdianLink));
                GithubLink = FindDeferredControl<Hyperlink>(content, nameof(GithubLink));
                SettingsPageHost.Child = content;
                InitializeUpdateSettings(FindDeferredControl<ContentControl>(content, "UpdatesHost"));
                FixedAppCombo.DropDownOpened += AppCombo_DropDownOpened;
            }
            finally
            {
                _suppressSettings = suppressed;
                _syncingOsdSliders = syncing;
            }
        }

        private void EnsureHotkeysPage()
        {
            if (_isClosed || HotkeysPageHost.Child != null) return;
            bool suppressed = _suppressSettings;
            _suppressSettings = true;
            try
            {
                var content = (Grid)Resources["HotkeysPageContent"];
                HotkeysPage = FindDeferredControl<Grid>(content, nameof(HotkeysPage));
                HotkeyList = FindDeferredControl<ItemsControl>(content, nameof(HotkeyList));
                HotkeysPageHost.Child = content;
            }
            finally { _suppressSettings = suppressed; }
        }

        private void EnsureExperimentalPage()
        {
            if (_isClosed || ExperimentalPageHost.Child != null) return;
            bool suppressed = _suppressSettings;
            _suppressSettings = true;
            try
            {
                var content = (Grid)Resources["ExperimentalPageContent"];
                ExperimentalPage = FindDeferredControl<Grid>(content, nameof(ExperimentalPage));
                ExpBackgroundPollingCheck = FindDeferredControl<CheckBox>(content, nameof(ExpBackgroundPollingCheck));
                ExpHistoryCheck = FindDeferredControl<CheckBox>(content, nameof(ExpHistoryCheck));
                ExpHistoryOptionsHost = FindDeferredControl<ContentControl>(content, nameof(ExpHistoryOptionsHost));
                ExpResetAllButton = FindDeferredControl<Button>(content, nameof(ExpResetAllButton));
                ExpClearConfigButton = FindDeferredControl<Button>(content, nameof(ExpClearConfigButton));
                ExpExportConfigButton = FindDeferredControl<Button>(content, nameof(ExpExportConfigButton));
                ExpImportConfigButton = FindDeferredControl<Button>(content, nameof(ExpImportConfigButton));
                ExperimentalPageHost.Child = content;
            }
            finally { _suppressSettings = suppressed; }
        }

        private void SyncSettingsPanelStyle()
        {
            if (SettingsPage == null) return;
            bool suppressed = _suppressSettings;
            _suppressSettings = true;
            try
            {
                string classic = L10n.T("St.PanelClassic"), modern = L10n.T("St.PanelModern");
                if (QuickPanelStyleCombo.ItemsSource is not string[] styles || styles.Length != 2
                    || styles[0] != classic || styles[1] != modern)
                    QuickPanelStyleCombo.ItemsSource = new[] { classic, modern };
                QuickPanelStyleCombo.SelectedIndex = _config.QuickPanelStyle == "classic" ? 0 : 1;
            }
            finally { _suppressSettings = suppressed; }
        }

        private void SyncThemePanelHeight()
        {
            if (ThemePage == null) return;
            bool suppressed = _suppressSettings;
            _suppressSettings = true;
            try
            {
                PanelHeightSection.Visibility = _config.QuickPanelStyle == "classic"
                    ? Visibility.Collapsed : Visibility.Visible;
                int height = SonicRoute.Core.Compat.MathEx.Clamp(_config.QuickPanelHeight, 350, 850);
                PanelHeightSlider.Value = height;
                PanelHeightValue.Text = height + " px";
            }
            finally { _suppressSettings = suppressed; }
        }

        private void ReleaseDeferredPages()
        {
            if (FixedAppCombo != null) FixedAppCombo.DropDownOpened -= AppCombo_DropDownOpened;
            ThemePageHost.Child = SettingsPageHost.Child = null;
            HotkeysPageHost.Child = ExperimentalPageHost.Child = null;
            foreach (string name in _deferredPageNames) UnregisterName(name);
            _deferredPageNames.Clear();
            HotkeysPage = null!;
            HotkeyList = null!;
            ExperimentalPage = null!;
            ExpBackgroundPollingCheck = null!;
            ExpHistoryCheck = null!;
            ExpHistoryOptionsHost = null!;
            ExpResetAllButton = null!;
            ExpClearConfigButton = null!;
            ExpExportConfigButton = null!;
            ExpImportConfigButton = null!;
            ThemePage = null!;
            ThemeSystem = null!;
            ThemeLight = null!;
            ThemeDark = null!;
            ThemeColorPreview = null!;
            ThemeColorHex = null!;
            ThemeChooseColor = null!;
            OpacityText = null!;
            OpacitySlider = null!;
            OsdAdjustBtnTheme = null!;
            OsdResetButtonTheme = null!;
            OsdVolumeVisualCheck = null!;
            MicMutePersistCheck = null!;
            MicMuteTrackInputRow = null!;
            MicMuteTrackInputCheck = null!;
            OsdMoreToggle = null!;
            OsdSlidersPanel = null!;
            OsdWidthSlider = null!;
            OsdWidthValue = null!;
            OsdFontSlider = null!;
            OsdFontValue = null!;
            OsdFadeInSlider = null!;
            OsdFadeInValue = null!;
            OsdFadeOutSlider = null!;
            OsdFadeOutValue = null!;
            PanelPosAdjustBtn = null!;
            PanelPosResetBtn = null!;
            PanelHeightSection = null!;
            PanelHeightSlider = null!;
            PanelHeightValue = null!;
            AppPeakMeterCheck = null!;
            SettingsPage = null!;
            KeepDevicesMoreToggle = null!;
            KeepDevicesBody = null!;
            SelectAllOutputButton = null!;
            OutputFilterList = null!;
            InputFilterHeader = null!;
            SelectAllInputButton = null!;
            InputFilterList = null!;
            DeviceNamesMoreToggle = null!;
            DeviceNamesBody = null!;
            OutputNameList = null!;
            InputNameHeader = null!;
            InputNameList = null!;
            DefaultAppRecent = null!;
            DefaultAppLast = null!;
            DefaultAppFixed = null!;
            FixedAppCombo = null!;
            SettingsStartMinimized = null!;
            SettingsAutoStart = null!;
            SettingsMoreToggle = null!;
            SettingsMorePanel = null!;
            CleanAutoStartBtn = null!;
            SettingsShowPanelOnStart = null!;
            VolumeStepBox = null!;
            SettingsTrayWheelEverywhere = null!;
            ExpMicOptionCheck = null!;
            ExpMicPanelCheck = null!;
            QuickPanelStyleCombo = null!;
            PanelChangeSysDefSection = null!;
            SettingsPanelChangeSysDef = null!;
            ExpCollapseCheck = null!;
            SettingsExperimentalMode = null!;
            ExperimentalModeHint = null!;
            LangCombo = null!;
            LangMoreToggle = null!;
            LangMorePanel = null!;
            ExpExportLangButton = null!;
            ExpImportLangButton = null!;
            ExpOpenLangDirButton = null!;
            ExpRestoreLangButton = null!;
            CustomLangSection = null!;
            CustomLangList = null!;
            AboutAuthorText = null!;
            BiliLink = null!;
            IfdianLink = null!;
            GithubLink = null!;
        }
    }
}
