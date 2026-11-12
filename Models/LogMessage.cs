using System;

namespace SmartUnrealLocalization.Models
{
    public enum LogLevel
    {
        Info,
        Warning,
        Error
    }

    public class LogMessage
    {
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public LogLevel Level { get; set; }
        public string Message { get; set; } = "";

        public string DisplayTimestamp => Timestamp.ToString("HH:mm:ss");

        // The log list shows a localized label per level; these pick which one.
        // (It printed the enum name — "Info" under 情報/警告/エラー filter tabs.)
        public bool IsError => Level == LogLevel.Error;
        public bool IsWarning => Level == LogLevel.Warning;
        public bool IsInfo => Level == LogLevel.Info;
    }
}
