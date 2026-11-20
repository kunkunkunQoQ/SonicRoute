using System;
using System.Globalization;
using System.Windows.Data;
using SonicRoute.Core.Interop;
using SonicRoute.Core.Models;

namespace SonicRoute;

public sealed class AudioDeviceLabelConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values[0] is not AudioDeviceInfo device) return string.Empty;
        string name = PanelDevices.NameKey(device) switch
        {
            "Dev.Unknown" => values[1] as string ?? "",
            "Dev.SystemDefaultOut" => values[2] as string ?? "",
            "Dev.SystemDefaultIn" => values[3] as string ?? "",
            "Phone.NativeAudioOutput" => values[5] as string ?? "",
            _ => device.DisplayName ?? ""
        };
        string icon = device.Flow == EDataFlow.eRender ? "🎧 " : "🎤 ";
        string suffix = device.IsDefault ? values[4] as string ?? "" : "";
        return suffix.Length == 0 ? icon + name : icon + name + (suffix[0] == '（' ? "" : " ") + suffix;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
