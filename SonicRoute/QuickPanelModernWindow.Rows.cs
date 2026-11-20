using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using SonicRoute.Core;
using SonicRoute.Core.Models;
using Button = System.Windows.Controls.Button;

namespace SonicRoute
{
    public partial class QuickPanelModernWindow
    {
        private Grid CreateRowMuteIcon(AppItem item, out Button button, out Border muteDot)
        {
            var iconGrid = new Grid { Width = 30, Height = 30, Margin = new Thickness(0, 0, 2, 0) };
            var appIcon = new System.Windows.Controls.ContentControl
            {
                Width = 18,
                Height = 18,
                Focusable = false,
                HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch,
                VerticalContentAlignment = System.Windows.VerticalAlignment.Stretch,
                Content = item,
                ContentTemplate = (DataTemplate)FindResource("AppIconTemplate")
            };
            // 图标与首字兜底共用模板；AppItem.Icon 的异步通知仍会自动刷新。
            button = new Button
            {
                Content = appIcon,
                Style = (Style)FindResource("RowIconButton")
            };
            iconGrid.Children.Add(button);
            muteDot = new Border
            {
                Width = 8,
                Height = 8,
                CornerRadius = new CornerRadius(4),
                IsHitTestVisible = false,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                VerticalAlignment = System.Windows.VerticalAlignment.Top,
                Margin = new Thickness(0, 2, 1, 0),
                Visibility = Visibility.Collapsed
            };
            muteDot.SetResourceReference(Border.BackgroundProperty, "Theme.AccentInverse");
            iconGrid.Children.Add(muteDot);
            DockPanel.SetDock(iconGrid, Dock.Left);
            return iconGrid;
        }

        private static void ApplyRowMuteAppearance(Slider slider, Border muteDot, bool muted)
        {
            var visibility = muted ? Visibility.Visible : Visibility.Collapsed;
            if (muteDot.Visibility == visibility) return;
            muteDot.Visibility = visibility;
            if (muted) slider.SetResourceReference(Slider.ForegroundProperty, "Theme.AccentInverse");
            else slider.ClearValue(Slider.ForegroundProperty);
        }

        private FrameworkElement CreateAppRowVisual(AppRow row)
        {
            if (row.RootElement != null) return row.RootElement;
            bool legacyLayout = row.LegacyLayout, showOutputName = row.ShowOutputName;
            var item = row.IconItem;
            row.VisualLifetime = CancellationTokenSource.CreateLinkedTokenSource(_uiLifetime.Token);
            var header = new Border
            {
                Height = row.CollapsedHeight - 2,
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(4, legacyLayout ? 0 : 2, 4, legacyLayout ? 0 : 2)
            };
            var dock = new DockPanel();

            var iconGrid = CreateRowMuteIcon(item, out var iconBtn, out row.MuteDot);
            row.IconButton = iconBtn;
            iconBtn.Tag = row;
            iconBtn.ToolTip = item.Label;
            iconBtn.Click += async (_, _) => await RowIconToggleMuteAsync(row);
            AppIconDragController.AttachDesktop(iconGrid, item);
            System.Windows.Automation.AutomationProperties.SetName(iconBtn, item.Label + " " + L10n.T("Qp.Mute"));

            var expand = new ToggleButton
            {
                Width = legacyLayout && showOutputName ? 80 : 32,
                Height = legacyLayout ? (showOutputName ? 24 : 16) : 32,
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
                Style = (Style)FindResource("RowExpandButton"),
                Tag = row,
                ToolTip = L10n.T("Qp.ExpandDevices")
            };
            var glyph = new TextBlock { Text = "▾", RenderTransform = row.ExpandArrow, RenderTransformOrigin = new System.Windows.Point(0.5, 0.5) };
            System.Windows.Automation.AutomationProperties.SetName(expand, item.Label + " " + L10n.T("Qp.ExpandDevices"));
            glyph.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding("Foreground") { Source = expand });
            if (legacyLayout && showOutputName)
            {
                var deviceLabel = new Grid { Width = 72, ClipToBounds = true };
                deviceLabel.ColumnDefinitions.Add(new ColumnDefinition());
                deviceLabel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Name = NewRowName();
                row.Name.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding("Foreground") { Source = expand });
                glyph.Margin = new Thickness(4, 0, 0, 0);
                glyph.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(glyph, 1);
                deviceLabel.Children.Add(row.Name);
                deviceLabel.Children.Add(glyph);
                expand.Content = deviceLabel;
            }
            else expand.Content = glyph;
            expand.Checked += RowExpand_Changed;
            expand.Unchecked += RowExpand_Changed;
            row.ExpandButton = expand;
            DockPanel.SetDock(expand, Dock.Right);

            row.Slider = legacyLayout ? new Slider() : new AppRowVolumeSlider();
            row.Slider.Minimum = 0;
            row.Slider.Maximum = 100;
            row.Slider.Style = (Style)FindResource("RowSliderStyle");
            row.Slider.VerticalAlignment = VerticalAlignment.Center;
            row.Slider.Margin = new Thickness(0, 0, 6, 0);
            row.Slider.Tag = row;
            System.Windows.Automation.AutomationProperties.SetName(row.Slider, item.Label + " " + L10n.T("Ov.VolumeTitle"));
            row.Slider.ApplyTemplate();
            row.TrackBg = row.Slider.Template.FindName("PART_TrackBg", row.Slider) as Border;
            row.TrackFill = row.Slider.Template.FindName("PART_TrackFill", row.Slider) as Border;
            row.PeakVisual = row.Slider.Template.FindName("PART_PeakVisual", row.Slider) as Border;
            if (row.PeakVisual != null)
            {
                // 模板内声明的 Freezable 可能被 WPF 共享并冻结；每行创建独立可写实例。
                row.PeakTransform = new ScaleTransform(0, 1);
                row.PeakVisual.RenderTransform = row.PeakTransform;
            }
            // 轨道宽度变化和 ValueChanged 才需要重排，避免每次电平渲染触发所有行的布局计算。
            if (row.TrackBg != null)
                row.TrackBg.SizeChanged += (_, _) => UpdateRowSliderLayout(row);
            // 标准 Track 负责点击定位和拖动；透明的 32px 区域也可命中。
            row.Slider.ValueChanged += RowSlider_ValueChanged;
            VolumePresets.Attach(row.Slider);
            row.Slider.MouseWheel += RowSlider_MouseWheel;

            FrameworkElement volume = row.Slider;
            if (!legacyLayout)
            {
                var labeledVolume = new Grid();
                labeledVolume.RowDefinitions.Add(new RowDefinition { Height = new GridLength(16) });
                labeledVolume.RowDefinitions.Add(new RowDefinition());
                row.Name = NewRowName();
                row.Name.Margin = new Thickness(14, 0, 20, 0);
                row.Name.IsHitTestVisible = false;
                row.Slider.Height = 48;
                row.Slider.Padding = new Thickness(0, 16, 0, 0);
                ToolTipService.SetShowOnDisabled(row.Slider, true);
                Grid.SetRowSpan(row.Slider, 2);
                labeledVolume.Children.Add(row.Slider);
                labeledVolume.Children.Add(row.Name);
                volume = labeledVolume;
            }
            dock.Children.Add(iconGrid);
            dock.Children.Add(expand);
            dock.Children.Add(volume);
            header.Child = dock;

            // 子内容始终按自然高度布局，外壳从顶部裁剪，文字和按钮不被压缩。
            row.ExpandPanel = new StackPanel
            {
                Margin = new Thickness(26, 0, 0, 6),
                Visibility = Visibility.Collapsed
            };
            row.ExpandWrap = new AnimatedExpandHost
            {
                Child = row.ExpandPanel
            };

            var root = new StackPanel { Margin = new Thickness(0, 0, 0, 2) };
            root.Children.Add(header);
            root.Children.Add(row.ExpandWrap);
            row.RootElement = root;
            row.HeaderBorder = header;
            row.NamePrimary = row.NameSuffix = null;
            bool updating = _updatingRowVolumes;
            _updatingRowVolumes = true;
            try
            {
                UpdateRowName(row, row.OutputId);
                ApplyRowAudioState(row, row.Volume, row.IsMuted, row.MuteKnown);
                row.ExpandButton.IsChecked = row.Expanded;
                UiMotion.RotateArrow(row.ExpandArrow, row.Expanded, false);
                if (row.Expanded) _ = RestoreRealizedExpansionAsync(row);
            }
            finally { _updatingRowVolumes = updating; }
            UpdateRowHighlights();
            QueueMeterViewport();
            QueueRowIconLoads();
            return root;
        }

        private async Task RestoreRealizedExpansionAsync(AppRow row)
        {
            if (await BuildRowDevicesAsync(row) && !_isClosed && row.RootElement != null && row.Expanded)
                row.ExpandWrap.SetExpanded(true, false);
        }

        private void ReleaseAppRowVisual(AppRow row)
        {
            if (row.RootElement == null) return;
            if (_scrollingRow == row) StopRowScroll();
            if (_recentDeviceRow == row) _recentDeviceRow = null;
            _visibleMeterRows.Remove(row);
            row.VisualLifetime?.Cancel();
            row.VisualLifetime?.Dispose();
            row.VisualLifetime = null;
            row.ExpandWrap.FinishImmediately();
            row.ExpandArrow.BeginAnimation(RotateTransform.AngleProperty, null);
            ClearDeviceControls(row);
            row.ExpandButton.Checked -= RowExpand_Changed;
            row.ExpandButton.Unchecked -= RowExpand_Changed;
            row.RootElement = null;
            row.HeaderBorder = null!;
            row.Slider = null!;
            row.MuteDot = null!;
            row.ExpandButton = null!;
            row.ExpandPanel = null!;
            row.ExpandWrap = null!;
            row.ExpandArrow = new RotateTransform();
            row.Name = null;
            row.IconButton = null;
            row.TrackBg = row.TrackFill = row.PeakVisual = null;
            row.PeakTransform = null;
            row.PeakLevel = 0;
            // Retire the UI icon model as well. An extraction already running for the
            // old realization cannot repopulate a newly visible row with a stale result.
            row.IconItem = AppItem.From(row.App);
            QueueMeterViewport();
        }
    }
}
