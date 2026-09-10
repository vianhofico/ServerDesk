using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ServerDesk.App;

public partial class RemoteExplorerWindow
{
    private void WindowOnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.L)
        {
            e.Handled = true;
            SetAddressEditing(true);
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F)
        {
            e.Handled = true;
            SearchBox.Focus();
            SearchBox.SelectAll();
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Alt)
        {
            if (e.Key == Key.Left && BackButton.IsEnabled)
            {
                e.Handled = true;
                BackOnClick(sender, e);
                return;
            }

            if (e.Key == Key.Right && ForwardButton.IsEnabled)
            {
                e.Handled = true;
                ForwardOnClick(sender, e);
                return;
            }

            if (e.Key == Key.Up && UpButton.IsEnabled)
            {
                e.Handled = true;
                UpOnClick(sender, e);
                return;
            }
        }

        if (Keyboard.Modifiers != ModifierKeys.None)
        {
            return;
        }

        if (e.Key == Key.F5 && RefreshButton.IsEnabled)
        {
            e.Handled = true;
            RefreshOnClick(sender, e);
            return;
        }

        if (e.Key == Key.Escape)
        {
            if (_isAddressEditing)
            {
                e.Handled = true;
                SetAddressEditing(false);
                return;
            }

            if (IsBusy)
            {
                e.Handled = true;
                CancelActiveOperation();
                return;
            }

            if (SearchBox.IsKeyboardFocusWithin && !string.IsNullOrEmpty(SearchBox.Text))
            {
                e.Handled = true;
                SearchBox.Clear();
            }

            return;
        }

        if (Keyboard.FocusedElement is TextBox or PasswordBox)
        {
            return;
        }

        if (e.Key == Key.F2 && RenameButton.IsEnabled)
        {
            e.Handled = true;
            RenameOnClick(sender, e);
            return;
        }

        if (e.Key == Key.Delete && FileGrid.IsKeyboardFocusWithin && DeleteButton.IsEnabled)
        {
            e.Handled = true;
            DeleteOnClick(sender, e);
        }
    }
}
