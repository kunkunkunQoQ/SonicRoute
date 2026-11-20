#if SONICROUTE_PHONE
using System.Windows;

namespace SonicRoute;

public partial class PhoneDevicePage
{
    private void ConnectionChoice_Changed(object sender, RoutedEventArgs e)
    {
        if (AirPlayCard == null || DirectConnectCard == null) return;
        UpdateConnectionChoice();
    }

    private void UpdateConnectionChoice()
    {
        bool airPlay = AirPlayConnectionChoice.IsChecked == true;
        PairingCard.Visibility = DirectConnectCard.Visibility = IsConnectionPage && !airPlay ? Visibility.Visible : Visibility.Collapsed;
        AirPlayCard.Visibility = IsConnectionPage && airPlay ? Visibility.Visible : Visibility.Collapsed;
        UpdateQrPairing();
    }
}
#endif
