using System.Collections.Generic;
using SonicRoute.Core;
using SonicRoute.Core.Interop;
using SonicRoute.Core.Models;

namespace SonicRoute
{
    /// <summary>
    /// 设备列表辅助：在显示设备列表头部插入"系统默认输出/输入"虚拟项。
    /// 用于快捷面板（经典/简洁）、完整界面概览/应用页、快捷键循环切换——行为与保留设备完全一致，
    /// 选中即把应用切回跟随系统默认设备（AudioService.ApplyEndpoint 内部清除持久化路由）。
    /// 虚拟项与正常设备一样参与"保留设备"筛选（可勾选隐藏）与"设备名称"自定义（可改名）。
    /// </summary>
    public static class PanelDevices
    {
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
                string name = L10n.T(isOut ? "Dev.SystemDefaultOut" : "Dev.SystemDefaultIn");
                if (config.DeviceNames.TryGetValue(id, out var custom) && !string.IsNullOrWhiteSpace(custom))
                    name = custom;
                list.Add(new AudioDeviceInfo { Id = id, DisplayName = name, Flow = flow });
            }
            if (devs != null)
                list.AddRange(devs);
            return list;
        }
    }
}
