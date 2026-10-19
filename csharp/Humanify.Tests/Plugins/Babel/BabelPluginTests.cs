using FluentAssertions;
using Humanify.Plugins.Babel;
using Xunit;

namespace Humanify.Tests.Plugins.Babel;

public class BabelPluginTests {
    [Fact]
    public async Task Transform_Void0ToUndefined_ReplacesCorrectly() {
        // Arrange
        var input = "var x = void 0;";

        // Act
        var result = await BabelPlugin.Transform(input);

        // Assert
        result.Should().Contain("undefined");
        result.Should().NotContain("void 0");
    }

    [Fact]
    public async Task Transform_FlipComparisons_FlipsLiteralToRight() {
        // Arrange
        var input = "if (5 == x) { }";

        // Act
        var result = await BabelPlugin.Transform(input);

        // Assert
        result.Should().Contain("x == 5");
        result.Should().NotContain("5 == x");
    }

    [Fact]
    public async Task Transform_ScientificNotation_ConvertsToRegularNumber() {
        // Arrange
        var input = "var y = 5e3;";

        // Act
        var result = await BabelPlugin.Transform(input);

        // Assert
        result.Should().Contain("5000");
        result.Should().NotContain("5e3");
    }

    [Fact]
    public async Task Transform_MultipleTransformations_AppliesAll() {
        // Arrange
        var input = "var x = void 0; if (10 > y) { var z = 2e2; }";

        // Act
        var result = await BabelPlugin.Transform(input);

        // Assert
        result.Should().Contain("undefined");
        result.Should().Contain("y < 10");
        result.Should().Contain("200");
    }

    [Fact]
    public async Task Transform_MinifiedFunction_AddsSpaces() {
        // Arrange
        var input = "function a(e,t){var n=[];var r=e.length;var i=0;for(;i<r;i+=t){if(i+t<r){n.push(e.substring(i,i+t))}else{n.push(e.substring(i,r))}}return n}";

        // Act
        var result = await BabelPlugin.Transform(input);

        // Assert
        result.Should().Contain("var n = []");
        result.Should().Contain("i < r");
        result.Should().Contain("i + = t");
    }

    [Fact]
    public async Task Transform_EmptyInput_ReturnsEmpty() {
        // Arrange
        var input = "";

        // Act
        var result = await BabelPlugin.Transform(input);

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task Transform_NoTransformationsNeeded_ReturnsOriginal() {
        // Arrange
        var input = "function test(x) { return x + 1; }";

        // Act
        var result = await BabelPlugin.Transform(input);

        // Assert
        result.Should().Be(input);
    }

    [Theory]
    [InlineData("var x = void 0;", "undefined")]
    [InlineData("if (5 == x) { }", "x == 5")]
    [InlineData("var y = 5e3;", "5000")]
    [InlineData("var z = 1e10;", "10000000000")]
    public async Task Transform_VariousInputs_MatchesExpected(string input, string expectedContains) {
        // Act
        var result = await BabelPlugin.Transform(input);

        // Assert
        result.Should().Contain(expectedContains);
    }
}
