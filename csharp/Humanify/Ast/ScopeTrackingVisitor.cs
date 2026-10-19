using Acornima;
using Acornima.Ast;

namespace Humanify.Ast;

using AstProgram = Acornima.Ast.Program;

public class ScopeTrackingVisitor : AstVisitor {
    /// <summary>
    /// We store tuple: (Identifier Node, Parent Scope Node)
    /// </summary>
    public List<(Identifier Node, Node ScopeNode)> ScopedIdentifiers { get; } = [];

    /// <summary>
    /// Track which scope is the parent of another
    /// </summary>
    public Dictionary<Node, Node> ScopeParentMap { get; } = new();

    /// <summary>
    /// Set of object names (e.g. imports) whose properties we should also track as identifiers
    /// </summary>
    public HashSet<string> TrackPropertiesOf { get; set; } = new();

    /// <summary>
    /// Stack tracks the chain of scopes (Global -> Func A -> Func B)
    /// </summary>
    private Stack<Scope> _scopeStack = new();

    /// <summary>
    /// Defined scopes store which variables belong to them
    /// </summary>
    private class Scope {
        public Node Node { get; }
        public HashSet<string> DeclaredVariables { get; } = new();
        public Scope(Node node) { Node = node; }
    }

    private readonly HashSet<string> _ignore = new() {
            "function", "return", "var", "let", "const", "if", "for", "while", "this",
            "window", "console", "require", "module", "exports", "arguments", "undefined", "null", "true", "false"
        };

    public ScopeTrackingVisitor() {
        // Default "root" scope will be the Program node once visited
    }

    // --- 1. Scope Entry/Exit Management ---

    protected override object? VisitProgram(AstProgram node) {
        EnterScope(node);
        base.VisitProgram(node);
        ExitScope();
        return node;
    }

    protected override object? VisitFunctionDeclaration(FunctionDeclaration node) {
        // The function name ITSELF is declared in the PARENT scope
        if (node.Id != null) RegisterDeclaration(node.Id.Name);

        EnterScope(node);
        // Params are declared INSIDE the function scope
        foreach (var p in node.Params) RegisterBinding(p);

        base.VisitFunctionDeclaration(node);
        ExitScope();
        return node;
    }

    protected override object? VisitFunctionExpression(FunctionExpression node) {
        EnterScope(node);
        // Function Expression name is local to the function itself (for recursion)
        if (node.Id != null) RegisterDeclaration(node.Id.Name);

        foreach (var p in node.Params) RegisterBinding(p);
        base.VisitFunctionExpression(node);
        ExitScope();
        return node;
    }

    protected override object? VisitArrowFunctionExpression(ArrowFunctionExpression node) {
        EnterScope(node);
        foreach (var p in node.Params) RegisterBinding(p);
        base.VisitArrowFunctionExpression(node);
        ExitScope();
        return node;
    }

    protected override object? VisitCatchClause(CatchClause node) {
        // Catch blocks are a scope for the error variable
        EnterScope(node);
        if (node.Param != null) RegisterBinding(node.Param);
        base.VisitCatchClause(node);
        ExitScope();
        return node;
    }

    // --- 2. Variable Declaration Tracking ---

    protected override object? VisitVariableDeclarator(VariableDeclarator node) {
        // Handle complex declarations like: var {a, b} = obj; or var [x] = arr;
        RegisterBinding(node.Id);
        return base.VisitVariableDeclarator(node);
    }

    protected override object? VisitClassDeclaration(ClassDeclaration node) {
        if (node.Id != null) RegisterDeclaration(node.Id.Name);
        return base.VisitClassDeclaration(node);
    }

    protected override object? VisitImportSpecifier(ImportSpecifier node) {
        RegisterDeclaration(node.Local.Name);
        return node;
    }

    protected override object? VisitImportDefaultSpecifier(ImportDefaultSpecifier node) {
        RegisterDeclaration(node.Local.Name);
        return node;
    }

    protected override object? VisitImportNamespaceSpecifier(ImportNamespaceSpecifier node) {
        RegisterDeclaration(node.Local.Name);
        return node;
    }

    // --- 3. Usage Resolution ---

    protected override object? VisitIdentifier(Identifier node) {
        if (!_ignore.Contains(node.Name) && node.Name.Length > 0) {
            ResolveAndRegister(node);
        }
        return base.VisitIdentifier(node);
    }

    // --- Helpers ---

    private void EnterScope(Node node) {
        if (_scopeStack.Count > 0) {
            // Record the parent of the new scope
            ScopeParentMap[node] = _scopeStack.Peek().Node;
        }
        _scopeStack.Push(new Scope(node));
    }

    private void ExitScope() {
        _scopeStack.Pop();
    }

    private void RegisterDeclaration(string name) {
        if (_scopeStack.Count > 0) {
            _scopeStack.Peek().DeclaredVariables.Add(name);
        }
    }

    /// <summary>
    /// Recursively extracts variable names from binding patterns (Identifiers, ObjectPattern, ArrayPattern, etc.)
    /// </summary>
    private void RegisterBinding(Node node) {
        if (_scopeStack.Count == 0) return;
        var targetSet = _scopeStack.Peek().DeclaredVariables;
        CollectNames(node, targetSet);
    }

    private static void CollectNames(Node? node, HashSet<string> target) {
        if (node == null) return;

        if (node is Identifier id) {
            target.Add(id.Name);
        } else if (node is AssignmentPattern ap) {
            // Default params: function(a = 1) -> 'a' is the binding
            CollectNames(ap.Left, target);
        } else if (node is RestElement re) {
            // Rest params: function(...args) -> 'args' is the binding
            CollectNames(re.Argument, target);
        } else if (node is ArrayPattern arr) {
            // Destructuring: var [a, b] = c;
            foreach (var el in arr.Elements) {
                CollectNames(el, target);
            }
        } else if (node is ObjectPattern obj) {
            // Destructuring: var {a, b: c} = d;
            foreach (var prop in obj.Properties) {
                if (prop is Property p) {
                    // In destructuring, the Value is the target variable ( Key: Value )
                    // { a: b } -> b is the variable
                    // { a } -> ShortHand -> Key=a, Value=a -> a is the variable
                    CollectNames(p.Value, target);
                } else if (prop is RestElement pRe) {
                    CollectNames(pRe.Argument, target);
                }
            }
        }
    }

    // The Magic: Find which scope owns this variable
    private void ResolveAndRegister(Identifier node, bool isPropertyAccess = false) {
        // Walk up the stack to find the declaration
        foreach (var scope in _scopeStack) {
            if (scope.DeclaredVariables.Contains(node.Name)) {
                // Found the owner! Add to list.
                ScopedIdentifiers.Add((node, scope.Node));
                return;
            }
        }

        // If not found in stack
        if (_scopeStack.Count > 0) {
            if (isPropertyAccess) {
                // For properties of imported objects (errorModule.A), they are not declared variables.
                // We want to associate them with the CURRENT scope (where they are used), 
                // so they get batched with the surrounding code context.
                ScopedIdentifiers.Add((node, _scopeStack.Peek().Node));
            } else {
                // For regular variables, it's likely a Global.
                // We add it to the ROOT scope (Program) so it gets renamed consistently globally.
                var rootScope = _scopeStack.Last(); // Bottom of stack is Program
                ScopedIdentifiers.Add((node, rootScope.Node));
            }
        }
    }

    // --- Exclusions ---
    protected override object? VisitMemberExpression(MemberExpression node) {
        Visit(node.Object);
        if (node.Computed) {
            Visit(node.Property); // Only visit property if it's computed (arr[i])            
        } else {
            // If this is a property access on an object we are tracking (e.g. an imported module)
            // we want to treat the property identifier (e.g. 'A' in errorModule.A) as a candidate for renaming.
            if (node.Object is Identifier objId && TrackPropertiesOf.Contains(objId.Name)) {
                if (node.Property is Identifier propId) {
                    ResolveAndRegister(propId, isPropertyAccess: true);
                }
            }
        }

        return node;
    }
    protected override object? VisitObjectExpression(ObjectExpression node) {
        foreach (var prop in node.Properties) {
            if (prop is Property p) {
                if (p.Computed) Visit(p.Key);
                Visit(p.Value);
            } else Visit(prop);
        }
        return node;
    }
}