using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace ServerDesk.App;

public partial class ScheduledTasksWindow
{
    private DependencyPropertyDescriptor? _statusTextDescriptor;

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        if (_statusTextDescriptor is null)
        {
            _statusTextDescriptor = DependencyPropertyDescriptor.FromProperty(
                TextBlock.TextProperty,
                typeof(TextBlock));
            _statusTextDescriptor?.AddValueChanged(StatusText, StatusTextOnValueChanged);
        }

        RefreshOverlay();
    }

    protected override void OnClosed(EventArgs e)
    {
        _statusTextDescriptor?.RemoveValueChanged(StatusText, StatusTextOnValueChanged);
        _statusTextDescriptor = null;
        base.OnClosed(e);
    }

    private void StatusTextOnValueChanged(object? sender, EventArgs e) => RefreshOverlay();
}
