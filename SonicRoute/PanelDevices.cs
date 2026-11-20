using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Automation;
using System.Windows.Data;
using Button = System.Windows.Controls.Button;
using Binding = System.Windows.Data.Binding;
using SonicRoute.Core;
using SonicRoute.Core.Interop;
using SonicRoute.Core.Models;

namespace SonicRoute
{
    /// <summary>
    /// 设备列表辅助：在显示设备列表头部插入"系统默认输出/输入"虚拟项。
    /// 用于快速面板、完整界面应用页、快捷键循环切换——行为与保留设备完全一致，
    /// 选中即把应用切回跟随系统默认设备（AudioService.ApplyEndpoint 内部清除持久化路由）。
    /// 虚拟项与正常设备一样参与"保留设备"筛选（可勾选隐藏）与"设备名称"自定义（可改名）。
    /// </summary>
    public static class PanelDevices
    {
        // TextSearch reads DisplayName directly when the user types in a device picker.
        private sealed class LocalizedDevice(string nameKey) : AudioDeviceInfo
        {
            internal string NameKey { get; } = nameKey;
            public override string DisplayName => L10n.T(NameKey);
        }

        internal static string? NameKey(AudioDeviceInfo device) =>
            device is LocalizedDevice localized ? localized.NameKey :
            string.IsNullOrWhiteSpace(device.DisplayName) ? "Dev.Unknown" : null;

        internal static string Name(AudioDeviceInfo device) =>
            NameKey(device) is string key ? L10n.T(key) : device.DisplayName!;

        internal static string ShortName(string full)
        {
            int paren = full.IndexOf('(');
            string head = paren > 0 ? full.Substring(0, paren).Trim() : full;
            if (head.Length <= 12) return head;
            int[] elements = StringInfo.ParseCombiningCharacters(head);
            return elements.Length <= 12 ? head : head.Substring(0, elements[11]) + "…";
        }

        private sealed class ShortcutNameConverter : IValueConverter
        {
            internal static readonly ShortcutNameConverter Instance = new();
            public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
                value is string name ? ShortName(name) : "";
            public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
                throw new NotSupportedException();
        }

        internal static Button CreateShortcut(AudioDeviceInfo device)
        {
            var button = new Button { Tag = device };
            if (NameKey(device) is string key)
            {
                string path = "[" + key + "]";
                button.SetBinding(Button.ContentProperty, new Binding(path)
                {
                    Source = L10n.Instance, Mode = BindingMode.OneWay, Converter = ShortcutNameConverter.Instance
                });
                button.SetBinding(AutomationProperties.NameProperty, new Binding(path) { Source = L10n.Instance, Mode = BindingMode.OneWay });
            }
            else
            {
                button.Content = ShortName(device.DisplayName!);
                AutomationProperties.SetName(button, device.DisplayName);
            }
            return button;
        }

        internal static AudioDeviceInfo WithDeviceName(AudioDeviceInfo device, AppConfig config)
        {
            if (config.DeviceNames.TryGetValue(device.Id, out var name) && !string.IsNullOrWhiteSpace(name))
                return new AudioDeviceInfo { Id = device.Id, DisplayName = name, Flow = device.Flow, IsDefault = device.IsDefault };
            return device is not LocalizedDevice && string.IsNullOrWhiteSpace(device.DisplayName)
                ? new LocalizedDevice("Dev.Unknown") { Id = device.Id, Flow = device.Flow, IsDefault = device.IsDefault } : device;
        }

        /// <summary>按可见列表循环；当前项已离线或隐藏时，向后取首项，向前取末项。</summary>
        internal static int CycleIndex(int currentIndex, int count, bool previous, int steps = 1)
        {
            if (count <= 0) return -1;
            if (currentIndex < 0 || currentIndex >= count) currentIndex = previous ? 0 : count - 1;
            int offset = System.Math.Max(1, steps) % count;
            return previous ? (currentIndex + count - offset) % count : (currentIndex + offset) % count;
        }

        /// <summary>构建带"系统默认"虚拟项的显示列表。
        /// 虚拟项按流使用独立 ID（输出 @@SYSTEM_DEFAULT@@ / 输入 @@SYSTEM_DEFAULT_IN@@），
        /// 从而在 HiddenOutputDevices/HiddenInputDevices 与 DeviceNames 中可分别控制显示和命名。
        /// 虚拟项被"保留设备"勾掉时不在列表中出现；自定义名称优先于默认文案。</summary>
        public static List<AudioDeviceInfo> WithSystemDefault(IEnumerable<AudioDeviceInfo> devs, EDataFlow flow, AppConfig config)
        {
            bool isOut = flow == EDataFlow.eRender;
            string id = isOut ? AudioService.SystemDefaultDeviceId : AudioService.SystemDefaultInputDeviceId;
            var hidden = isOut ? config.HiddenOutputDevices : config.HiddenInputDevices;

            var list = new List<AudioDeviceInfo>();
            if (!hidden.Contains(id))
            {
                string key = isOut ? "Dev.SystemDefaultOut" : "Dev.SystemDefaultIn";
                list.Add(WithDeviceName(new LocalizedDevice(key) { Id = id, Flow = flow }, config));
            }
            if (devs != null)
                list.AddRange(devs);
            return list;
        }
    }
}
