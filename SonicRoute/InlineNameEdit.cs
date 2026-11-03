using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TextBox = System.Windows.Controls.TextBox;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace SonicRoute;

/// <summary>Shared desktop-app and phone inline editing interaction; persistence stays with each owner.</summary>
internal static class InlineNameEdit
{
    internal static void Begin(TextBlock label, TextBox editor)
    {
        if (editor.Visibility != Visibility.Visible) { editor.Text = label.Text; editor.Visibility = Visibility.Visible; }
        editor.Focus(); editor.SelectAll();
    }
    internal static void KeyDown(TextBox editor, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return; // allow IME composition to finish
        e.Handled = true;
        editor.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
    }
}
