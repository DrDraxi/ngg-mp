using System;

namespace NGGMultiplayer.Core.Diagnostics
{
    public enum LogLevel : byte
    {
        Debug,
        Info,
        Warning,
        Error
    }

    /// <summary>
    /// Minimal logging facade for Core. The plugin points <see cref="Sink"/> at the BepInEx logger;
    /// with no sink, messages are discarded. A throwing sink never propagates into Core.
    /// </summary>
    public static class NetLog
    {
        private static volatile Action<LogLevel, string> _sink;

        public static Action<LogLevel, string> Sink
        {
            get => _sink;
            set => _sink = value;
        }

        public static LogLevel MinLevel { get; set; } = LogLevel.Info;

        public static bool IsEnabled(LogLevel level) => _sink != null && level >= MinLevel;

        public static void Debug(string message) => Write(LogLevel.Debug, message);

        public static void Info(string message) => Write(LogLevel.Info, message);

        public static void Warn(string message) => Write(LogLevel.Warning, message);

        public static void Error(string message) => Write(LogLevel.Error, message);

        public static void Write(LogLevel level, string message)
        {
            var sink = _sink;
            if (sink == null || level < MinLevel)
                return;
            try
            {
                sink(level, message);
            }
            catch
            {
                // Logging must never take down networking.
            }
        }
    }
}
