using Acornima;
using Acornima.Ast;

namespace Humanify.Ast;

using AstProgram = Acornima.Ast.Program;

/// <summary>
/// Scans for imports to resolve file paths
/// </summary>
public class ImportScanner : AstVisitor {
    public Dictionary<string, string> ImportMap { get; } = new();

    protected override object? VisitCallExpression(CallExpression node) {
        // require("./foo.js")
        if (node.Callee is Identifier id && id.Name == "require" && node.Arguments.Count > 0) {
            if (node.Arguments[0] is Literal lit && lit.Value is string path) {
                // Try to find the variable assignment: var r = require(...)
                // This is hard in a simple visitor because we are inside the call.
                // Instead, we check VariableDeclarator.
            }
        }
        return base.VisitCallExpression(node);
    }

    protected override object? VisitVariableDeclarator(VariableDeclarator node) {
        // var r = require("...")
        if (node.Id is Identifier id && node.Init is CallExpression call) {
            if (call.Callee is Identifier callee && callee.Name == "require" && call.Arguments.Count > 0) {
                if (call.Arguments[0] is Literal lit && lit.Value is string path) {
                    ImportMap[id.Name] = path;
                }
            }
        }
        return base.VisitVariableDeclarator(node);
    }

    protected override object? VisitImportDeclaration(ImportDeclaration node) {
        // import r from "./..."
        if (node.Source is Literal lit && lit.Value is string path) {
            foreach (var spec in node.Specifiers) {
                if (spec is ImportDefaultSpecifier def) ImportMap[def.Local.Name] = path;
                else if (spec is ImportNamespaceSpecifier ns) ImportMap[ns.Local.Name] = path;
                else if (spec is ImportSpecifier named) ImportMap[named.Local.Name] = path;
            }
        }
        return base.VisitImportDeclaration(node);
    }
}