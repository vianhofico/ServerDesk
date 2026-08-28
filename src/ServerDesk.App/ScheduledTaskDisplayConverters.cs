using System.Globalization;
using System.Windows;
using System.Windows.Data;
using ServerDesk.Application.ScheduledTasks;

namespace ServerDesk.App;

public sealed class ScheduledTaskKindDisplayConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not ScheduledTaskKind kind)
        {
            return "—";
        }

        var key = kind == ScheduledTaskKind.Cron
            ? "Loc.Tasks.Kind.Cron"
            : "Loc.Tasks.Kind.SystemdTimer";
        return System.Windows.Application.Current?.TryFindResource(key) as string ?? key;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
