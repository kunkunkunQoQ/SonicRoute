using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TextBox = System.Windows.Controls.TextBox;

namespace SonicRoute
{
    public partial class MainWindow
    {
        internal static string ReadProjectLicense()
        {
            using var stream = typeof(App).Assembly.GetManifestResourceStream("SonicRoute.Resources.LICENSE")
                ?? throw new InvalidOperationException("Missing embedded GPL license.");
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        private void LicenseLink_Click(object sender, RoutedEventArgs e)
        {
            // Read only when requested; reuse the themed read-only text viewer
            // pattern used by execution history. No files or background work.
            var text = new TextBox
            {
                Text = "SonicRoute\nCopyright (c) 2026 kunkunkunQoQ\nGPL-3.0-only\n\n" + ReadProjectLicense(),
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(16),
                FontSize = 13
            };
            text.SetResourceReference(BackgroundProperty, "Theme.SurfaceBg");
            text.SetResourceReference(ForegroundProperty, "Theme.TextPrimary");
            var window = new Window
            {
                Title = "SonicRoute · GPL-3.0",
                Owner = this,
                Width = 800,
                Height = 600,
                MinWidth = 400,
                MinHeight = 300,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = text
            };
            window.SetResourceReference(BackgroundProperty, "Theme.WindowBg");
            window.SetResourceReference(ForegroundProperty, "Theme.TextPrimary");
            window.PreviewKeyDown += (_, key) => { if (key.Key == Key.Escape) window.Close(); };
            try { window.ShowDialog(); }
            finally { window.Content = null; }
        }
    }
}
