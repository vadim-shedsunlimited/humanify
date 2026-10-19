using FluentAssertions;
using Humanify;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace Humanify.Tests;

public class UnminifyTests {
    [Fact]
    public async Task Execute_SingleFile_ProcessesSuccessfully() {
        // Arrange
        var testOutputDir = Path.Combine(Path.GetTempPath(), $"humanify_test_{Guid.NewGuid()}");
        try {
            var inputFile = Path.Combine(testOutputDir, "input.js");
            Directory.CreateDirectory(testOutputDir);
            await File.WriteAllTextAsync(inputFile, "var x = void 0;");

            var plugins = new List<Func<string, Task<string>>>
            {
                async (code) => code.Replace("void 0", "undefined")
            };

            var options = new UnminifyOptions {
                Resume = false,
                ContinueOnError = false,
                Agents = 1
            };

            // Act
            var result = await Unminify.Execute(
                new[] { inputFile },
                testOutputDir,
                plugins,
                options);

            // Assert
            result.Should().Be(0); // Success

            var outputFile = Path.Combine(testOutputDir, "input.js");
            File.Exists(outputFile).Should().BeTrue();

            var outputContent = await File.ReadAllTextAsync(outputFile);
            outputContent.Should().Contain("undefined");
        } finally {
            if (Directory.Exists(testOutputDir)) {
                Directory.Delete(testOutputDir, true);
            }
        }
    }

    [Fact]
    public async Task Execute_MultipleFiles_ProcessesAll() {
        // Arrange
        var testOutputDir = Path.Combine(Path.GetTempPath(), $"humanify_test_{Guid.NewGuid()}");
        try {
            var inputDir = Path.Combine(testOutputDir, "input");
            Directory.CreateDirectory(inputDir);

            var file1 = Path.Combine(inputDir, "file1.js");
            var file2 = Path.Combine(inputDir, "file2.js");

            await File.WriteAllTextAsync(file1, "var a = void 0;");
            await File.WriteAllTextAsync(file2, "var b = void 1;");

            var plugins = new List<Func<string, Task<string>>>
            {
                async (code) => code.Replace("void 0", "undefined").Replace("void 1", "undefined")
            };

            var options = new UnminifyOptions {
                Resume = false,
                ContinueOnError = false,
                Agents = 2
            };

            // Act
            var result = await Unminify.Execute(
                new[] { inputDir },
                testOutputDir,
                plugins,
                options);

            // Assert
            result.Should().Be(0);

            var output1 = Path.Combine(testOutputDir, "file1.js");
            var output2 = Path.Combine(testOutputDir, "file2.js");

            File.Exists(output1).Should().BeTrue();
            File.Exists(output2).Should().BeTrue();

            var content1 = await File.ReadAllTextAsync(output1);
            var content2 = await File.ReadAllTextAsync(output2);

            content1.Should().Contain("undefined");
            content2.Should().Contain("undefined");
        } finally {
            if (Directory.Exists(testOutputDir)) {
                Directory.Delete(testOutputDir, true);
            }
        }
    }

    [Fact]
    public async Task Execute_ContinueOnError_ContinuesAfterFailure() {
        // Arrange
        var testOutputDir = Path.Combine(Path.GetTempPath(), $"humanify_test_{Guid.NewGuid()}");
        try {
            var file1 = Path.Combine(testOutputDir, "file1.js");
            var file2 = Path.Combine(testOutputDir, "file2.js");

            Directory.CreateDirectory(testOutputDir);
            await File.WriteAllTextAsync(file1, "var a = 1;");
            await File.WriteAllTextAsync(file2, "var b = 2;");

            var callCount = 0;
            var plugins = new List<Func<string, Task<string>>>
            {
                async (code) =>
                {
                    callCount++;
                    if (callCount == 1)
                    {
                        throw new Exception("Simulated failure");
                    }
                    return code + " // processed";
                }
            };

            var options = new UnminifyOptions {
                Resume = false,
                ContinueOnError = true,
                Agents = 1
            };

            // Act
            var result = await Unminify.Execute(
                new[] { file1, file2 },
                testOutputDir,
                plugins,
                options);

            // Assert
            result.Should().Be(0); // Should succeed despite one failure

            var output1 = Path.Combine(testOutputDir, "file1.js");
            var output2 = Path.Combine(testOutputDir, "file2.js");

            // file1 should exist but not be processed (failed)
            File.Exists(output1).Should().BeTrue();

            // file2 should be processed
            File.Exists(output2).Should().BeTrue();
            var content2 = await File.ReadAllTextAsync(output2);
            content2.Should().Contain("// processed");
        } finally {
            if (Directory.Exists(testOutputDir)) {
                Directory.Delete(testOutputDir, true);
            }
        }
    }
}
