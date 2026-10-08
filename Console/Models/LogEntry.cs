namespace MCPPOC.Console.Models;

public enum LogLevel { Debug, Info, Request, Warning, Error, Status }

public sealed record LogEntry(DateTime Time, LogLevel Level, string Source, string Message)
{
    public string TimeText => Time.ToString("HH:mm:ss");
}
