using Humanify.UI;

namespace Humanify;

public enum LogLevel {
    Debug = 0,
    Info = 1,
    Warning = 2,
    Error = 3,
    None = 4
}

public static class Logger {
    /// <summary>
    /// The minimum level to show in the Console.
    /// Default is Info (hides Debug).
    /// </summary>
    public static LogLevel ConsoleLevel { get; set; } = LogLevel.Info;

    /// <summary>
    /// The minimum level to write to the File.
    /// Default is Debug (logs everything to file if file logging is on).
    /// </summary>
    public static LogLevel FileLevel { get; set; } = LogLevel.Debug;

    // The Injected Dependency (Singleton)
    private static IUserInterface _ui = new ConsoleUI(); // Default fallback

    private static string? _logFilePath;
    private static object _fileLock = new();

    /// <summary>
    /// Initializes the global log file. Call this at the start of your program.
    /// </summary>
    public static void Initialize(string filePath, IUserInterface ui) {
        _ui = ui;
        _logFilePath = filePath;
        try {
            // Ensure directory exists
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // Start fresh log on every run
            lock (_fileLock) {
                File.WriteAllText(_logFilePath, $"=== Humanify Log Started {DateTime.Now} ==={Environment.NewLine}");
            }
        } catch (Exception ex) {
            Console.Error.WriteLine($"[System] Warning: Could not initialize log file: {ex.Message}");
        }
    }

    public static void Log(LogLevel level, Exception? ex, string message, params object[] args) {
        if (args.Length > 0) message = string.Format(message, args);

        if (level >= ConsoleLevel) {
            _ui.Write(level, message, ex);
        }

        if (level >= FileLevel && !string.IsNullOrEmpty(_logFilePath)) {
            WriteToFile(level, message, ex);
        }
    }

    public static void LogDebug(string message, params object[] args) => Log(LogLevel.Debug, null, message, args);
    public static void LogInfo(string message, params object[] args) => Log(LogLevel.Info, null, message, args);
    public static void LogWarning(string message, params object[] args) => Log(LogLevel.Warning, null, message, args);
    public static void LogError(string message, params object[] args) => Log(LogLevel.Error, null, message, args);

    public static void LogWarning(Exception ex, string message, params object[] args) => Log(LogLevel.Warning, ex, message, args);
    public static void LogError(Exception ex, string message, params object[] args) => Log(LogLevel.Error, ex, message, args);

    private static void WriteToFile(LogLevel level, string message, Exception? ex) {
        if (string.IsNullOrEmpty(_logFilePath)) return;

        var levelText = level.ToString().ToUpper();
        var centeredLevel = CenterText(levelText, 7);

        var logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{centeredLevel}] {message}";
        if (ex != null) {
            logEntry += $"{Environment.NewLine}EXCEPTION: {ex}";
        }

        lock (_fileLock) {
            try {
                File.AppendAllText(_logFilePath, logEntry + Environment.NewLine);
            } catch { /* Ignore file write errors */ }
        }
    }

    static string CenterText(string s, int width) {
        if (s == null) s = string.Empty;
        if (s.Length >= width) return s;
        int left = (width - s.Length) / 2 + s.Length;
        return s.PadLeft(left).PadRight(width);
    }

    public static async Task RunWithProgress(Func<IProgressContext, Task> action) {
        await _ui.RunWithProgress(action);
    }
}