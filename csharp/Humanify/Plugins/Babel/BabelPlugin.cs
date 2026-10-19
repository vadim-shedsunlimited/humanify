using Acornima;
using Acornima.Ast;

namespace Humanify.Plugins.Babel;

using AstProgram = Acornima.Ast.Program;

public class BabelPlugin : IPlugin {
    public string Name => "Babel Plugin";
    public string Description => "Transforms JavaScript code using Babel.";
    public string Identifier => "BABEL";
    public string InstanceId { get; } = Guid.NewGuid().ToString();

    public Task<(string Code, AstProgram? Ast)> ProcessAsync(PluginContext context) {
        if (string.IsNullOrWhiteSpace(context.Code)) {
            return Task.FromResult((context.Code, (AstProgram?)context.GetAst()));
        }

        try {
            var ast = context.GetAst();

            if (ast == null) {
                Logger.LogWarning($"[{Identifier}] Could not parse AST, skipping Babel transformations.");
                return Task.FromResult((context.Code, (AstProgram?)null));
            }

            // Rewrite the AST (Apply transformations)
            var rewriter = new BabelRewriter();
            var newAstNode = rewriter.Visit(ast);

            if (newAstNode is AstProgram newProgram) {
                // Optimization: Pass the AST back so the next plugin doesn't need to parse
                return Task.FromResult((newProgram.ToJavaScript(), (AstProgram?)null));
            }

            // Fallback if the rewrite resulted in a non-Program node (unlikely for file transforms)
            if (newAstNode is Node node) {
                return Task.FromResult((node.ToJavaScript(), (AstProgram?)null));
            }
            return Task.FromResult((context.Code, (AstProgram?)null));
        } catch (Exception ex) {
            Logger.LogError(ex, $"[{Identifier}] Failed to transform code: {ex.Message}");
            // Return original code on failure so the pipeline continues
            return Task.FromResult((context.Code, (AstProgram?)null));
        }
    }
    // The Rewriter class handles the specific node transformations
    private class BabelRewriter : AstRewriter {
        // Consolidates all string literals to use double quotes and removes unneeded hex escapes.
        protected override object? VisitLiteral(Literal node) {
            // If the literal holds a string value, we regenerate it to ensure it's "clean"
            // (e.g., changing '\x61' to 'a' and normalizing quotes)
            if (node.Value is string s) {
                // Optimization: If it's already a StringLiteral with the same value/raw, 
                // we could skip, but regenerating ensures normalization.
                return CreateStringLiteral(s);
            }
            return base.VisitLiteral(node);
        }

        // Unroll Sequence Expressions in Statements
        // "a = 1, b = 2;"  ->  "{ a = 1; b = 2; }"
        protected override object? VisitExpressionStatement(ExpressionStatement node) {
            if (node.Expression is SequenceExpression seq) {
                var statements = new List<Statement>();
                foreach (var expr in seq.Expressions) {
                    var visitedExpr = Visit(expr) as Expression ?? expr;
                    statements.Add(new NonSpecialExpressionStatement(visitedExpr));
                }

                return new NestedBlockStatement(NodeList.From(statements));
            }
            return base.VisitExpressionStatement(node);
        }

        // Unroll Sequence Expressions in Returns
        // "return a = 1, b;"  ->  "{ a = 1; return b; }"
        protected override object? VisitReturnStatement(ReturnStatement node) {
            if (node.Argument is SequenceExpression seq) {
                var statements = new List<Statement>();

                // All expressions except last become statements
                for (int i = 0; i < seq.Expressions.Count - 1; i++) {
                    var expr = seq.Expressions[i];
                    var visitedExpr = Visit(expr) as Expression ?? expr;
                    statements.Add(new NonSpecialExpressionStatement(visitedExpr));
                }

                // Last expression is the return
                var lastExpr = seq.Expressions.Last();
                var visitedLast = Visit(lastExpr) as Expression ?? lastExpr;
                statements.Add(new ReturnStatement(visitedLast));

                return new NestedBlockStatement(NodeList.From(statements));
            }
            return base.VisitReturnStatement(node);
        }

        protected override object? VisitUnaryExpression(UnaryExpression node) {
            // Helper to get double value safely
            bool IsNumber(Expression arg, double expectedVal) {
                if (arg is Literal { Value: not null } l && double.TryParse(l.Value.ToString(), out var d)) {
                    return d == expectedVal;
                }
                return false;
            }

            // Transformation: "void 0" -> "undefined"
            // Minifiers use "void 0" because it is shorter than "undefined"
            if (node.Operator == Operator.Void && IsNumber(node.Argument, 0)) {
                return new Identifier("undefined");
            }

            // Transformation: "!0" -> "true"
            if (node.Operator == Operator.LogicalNot && IsNumber(node.Argument, 0)) {
                return new BooleanLiteral(true, "true");
            }

            // Transformation: "!1" -> "false"
            if (node.Operator == Operator.LogicalNot && IsNumber(node.Argument, 1)) {
                return new BooleanLiteral(false, "false");
            }

            return base.VisitUnaryExpression(node);
        }

        // Constant Folding & Yoda Conditions
        protected override object? VisitBinaryExpression(BinaryExpression node) {
            // Recurse first to handle deep nesting like "a" + "b" + "c"
            var visited = base.VisitBinaryExpression(node) as BinaryExpression ?? node;

            // Feature: String Concatenation (Constant Folding)
            // Turns: "User" + "Name" -> "UserName"
            if (visited.Operator == Operator.Addition &&
                visited.Left is Literal { Value: string s1 } &&
                visited.Right is Literal { Value: string s2 }) {

                return CreateStringLiteral(s1 + s2);
            }

            // Feature: Yoda Conditions
            // Turns: if (5 === x) -> if (x === 5)
            if (IsComparison(visited.Operator) && visited.Left is Literal && visited.Right is Identifier) {
                // NonLogicalBinaryExpression is the concrete class for comparisons in Acornima
                return new NonLogicalBinaryExpression(visited.Operator, visited.Right, visited.Left);
            }

            return visited;
        }

        // Helper to satisfy StringLiteral(string value, string raw) constructor
        private StringLiteral CreateStringLiteral(string value) {
            // We use JSON serialization to generate a valid, double-quoted JS string literal.
            // Example: value="hello" -> raw="\"hello\""
            var raw = System.Text.Json.JsonSerializer.Serialize(value);
            return new StringLiteral(value, raw);
        }

        private static bool IsComparison(Operator op) {
            return op == Operator.Equality || op == Operator.Inequality ||
                   op == Operator.StrictEquality || op == Operator.StrictInequality;
        }
    }
}