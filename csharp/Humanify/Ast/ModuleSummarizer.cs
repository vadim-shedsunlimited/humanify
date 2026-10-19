using Acornima;
using Acornima.Ast;
using System.Text;
using System.Linq;
using System.Collections.Generic;

namespace Humanify.Ast;

using AstProgram = Acornima.Ast.Program;

public class ModuleSummarizer : AstVisitor {
    private readonly StringBuilder _sb = new();
    private readonly string _sourceCode;
    private readonly Node _root; // Root node needed for AncestorNodes
    private readonly List<Comment> _comments;
    private readonly HashSet<string> _seenExports = new();

    public ModuleSummarizer(string sourceCode, Node root, List<Comment> comments) {
        _sourceCode = sourceCode;
        _root = root;
        _comments = comments;
    }

    public string GetSummary() => _sb.ToString();

    // 1. Handle "export function foo(...) {}" AND "export { a, b }"
    protected override object? VisitExportNamedDeclaration(ExportNamedDeclaration node) {
        AppendDocComment(node);

        // Handle: export function/const ...
        if (node.Declaration is FunctionDeclaration func && func.Id != null) {
            _sb.AppendLine($"export function {func.Id.Name}({GetParams(func.Params)});");
        } else if (node.Declaration is VariableDeclaration varDecl) {
            foreach (var decl in varDecl.Declarations) {
                if (decl.Id is Identifier id) _sb.AppendLine($"export const {id.Name};");
            }
        } else if (node.Declaration is ClassDeclaration cls && cls.Id != null) {
            SummarizeClass(cls, $"export class {cls.Id.Name}");
        }

        // Handle: export { a, b } [from '...']
        if (node.Specifiers.Count > 0) {
            foreach (var spec in node.Specifiers) {
                string exportedName = "";

                // Safely extract name from Expression (Identifier or Literal)
                if (spec.Exported is Identifier id) {
                    exportedName = id.Name;
                } else if (spec.Exported is Literal lit) {
                    exportedName = lit.Value as string ?? "";
                }

                if (!string.IsNullOrWhiteSpace(exportedName)) {
                    _sb.AppendLine($"export const {exportedName};");
                }
            }
        }

        return base.VisitExportNamedDeclaration(node);
    }

    // 2. Handle "export default ..."
    protected override object? VisitExportDefaultDeclaration(ExportDefaultDeclaration node) {
        AppendDocComment(node);
        if (node.Declaration is FunctionDeclaration func) {
            var name = func.Id?.Name ?? "default";
            _sb.AppendLine($"export default function {name}({GetParams(func.Params)});");
        } else if (node.Declaration is ClassDeclaration cls) {
            _sb.AppendLine($"export default class {cls.Id?.Name ?? "default"} {{}};");
        } else {
            _sb.AppendLine("export default ...;");
        }
        return base.VisitExportDefaultDeclaration(node);
    }

    // 3. Handle CommonJS "module.exports = { ... }" or "exports.foo = ..."
    protected override object? VisitAssignmentExpression(AssignmentExpression node) {
        // module.exports = { ... }
        if (IsModuleExports(node.Left)) {
            if (node.Right is ObjectExpression obj) {
                foreach (var prop in obj.Properties) {
                    if (prop is Property p && p.Key is Identifier key) {
                        // Try to infer function signature if value is a function
                        string sig = "";
                        if (p.Value is FunctionExpression fe) sig = $"({GetParams(fe.Params)})";
                        else if (p.Value is ArrowFunctionExpression afe) sig = $"({GetParams(afe.Params)})";

                        _sb.AppendLine($"module.exports.{key.Name} = function{sig};");
                    }
                }
            }
        }
        // exports.foo = ... OR module.exports.foo = ...
        else if (IsExportsProperty(node.Left, out var propName)) {
            // Dedup
            if (_seenExports.Add(propName)) {
                // Try to get comment on the statement (parent)
                // Use AncestorNodes to find the enclosing statement for doc comments
                var parent = node.AncestorNodes(_root).LastOrDefault() ?? node;
                AppendDocComment(parent);

                string sig = GetSignature(node.Right);
                _sb.AppendLine($"exports.{propName} = {sig};");
            }
        }

        return base.VisitAssignmentExpression(node);
    }

    private void SummarizeClass(ClassDeclaration node, string header) {
        _sb.AppendLine($"{header} {{");
        foreach (var element in node.Body.Body) {
            if (element is MethodDefinition md && md.Key is Identifier methodId && md.Kind == PropertyKind.Method) {
                string sig = $"({GetParams(md.Value.Params)})";
                _sb.AppendLine($"  {methodId.Name}{sig};");
            } else if (element is MethodDefinition ctor && ctor.Kind == PropertyKind.Constructor) {
                string sig = $"({GetParams(ctor.Value.Params)})";
                _sb.AppendLine($"  constructor{sig};");
            }
        }
        _sb.AppendLine("};");
    }

    private string GetSignature(Node value) {
        if (value is FunctionExpression fe) return $"function({GetParams(fe.Params)})";
        if (value is ArrowFunctionExpression afe) return $"function({GetParams(afe.Params)})";
        if (value is ClassExpression) return "class { ... }";
        return "value";
    }

    private void AppendDocComment(Node node) {
        // Find comments that end immediately before this node starts
        var relevantComments = _comments
            .Where(c => c.End <= node.Range.Start && c.End > node.Range.Start - 200) // Look back a bit
            .OrderBy(c => c.Start)
            .ToList();

        if (relevantComments.Any()) {
            // Just take the last block comment or the last few line comments
            var last = relevantComments.Last();
            if (last.Kind == CommentKind.Block) {
                _sb.AppendLine("/*" + _sourceCode.Substring(last.Start + 2, last.End - last.Start - 4) + "*/");
            }
        }
    }

    private string GetParams(NodeList<Node> pars) {
        return string.Join(", ", pars.Select(p => {
            if (p is Identifier id) return id.Name;
            if (p is AssignmentPattern ap && ap.Left is Identifier aid) return aid.Name + "?";
            if (p is RestElement re && re.Argument is Identifier rid) return "..." + rid.Name;
            return "_";
        }));
    }

    private bool IsModuleExports(Node node) {
        // checks for "module.exports"
        return node is MemberExpression me &&
               me.Object is Identifier o && o.Name == "module" &&
               me.Property is Identifier p && p.Name == "exports";
    }

    private bool IsExportsProperty(Node node, out string name) {
        name = "";
        if (node is MemberExpression me) {
            // 1. exports.foo
            if (me.Object is Identifier o && o.Name == "exports" && me.Property is Identifier p) {
                name = p.Name;
                return true;
            }
            // 2. module.exports.foo
            if (me.Object is MemberExpression inner &&
                inner.Object is Identifier mo && mo.Name == "module" &&
                inner.Property is Identifier mp && mp.Name == "exports" &&
                me.Property is Identifier finalP) {
                name = finalP.Name;
                return true;
            }
        }
        return false;
    }
}