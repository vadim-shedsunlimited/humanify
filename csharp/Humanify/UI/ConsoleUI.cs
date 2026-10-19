
namespace Humanify.UI;

public class ConsoleUI : IUserInterface {
    public void Write(LogLevel level, string message, Exception? ex = null) {
        var originalColor = Console.ForegroundColor;
        switch (level) {
            case LogLevel.Debug: Console.ForegroundColor = ConsoleColor.DarkGray; break;
            case LogLevel.Warning: Console.ForegroundColor = ConsoleColor.Yellow; break;
            case LogLevel.Error: Console.ForegroundColor = ConsoleColor.Red; break;
            default: Console.ForegroundColor = ConsoleColor.White; break;
        }

        // If the message starts with a checkmark (used by Unminify.cs), make it Green
        if (message.TrimStart().StartsWith("✓")) {
            Console.ForegroundColor = ConsoleColor.Green;
        }

        // Prefix debug logs to distinguish them
        var prefix = level == LogLevel.Debug ? "[DEBUG] " : "";
        var output = $"{prefix}{message}";

        if (ex != null) {
            output += $"{Environment.NewLine}{ex.GetType().Name}: {ex.Message}";

            // Access Logger.ConsoleLevel to determine if we show stack traces.
            // (You could also pass this in, but accessing the static config is fine for now)
            if (level == LogLevel.Error || Logger.ConsoleLevel == LogLevel.Debug) {
                output += $"{Environment.NewLine}{ex.StackTrace}";
            }
        }

        if (level == LogLevel.Error) Console.Error.WriteLine(output);
        else Console.WriteLine(output);

        Console.ForegroundColor = originalColor;
    }

    public async Task RunWithProgress(Func<IProgressContext, Task> action) {
        // Minimal fallback: just run the action
        await action(new ConsoleContext());
    }

    private class ConsoleContext : IProgressContext {
        public IProgressTask AddTask(string description, double maxValue, IProgressTask? parent = null) {
            Console.WriteLine($"[Start] {description}");
            return new ConsoleTask();
        }
    }

    private class ConsoleTask : IProgressTask {
        public void Increment(double amount) { }
        public double Value { set { } }
        public void Complete() { }
        public string Description { set { } }
    }
}