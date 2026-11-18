#if SONICROUTE_PHONE
using System.Windows;

namespace SonicRoute;

public partial class PhoneDevicePage
{
    private void ConnectionChoice_Changed(object sender, RoutedEventArgs e)
    {
        if (AirPlayCard == null || DirectConnectCard == null) return;
        bool airPlay = AirPlayConnectionChoice.IsChecked == true;
        PairingCard.Visibility = DirectConnectCard.Visibility = airPlay ? Visibility.Collapsed : Visibility.Visible;
        AirPlayCard.Visibility = airPlay ? Visibility.Visible : Visibility.Collapsed;
        UpdateQrPairing();
    }
}
#endif
