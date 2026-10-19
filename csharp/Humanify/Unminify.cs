using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Humanify.Plugins;
using Humanify.Plugins.Webcrack;
using Humanify.UI;

namespace Humanify;

using AstProgram = Acornima.Ast.Program;

// --- Manifest Data Structures ---
public record BundleManifest(string? Type, List<BundleModule>? Modules);
public record BundleModule(string? Id, string? Path);

public record UnminifyOptions(
    bool Resume,
    bool ContinueOnError,
    int Agents = 1,
    int ParallelFiles = 1,
    string? PipelineId = null,
    string? RootDirectory = null
);

public class TaskState {
    public string Path { get; set; } = "";
    public string InputHash { get; set; } = "";
    /// <summary>
    /// Output Hash to verify integrity on resume
    /// </summary>
    public string? OutputHash { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public TaskStatus Status { get; set; } = TaskStatus.Pending;
    public string? Result { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? Error { get; set; }
}

public enum TaskStatus { Pending, Processing, Completed, Failed }

public class TaskStateFile {
    public List<TaskState> Tasks { get; set; } = new();
    public string? PipelineId { get; set; }
    public string? InputBundleHash { get; set; } // Tracks the hash of the source files (CLI inputs)
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public static class Unminify {
    private static readonly SemaphoreSlim _stateLock = new(1, 1);

    /// <summary>
    /// Configuration for long-running stability
    /// </summary>
    private const int SAVE_INTERVAL_FILES = 1;
    /// <summary>
    /// Trigger GC every 'X' files to clean up large AST objects
    /// </summary>
    private const int GC_INTERVAL_FILES = 20;

    public static async Task<int> Execute(
        string[] inputs,
        string outputDir,
        List<IPlugin> plugins,
        UnminifyOptions options
    ) {
        var resume = options.Resume;
        var continueOnError = options.ContinueOnError;
        var agents = Math.Max(1, options.Agents);

        // Define Subdirectories
        var inputDir = Path.Combine(outputDir, "input");
        var resultDir = Path.Combine(outputDir, "result");

        Directory.CreateDirectory(inputDir);
        Directory.CreateDirectory(resultDir);

        // Resolve Files
        List<string> sourceFiles = [];
        foreach (var input in inputs) {
            if (File.Exists(input)) sourceFiles.Add(Path.GetFullPath(input));
            else if (Directory.Exists(input)) sourceFiles.AddRange(Directory.GetFiles(input, "*.js", SearchOption.AllDirectories));
        }

        if (sourceFiles.Count == 0) {
            Logger.LogInfo("No input files found.");
            return 1;
        }

        // Load State & Intelligent Setup
        var stateFilePath = Path.Combine(outputDir, ".humanify.tasks.json");
        var state = await LoadTaskState(stateFilePath);
        // Check if source inputs have changed
        var currentInputHash = await ComputeCombinedHash(sourceFiles);

        // Wipe if inputs changed or not resuming
        if (state.InputBundleHash != currentInputHash || !resume) {
            Logger.LogInfo("Preparing workspace...");

            if (Directory.Exists(inputDir)) Directory.Delete(inputDir, true);
            Directory.CreateDirectory(inputDir);

            // Explode/Copy files
            await Webcrack.Prepare(sourceFiles, inputDir);

            state.InputBundleHash = currentInputHash;
            state.Tasks.Clear(); // Invalidate old tasks since files changed
            await SaveTaskState(stateFilePath, state);
        }

        // Scan for all JS files first
        var allFiles = Directory.GetFiles(inputDir, "*.js", SearchOption.AllDirectories);

        // Filter artifacts (Prioritize Manifest > Deobfuscated > All)
        var inputFilesToProcess = await FilterArtifacts(allFiles);

        Logger.LogInfo($"Found {inputFilesToProcess.Count} modules to process (filtered from {allFiles.Length} files).");

        // Pipeline Invalidation Logic
        // var pipelineId = options.PipelineId;
        // if (!string.IsNullOrEmpty(options.PipelineId)) {
        //     // In TS this hashes the pipeline ID configuration object, simpler here to just compare IDs
        //     if (state.PipelineId != options.PipelineId) {
        //         Verbose.Log($"Pipeline configuration changed (old: {state.PipelineId}, new: {pipelineId}). Resetting completed tasks.");
        //         foreach (var t in state.Tasks) {
        //             if (t.Status == TaskStatus.Completed || t.Status == TaskStatus.Failed) {
        //                 t.Status = TaskStatus.Pending;
        //                 t.Error = "Pipeline changed; re-queued";
        //             }
        //         }
        //     }
        //     state.PipelineId = options.PipelineId;
        // }

        // Build Dependency Graph
        // We do this BEFORE filtering for completed tasks because we need the full graph 
        // to calculate the correct processing order for the remaining files.
        var graph = new DependencyGraph();
        await graph.Build(inputFilesToProcess);
        graph.PrintDependencyTree(inputDir);

        var layers = graph.GetExecutionLayers();
        Logger.LogInfo($"Computed {layers.Count} execution layers based on dependencies.");

        // Shared Cache for File Content across all plugins and files
        var sharedFileCache = new ConcurrentDictionary<string, string>();

        // Create the Blackboard (persists for the whole run)
        // Key:   "PluginIdentifier:InstanceId"
        // Value: Dictionary of "Hash(Content)" -> "Result"
        var blackboard = new ConcurrentDictionary<string, ConcurrentDictionary<string, string>>();

        // Stats tracking
        var processedThisRun = 0;
        var failedThisRun = 0;
        var totalFiles = inputFilesToProcess.Count;
        int filesCompletedPreviously = 0;

        // Sync local task state with files found
        foreach (var file in inputFilesToProcess) {
            string relPath = Path.GetRelativePath(inputDir, file);
            var existing = state.Tasks.FirstOrDefault(t => t.Path == relPath);

            if (existing == null) {
                state.Tasks.Add(new TaskState { Path = relPath, Status = TaskStatus.Pending });
            } else if (existing.Status == TaskStatus.Completed) {
                var expectedOutputPath = Path.Combine(resultDir, relPath);
                bool isValid = false;

                if (File.Exists(expectedOutputPath)) {
                    // Verify content hash matches what we recorded
                    var currentOutputHash = await ComputeFileHash(expectedOutputPath);
                    if (string.Equals(currentOutputHash, existing.OutputHash, StringComparison.OrdinalIgnoreCase)) {
                        isValid = true;
                    } else {
                        Logger.LogWarning($"[Resume] Hash mismatch for {relPath}. Invalidating task.");
                    }
                } else {
                    Logger.LogWarning($"[Resume] Output file missing for {relPath}. Invalidating task.");
                }

                if (isValid) {
                    filesCompletedPreviously++;
                } else {
                    // Reset task to ensure it runs again
                    existing.Status = TaskStatus.Pending;
                    existing.OutputHash = null;
                    existing.Error = null;
                }
            }
        }

        await SaveTaskState(stateFilePath, state);

        if (filesCompletedPreviously == totalFiles) {
            Logger.LogInfo("All files already processed.");
            return 0;
        }

        Logger.LogInfo($"Processing {totalFiles - filesCompletedPreviously} files using {agents} agents...");

        // Prepare tasks (Smart Resume / Integrity Check)
        var tasks = new List<Task>();
        var taskQueue = new ConcurrentQueue<(string file, TaskState task)>();

        await Logger.RunWithProgress(async (ctx) => {
            // Overall Progress Bar
            var overallTask = ctx.AddTask("[bold green]Overall Progress[/]", inputFilesToProcess.Count);
            overallTask.Value = filesCompletedPreviously; // Restore progress if resuming

            // Execute Layers
            // We process one layer at a time. The semaphore limits concurrency WITHIN the layer.
            var layerIndex = 0;

            foreach (var layer in layers) {
                layerIndex++;

                // This list contains ALL files in the layer, regardless of status
                var layerFiles = layer
                    .Select(f => new { FullPath = f, RelPath = Path.GetRelativePath(inputDir, f) })
                    .ToList();

                // This list contains only the ones we actually need to process
                var layerFilesToProcess = layerFiles
                    .Where(f => {
                        var t = state.Tasks.FirstOrDefault(x => x.Path == f.RelPath);
                        return t == null || t.Status != TaskStatus.Completed;
                    })
                    .ToList();

                if (layerFilesToProcess.Count == 0 && layerFiles.Count > 0) {
                    Logger.LogInfo($"[Resume] Skipping entire layer {layerIndex}/{layers.Count} as all files are already processed.");
                    continue;
                }

                Logger.LogInfo($"\n --- Layer {layerIndex}/{layers.Count} ({layerFilesToProcess.Count} to process, {layerFiles.Count} total) --- \n");

                // Layer Progress Bar
                // We assume layer tasks are indeterminate steps or just count files
                var layerTask = ctx.AddTask($"[bold cyan]Layer {layerIndex}/{layers.Count}[/]", layerFilesToProcess.Count);

                // Initialize counter with already completed files in this layer
                int layerCompleted = layerFiles.Count - layerFilesToProcess.Count;

                await Parallel.ForEachAsync(layerFilesToProcess, new ParallelOptions { MaxDegreeOfParallelism = options.ParallelFiles }, async (item, ct) => {
                    var taskState = state.Tasks.First(t => t.Path == item.RelPath);

                    // File Progress Bar (Indeterminate Spinner)
                    // We use max value 0 to trigger spinner mode in our adapter
                    var fileTask = ctx.AddTask($"Processing {Path.GetFileName(item.RelPath)}", 0);

                    int attempt = 0;
                    const int MAX_ATTEMPTS = 5;
                    bool success = false;

                    while (!success && attempt < MAX_ATTEMPTS) {
                        attempt++;

                        try {
                            // Pass 'false' to ensure ProcessFile throws on error for retry logic
                            await ProcessFile(item.FullPath, taskState, resultDir, plugins, false, sharedFileCache, blackboard, ctx, fileTask);
                            success = true;
                        } catch (Exception ex) {
                            if (attempt < MAX_ATTEMPTS) {
                                Logger.LogWarning($"[Retry] Task {item.RelPath} failed (Attempt {attempt}/{MAX_ATTEMPTS}). Retrying...");

                                // Optional: Add a small backoff delay to let API rate limits settle
                                await Task.Delay(1000 * attempt, ct);

                                // Reset status for next attempt
                                taskState.Status = TaskStatus.Pending;
                                taskState.Error = null;
                            } else {
                                // Final failure: Allow ProcessFile's exception handling (or manual handling) to take over
                                // We set the state manually here since we bypassed ProcessFile's catch block by passing continueOnError: false
                                taskState.Status = TaskStatus.Failed;
                                taskState.CompletedAt = DateTime.UtcNow;
                                taskState.Error = ex.Message;

                                Logger.LogError(ex, $"[Fatal] {item.RelPath}: Failed after {MAX_ATTEMPTS} attempts.");

                                // Only increment failure count on the final attempt
                                Interlocked.Increment(ref failedThisRun);
                                fileTask.Description = $"[red]Failed[/] {item.RelPath}";
                                fileTask.Complete();
                            }
                        }
                    }

                    if (success) {
                        // Atomic increment returns the NEW value, ensuring unique triggers
                        int count = Interlocked.Increment(ref processedThisRun);
                        fileTask.Complete(); // Only complete the file task when ALL plugins are done
                        layerTask.Increment(1);
                        overallTask.Increment(1);

                        int currentLayerCount = Interlocked.Increment(ref layerCompleted);
                        Logger.LogInfo($"✓ [{currentLayerCount}/{layerFiles.Count}] {item.RelPath}");

                        // Autosave periodically
                        if (count % SAVE_INTERVAL_FILES == 0) {
                            _ = SaveTaskState(stateFilePath, state);
                        }

                        // Garbage Collection
                        // We force a collection to prevent memory bloat from thousands of ASTs
                        if (count % GC_INTERVAL_FILES == 0) {
                            Logger.LogDebug("[System] Triggering Garbage Collection...");
                            GC.Collect();
                            GC.WaitForPendingFinalizers();
                        }
                    }
                });


                Logger.LogInfo($"\n --- Layer {layerIndex}/{layers.Count} Completed --- \n");

                // Checkpoint after every layer to ensure dependency consistency for next layer
                await SaveTaskState(stateFilePath, state);
            }

            overallTask.Complete();
        });


        // Print summary
        Logger.LogInfo($"\nDone. Processed: {processedThisRun}, Failed: {failedThisRun}");
        return failedThisRun > 0 ? 1 : 0;
    }

    /// <summary>
    /// Filters files based on priority:
    /// 1. bundle.json (Strict list of modules)
    /// 2. deobfuscated.js (Single file fallback)
    /// 3. All files (Loose fallback)
    /// </summary>
    private static async Task<List<string>> FilterArtifacts(string[] allFiles) {
        var result = new List<string>();

        // Group files by their directory to process each bundle separately
        var filesByDir = allFiles.GroupBy(Path.GetDirectoryName);

        JsonSerializerOptions jsonOptions = new() {
            PropertyNameCaseInsensitive = true
        };

        foreach (var dirGroup in filesByDir) {
            var dirPath = dirGroup.Key;
            if (string.IsNullOrEmpty(dirPath)) continue;

            var filesInDir = dirGroup.ToList();
            var bundleJsonPath = Path.Combine(dirPath, "bundle.json");

            // STRATEGY 1: Explicit Manifest (Best)
            if (File.Exists(bundleJsonPath)) {
                try {
                    var json = await File.ReadAllTextAsync(bundleJsonPath);
                    var manifest = JsonSerializer.Deserialize<BundleManifest>(json, jsonOptions);

                    if (manifest != null && manifest.Modules != null) {
                        Logger.LogDebug($"[Filter] Found Webcrack manifest in {Path.GetFileName(dirPath)}. Using explicit module list.");

                        bool hasValidModules = false;
                        foreach (var mod in manifest.Modules) {
                            if (string.IsNullOrWhiteSpace(mod.Path)) continue;

                            // The path in JSON is usually relative like "./81499.js"
                            var cleanPath = mod.Path.TrimStart('.', '/', '\\');
                            var fullPath = Path.GetFullPath(Path.Combine(dirPath, cleanPath));

                            if (File.Exists(fullPath)) {
                                result.Add(fullPath);
                                hasValidModules = true;
                            } else {
                                Logger.LogWarning($"[Filter] Manifest listed {mod.Path} but file was not found.");
                            }
                        }

                        if (hasValidModules) continue; // Success, move to next directory
                    }
                } catch (Exception ex) {
                    Logger.LogWarning($"[Filter] Failed to parse bundle.json in {dirPath}: {ex.Message}. Falling back.");
                }
            }

            // STRATEGY 2: Deobfuscated File Fallback
            // If manifest failed or didn't exist, check for deobfuscated.js
            var deobfuscatedFile = filesInDir.FirstOrDefault(f => Path.GetFileName(f).Equals("deobfuscated.js", StringComparison.OrdinalIgnoreCase));
            if (deobfuscatedFile != null) {
                Logger.LogDebug($"[Filter] Using 'deobfuscated.js' for {Path.GetFileName(dirPath)} (Manifest missing/invalid).");
                result.Add(deobfuscatedFile);
                continue;
            }

            // STRATEGY 3: Loose Fallback
            // No manifest, no main output file. Assume all files are relevant.
            result.AddRange(filesInDir);
        }
        return result;
    }

    private static async Task ProcessFile(
        string filePath,
        TaskState task,
        string outputDir,
        List<IPlugin> plugins,
        bool continueOnError,
        ConcurrentDictionary<string, string> sharedCache,
        ConcurrentDictionary<string, ConcurrentDictionary<string, string>> blackboard,
        IProgressContext ctx,
        IProgressTask fileTask
    ) {
        try {
            task.Status = TaskStatus.Processing;
            task.StartedAt = DateTime.UtcNow;

            // Set parent description
            fileTask.Description = $"Processing {Path.GetFileName(task.Path)}";

            // Read input file
            var code = await File.ReadAllTextAsync(filePath);

            // This variable holds the "current" valid AST.
            // It starts null, meaning "not parsed yet".
            AstProgram? currentAst = null;

            const int MAX_PLUGIN_RETRIES = 3;

            // Keep track of successful states to allow "stepping back"
            var history = new List<(string Code, AstProgram? Ast)>();

            // Track where we failed last to implement "Cascading Rollback"
            int lastFailedIndex = -1;
            int consecutiveFailures = 0;

            // Calculate total weight of the pipeline for normalization
            double totalWeight = plugins.Sum(p => p.Weight);
            double accumulatedWeight = 0;

            // Apply plugins sequentially
            for (int i = 0; i < plugins.Count; ++i) {
                var plugin = plugins[i];
                double currentPluginWeight = plugin.Weight;

                int attempts = 0;
                bool pluginSuccess = false;

                // Create SUB-TASK immediately after the file task
                // We use Markup.Escape to prevent crashes if plugin name has brackets
                var pluginTask = ctx.AddTask($"  [grey]↳[/] {Spectre.Console.Markup.Escape(plugin.Name)}", 100, fileTask);

                // Capture 'accumulatedWeight' for the closure (since it changes in the loop)
                double startWeight = accumulatedWeight;

                // Adapter: Map 0.0-1.0 from Plugin to 0-100 on UI
                var progressReporter = new Progress<double>(percent => {
                    // Sub-task progress (0-100%)
                    pluginTask.Value = percent * 100;

                    // Parent Task Weighted Progress
                    // Formula: (PreviousWeights + (MyWeight * MyPercent)) / TotalWeight
                    double weightedProgress = (startWeight + (currentPluginWeight * percent)) / totalWeight;
                    fileTask.Value = weightedProgress * 100;
                });

                while (!pluginSuccess && attempts < MAX_PLUGIN_RETRIES) {
                    attempts++;
                    try {
                        // Update status on the parent task (optional, helps if sub-task is indeterminate)
                        // fileTask.Description = $"Processing {Path.GetFileName(task.Path)} ([grey]{plugin.Name}[/])";

                        // Create context (Passing the current valid AST if we have one, AND shared cache)
                        var context = new PluginContext(task.Path, code, currentAst, sharedCache, blackboard, progressReporter);

                        // Execute plugin
                        var (newCode, newAst) = await plugin.ProcessAsync(context);

                        // If successful, save state and move to next plugin
                        history.Add((code, currentAst)); // Save state BEFORE this plugin ran

                        // State Update Logic
                        if (newAst != null) {
                            // BEST CASE: Plugin gave us a valid AST. Use it.
                            code = newCode;
                            currentAst = newAst;
                        } else if (newCode != code) {
                            // INVALIDATION: Code changed, but no AST returned.
                            // The old AST is now invalid (ranges are wrong).
                            code = newCode;
                            currentAst = null;
                        } else {
                            // PASS-THROUGH: Code didn't change (e.g. Documentation plugin that found nothing).
                            // Keep the AST we already had (from the context).
                            // No changes needed to 'code' or 'currentAst'.
                        }
                        pluginSuccess = true;

                        // Complete the sub-task so it (potentially) vanishes or shows 100%
                        pluginTask.Value = 100;
                        pluginTask.Complete();
                    } catch (Exception ex) {
                        Logger.LogWarning(ex, $"[Plugin Retry] {plugin.Name} failed on {task.Path} (Attempt {attempts}/{MAX_PLUGIN_RETRIES}).");

                        // --- CASCADING ROLLBACK LOGIC ---
                        // Detect if we are stuck
                        if (i == lastFailedIndex) {
                            consecutiveFailures++;
                        } else {
                            lastFailedIndex = i;
                            consecutiveFailures = 1;
                        }

                        // Calculate how far to step back
                        // If we failed once, go back 1 step (i-1). 
                        // If we failed twice at this same spot, go back 2 steps (i-2), etc.
                        int stepsToRollback = consecutiveFailures;
                        int targetHistoryIndex = (i - stepsToRollback); // e.g. 4 - 1 = 3 (Integrity)

                        // Check feasibility
                        if (attempts < MAX_PLUGIN_RETRIES && targetHistoryIndex >= 0) {
                            // Restore state from the deeper history point
                            var recoveryState = history[targetHistoryIndex];
                            code = recoveryState.Code;
                            currentAst = recoveryState.Ast;

                            // Invalidate Cache for the plugins we are re-visiting
                            // We are resetting execution to 'targetHistoryIndex'. 
                            // We should clear the cache for that plugin so it regenerates fresh data.
                            for (int k = targetHistoryIndex; k <= i; ++k) {
                                var p = plugins[k];
                                string key = $"{p.Identifier}:{p.InstanceId}";
                                if (blackboard.TryGetValue(key, out var pCache)) {
                                    p.InvalidateCache(task.Path, pCache);
                                }
                            }

                            // Wipe history after that point (we are rewriting this timeline)
                            // Remove range: from 'targetHistoryIndex' to the end
                            int itemsToRemove = history.Count - targetHistoryIndex;
                            history.RemoveRange(targetHistoryIndex, itemsToRemove);

                            // Recalculate accumulated weight up to the point we are rolling back to
                            accumulatedWeight = plugins.Take(targetHistoryIndex).Sum(p => p.Weight);

                            // Move loop index 'i' back
                            // We want the NEXT iteration to be (targetHistoryIndex).
                            // The loop does ++i, so we set i = targetHistoryIndex - 1
                            string targetPluginName = plugins[targetHistoryIndex].Name;
                            i = targetHistoryIndex - 1;

                            Logger.LogInfo($"[Rollback] Plugin {plugin.Name} is stuck. Rolling back {stepsToRollback} steps to '{targetPluginName}' to attempt recovery.");

                            // Fail the current plugin task visibly
                            pluginTask.Description = $"[red]Failed (Retrying)[/] {plugin.Name}";
                            pluginTask.Complete();

                            break; // Break the 'while' retry loop to trigger the 'for' loop update
                        } else if (attempts >= MAX_PLUGIN_RETRIES) {
                            pluginTask.Description = $"[red]Failed[/] {plugin.Name}";
                            pluginTask.Complete();
                            // Can't go back further or out of retries
                            throw new Exception($"Plugin {plugin.Name} failed indefinitely. Stuck at step {i} after {attempts} attempts and {consecutiveFailures} rollbacks. Error: {ex.Message}", ex);
                        }
                    }
                }

                // Add this plugin's weight to the pile for the next iteration
                accumulatedWeight += currentPluginWeight;
            }

            // Determine output path
            var outputPath = Path.Combine(outputDir, task.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            // Write result
            await File.WriteAllTextAsync(outputPath, code);

            task.Status = TaskStatus.Completed;
            task.CompletedAt = DateTime.UtcNow;
            task.OutputHash = await ComputeFileHash(outputPath);
        } catch (Exception ex) {
            task.Status = TaskStatus.Failed;
            task.CompletedAt = DateTime.UtcNow;
            task.Error = ex.Message;

            Logger.LogError(ex, $"✗ {task.Path}: {ex.Message}");
            if (!continueOnError) {
                throw;
            }
        }
    }

    private static async Task<string> ComputeFileHash(string filePath) {
        using var sha1 = SHA1.Create();
        using var stream = File.OpenRead(filePath);
        var hash = await sha1.ComputeHashAsync(stream);
        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
    }

    private static async Task<string> ComputeCombinedHash(List<string> filePaths) {
        // Simple combined hash of file paths + modified times (or content)
        // Using content is safer but slower. Let's use content for correctness.
        using var sha = SHA1.Create();
        var sb = new StringBuilder();
        foreach (var f in filePaths.OrderBy(x => x)) {
            sb.Append(await ComputeFileHash(f));
        }
        var input = Encoding.UTF8.GetBytes(sb.ToString());
        return BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "").ToLowerInvariant();
    }

    private static async Task<TaskStateFile> LoadTaskState(string stateFilePath) {
        if (!File.Exists(stateFilePath)) {
            return new TaskStateFile();
        }

        try {
            var json = await File.ReadAllTextAsync(stateFilePath);
            return JsonSerializer.Deserialize<TaskStateFile>(json) ?? new TaskStateFile();
        } catch {
            return new TaskStateFile();
        }
    }

    // Robust Save Implementation (Atomic Rename)
    private static async Task SaveTaskState(string stateFilePath, TaskStateFile state) {
        await _stateLock.WaitAsync();
        try {
            state.UpdatedAt = DateTime.UtcNow;
            var json = JsonSerializer.Serialize(state, new JsonSerializerOptions {
                WriteIndented = true
            });

            // Write to tmp file first
            var tmpPath = $"{stateFilePath}.tmp-{Guid.NewGuid()}";
            await File.WriteAllTextAsync(tmpPath, json);

            // Retry loop for file locking issues (common on Windows)
            int retries = 0;
            while (retries < 10) {
                try {
                    if (File.Exists(stateFilePath)) File.Delete(stateFilePath);
                    File.Move(tmpPath, stateFilePath);
                    return;
                } catch (IOException) {
                    retries++;
                    await Task.Delay(50 * retries);
                }
            }
        } catch (Exception ex) {
            Logger.LogError(ex, $"Warning: Failed to save state file: {ex.Message}");
        } finally {
            _stateLock.Release();
        }
    }
}
