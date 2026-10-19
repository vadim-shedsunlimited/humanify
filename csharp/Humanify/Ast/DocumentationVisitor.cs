using Acornima;
using Acornima.Ast;

namespace Humanify.Ast;

public class DocumentationVisitor : AstVisitor {
    public List<Node> Targets { get; } = new();

    protected override object? VisitFunctionDeclaration(FunctionDeclaration node) {
        Targets.Add(node);
        return base.VisitFunctionDeclaration(node);
    }

    protected override object? VisitClassDeclaration(ClassDeclaration node) {
        Targets.Add(node);
        return base.VisitClassDeclaration(node);
    }

    protected override object? VisitExportNamedDeclaration(ExportNamedDeclaration node) {
        // If we export a Function or Class, target the Export node itself (starts at 'export')
        // instead of the inner declaration (starts at 'function'/'class').
        if (node.Declaration is FunctionDeclaration || node.Declaration is ClassDeclaration) {
            Targets.Add(node);
            // Return null to STOP visiting children. 
            // We don't want to add the inner FunctionDeclaration to the list again.
            return null;
        }
        return base.VisitExportNamedDeclaration(node);
    }

    protected override object? VisitExportDefaultDeclaration(ExportDefaultDeclaration node) {
        // Same logic for default exports
        if (node.Declaration is FunctionDeclaration || node.Declaration is ClassDeclaration) {
            Targets.Add(node);
            return null;
        }
        return base.VisitExportDefaultDeclaration(node);
    }

    protected override object? VisitAssignmentExpression(AssignmentExpression node) {
        // Case 1: module.exports = { create: ..., node: ... }
        // If we are exporting an object literal, we want to document its properties
        // individually rather than sending the huge object to the LLM as one blob.
        if (IsModuleExports(node.Left) && node.Right is ObjectExpression objExpr) {
            foreach (var item in objExpr.Properties) {
                // We only care about properties that are functions or classes
                if (item is Property prop &&
                   (prop.Value is FunctionExpression ||
                    prop.Value is ArrowFunctionExpression ||
                    prop.Value is ClassExpression)) {

                    Targets.Add(prop);
                }
            }
            // We return base to allow visiting children, but we DON'T add the 
            // parent 'node' to Targets. This prevents double-documentation.
            return base.VisitAssignmentExpression(node);
        }

        // Case 2: module.exports = function() { ... }
        if (IsModuleExports(node.Left)) {
            Targets.Add(node);
            return base.VisitAssignmentExpression(node);
        }

        // Case 3: exports.foo = ...
        if (IsExportsProperty(node.Left)) {
            Targets.Add(node);
            return base.VisitAssignmentExpression(node);
        }

        return base.VisitAssignmentExpression(node);
    }

    // Helper to check for 'module.exports'
    private static bool IsModuleExports(Node node) {
        return node is MemberExpression member &&
                           member.Object is Identifier obj && obj.Name == "module" &&
                           member.Property is Identifier prop && prop.Name == "exports";
    }

    // Helper to check for 'exports.Something'
    private static bool IsExportsProperty(Node node) {
        // Matches: exports.foo = ...
        return node is MemberExpression member &&
               member.Object is Identifier obj && obj.Name == "exports";
    }
}