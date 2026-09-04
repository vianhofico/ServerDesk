using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace ServerDesk.App;

public partial class App
{
    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var logPath = TryWriteCrashLog(e.Exception);
        e.Handled = true;

        var vietnamese = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("vi", StringComparison.OrdinalIgnoreCase);
        var message = vietnamese
            ? "ServerDesk gặp lỗi giao diện ngoài dự kiến và sẽ đóng để tránh trạng thái không an toàn.\n\n" +
              (logPath is null ? "Không thể ghi file chẩn đoán." : $"File chẩn đoán: {logPath}")
            : "ServerDesk encountered an unexpected UI error and will close to avoid an unsafe state.\n\n" +
              (logPath is null ? "A diagnostic file could not be written." : $"Diagnostic file: {logPath}");

        try
        {
            MessageBox.Show(
                message,
                vietnamese ? "Lỗi ServerDesk" : "ServerDesk error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            Shutdown(-1);
        }
    }

    private static string? TryWriteCrashLog(Exception exception)
    {
        try
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var logDirectory = Path.Combine(localAppData, "ServerDesk", "Logs");
            Directory.CreateDirectory(logDirectory);
            var logPath = Path.Combine(logDirectory, "startup-crash.log");
            File.AppendAllText(logPath, FormatCrashReport(exception), Encoding.UTF8);
            return logPath;
        }
        catch
        {
            return null;
        }
    }

    internal static string FormatCrashReport(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var builder = new StringBuilder();
        builder.AppendLine("--- ServerDesk UI exception ---");
        builder.Append("UTC: ").AppendLine(DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));

        var current = exception;
        var depth = 0;
        while (current is not null && depth < 8)
        {
            builder.Append("ExceptionType[").Append(depth).Append("]: ")
                .AppendLine(current.GetType().FullName ?? current.GetType().Name);
            builder.Append("HResult[").Append(depth).Append("]: 0x")
                .AppendLine(current.HResult.ToString("X8", CultureInfo.InvariantCulture));
            if (!string.IsNullOrWhiteSpace(current.Source))
            {
                builder.Append("Source[").Append(depth).Append("]: ").AppendLine(current.Source);
            }

            if (current.TargetSite is { } targetSite)
            {
                builder.Append("Target[").Append(depth).Append("]: ")
                    .Append(targetSite.DeclaringType?.FullName ?? "<unknown>")
                    .Append('.')
                    .AppendLine(targetSite.Name);
            }

            if (!string.IsNullOrWhiteSpace(current.StackTrace))
            {
                builder.Append("Stack[").Append(depth).AppendLine("]:");
                builder.AppendLine(current.StackTrace);
            }

            current = current.InnerException;
            depth++;
        }

        // Deliberately omit Exception.Message and exception data because remote/user values can contain secrets.
        builder.AppendLine("Messages and exception Data are intentionally omitted from this secret-safe diagnostic.");
        builder.AppendLine();
        return builder.ToString();
    }
}
