namespace LMCL.Core.Diagnostics;

public enum LogLevel
{
    Debug,
    Info,
    Success,
    Warning,
    Error,
}

public sealed record LogEntry(DateTime Time, LogLevel Level, string Message)
{
    public string TimeText => Time.ToString("HH:mm:ss");

    public override string ToString() => $"[{TimeText}] {Message}";
}

public interface ILogSink
{
    void Write(LogEntry entry);
}

public sealed class ActionLogSink : ILogSink
{
    private readonly Action<LogEntry> _sink;

    public ActionLogSink(Action<LogEntry> sink) => _sink = sink;

    public void Write(LogEntry entry) => _sink(entry);
}

public static class LogSinkExtensions
{
    public static void Debug(this ILogSink? sink, string message) =>
        sink?.Write(new LogEntry(DateTime.Now, LogLevel.Debug, message));

    public static void Info(this ILogSink? sink, string message) =>
        sink?.Write(new LogEntry(DateTime.Now, LogLevel.Info, message));

    public static void Success(this ILogSink? sink, string message) =>
        sink?.Write(new LogEntry(DateTime.Now, LogLevel.Success, message));

    public static void Warn(this ILogSink? sink, string message) =>
        sink?.Write(new LogEntry(DateTime.Now, LogLevel.Warning, message));

    public static void Error(this ILogSink? sink, string message) =>
        sink?.Write(new LogEntry(DateTime.Now, LogLevel.Error, message));
}
