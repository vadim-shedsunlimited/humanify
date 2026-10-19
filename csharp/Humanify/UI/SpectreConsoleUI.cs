
using Spectre.Console;

namespace Humanify.UI;

public class SpectreConsoleUI : IUserInterface {
    public void Write(LogLevel level, string message, Exception? ex = null) {
        // 1. Color/Style Selection
        var style = level switch {
            LogLevel.Debug => "grey",
            LogLevel.Warning => "yellow",
            LogLevel.Error => "red",
            _ => "white"
        };

        // Special handling for success marks
        if (message.TrimStart().StartsWith("✓")) style = "green";
        if (message.TrimStart().StartsWith("✗")) style = "red";

        // Escape Markup to prevent crashes if message contains '[' or ']'
        var escaped = Markup.Escape(message);
        var timestamp = DateTime.Now.ToString("HH:mm:ss");

        // We add a timestamp to the log since we have the screen real estate
        AnsiConsole.MarkupLine($"[grey]{timestamp}[/] [{style}]{escaped}[/]");

        if (ex != null) {
            if (level == LogLevel.Error) AnsiConsole.WriteException(ex, ExceptionFormats.ShortenEverything);
            else AnsiConsole.MarkupLine($"[red]  {Markup.Escape(ex.Message)}[/]");
        }
    }

    public async Task RunWithProgress(Func<IProgressContext, Task> action) {
        // Custom column configuration for a rich display
        await AnsiConsole.Progress()
            .AutoClear(false)
            .HideCompleted(true) // <--- Key feature: File bars vanish when done
            .Columns(new ProgressColumn[]
            {
                new TaskDescriptionColumn(),    // "Processing file.js"
                new ProgressBarColumn(),        // [====----]
                new PercentageColumn(),         // 50%
                new SpinnerColumn(),            // /
                new ElapsedTimeColumn(),        // 00:05
            })
            .StartAsync(async ctx => {
                await action(new SpectreContext(ctx));
            });
    }

    private class SpectreContext : IProgressContext {
        private readonly ProgressContext _ctx;
        public SpectreContext(ProgressContext ctx) => _ctx = ctx;

        public IProgressTask AddTask(string description, double maxValue, IProgressTask? parent = null) {
            ProgressTask task;

            // Check if we have a parent to insert after
            if (parent is SpectreTask parentTask) {
                // Insert the new task immediately after the parent task
                task = _ctx.AddTaskAfter(description, parentTask.InternalTask, true, maxValue > 0 ? maxValue : 100);
            } else {
                // Standard append behavior
                task = _ctx.AddTask(description, maxValue: maxValue > 0 ? maxValue : 100);
            }

            if (maxValue <= 0) task.IsIndeterminate = true;

            return new SpectreTask(task);
        }
    }

    private class SpectreTask : IProgressTask {
        internal readonly ProgressTask InternalTask; // Expose internal task to the Context       

        public SpectreTask(ProgressTask task) => InternalTask = task;

        public void Increment(double amount) {
            // If we are incrementing, we are making specific progress, so stop spinning.
            if (InternalTask.IsIndeterminate) InternalTask.IsIndeterminate = false;
            InternalTask.Increment(amount);
        }
        public double Value {
            set {
                // When a specific value is set (e.g. 10%), switch to the Bar view.
                if (InternalTask.IsIndeterminate) InternalTask.IsIndeterminate = false;
                InternalTask.Value = value;
            }
        }
        public void Complete() {
            InternalTask.IsIndeterminate = false; // Ensure it finishes as a full bar
            InternalTask.Value = InternalTask.MaxValue;
        }
        public string Description { set => InternalTask.Description = value; }
    }
}