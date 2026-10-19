namespace Humanify.UI;

public interface IUserInterface {
    /// <summary>
    /// Writes a log message to the user interface.
    /// </summary>
    void Write(LogLevel level, string message, Exception? ex = null);

    /// <summary>
    /// Starts a context where multiple progress bars can be managed.
    /// </summary>
    Task RunWithProgress(Func<IProgressContext, Task> action);
}

public interface IProgressContext {
/// <summary>
    /// Adds a new progress bar to the current context.
    /// </summary>
    /// <param name="description">Name of the task</param>
    /// <param name="maxValue">Total steps (use 0 or -1 for indeterminate/spinner)</param>
    /// <param name="parent">Optional parent task to insert this task immediately after</param>
    IProgressTask AddTask(string description, double maxValue = 100, IProgressTask? parent = null);
}

public interface IProgressTask {
    /// <summary>
    /// Updates the task's numeric progress.
    /// </summary>
    void Increment(double amount);

    /// <summary>
    /// Sets the task's progress value directly.
    /// </summary>
    double Value { set; }

    /// <summary>
    /// Force completes the task (sets to 100%).
    /// </summary>
    void Complete();

    /// <summary>
    /// Updates the description of the task.
    /// </summary>
    string Description { set; }
}