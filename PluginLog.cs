using System;
using System.IO;
using System.Text;

namespace AreWeThereYet
{
    public static class PluginLog
    {
        private static string _logFilePath;
        private static readonly object FileLock = new object();

        public static void Initialize(string pluginDirectory)
        {
            _logFilePath = Path.Combine(pluginDirectory, "debug.log");
            // Clear the log file on initialization
            try
            {
                File.WriteAllText(_logFilePath, $"--- Log Initialized at {DateTime.Now} ---" + Environment.NewLine);
            }
            catch (Exception)
            {
                // Ignore if we can't write, we'll just log to the game console
            }
        }

        public static void Log(string message, LogLevel level = LogLevel.Info)
        {
            var formattedMessage = $"[{DateTime.Now:HH:mm:ss}] [{level}] {message}";

            // Always log to the game's console
            if (level == LogLevel.Error)
            {
                PluginLog.Log(message, LogLevel.Error);
            }
            else
            {
                AreWeThereYet.Instance.LogMessage(message);
            }

            // Also write to the file if it's initialized
            if (string.IsNullOrEmpty(_logFilePath)) return;

            try
            {
                lock (FileLock)
                {
                    File.AppendAllText(_logFilePath, formattedMessage + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch (Exception)
            {
                // Failsafe, don't crash the plugin if logging fails
            }
        }
    }

    public enum LogLevel
    {
        Info,
        Error,
        Debug
    }
}