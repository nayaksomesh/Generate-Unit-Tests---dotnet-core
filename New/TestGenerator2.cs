using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ClassTestGenerator
{
    internal static class TestGenerator
    {
        public static string GenerateUnitTests(string sourceCode)
        {
            var tree = CSharpSyntaxTree.ParseText(sourceCode);
            var root = tree.GetRoot() as CompilationUnitSyntax;
            if (root == null)
                return null;

            var references = new List<MetadataReference>
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(List<>).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Task).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Console).Assembly.Location)
            };

            var compilation = CSharpCompilation.Create(
                assemblyName: "ClassTestGen",
                syntaxTrees: new[] { tree },
                references: references,
                options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            string namespaceName = root.DescendantNodes()
                .OfType<NamespaceDeclarationSyntax>()
                .FirstOrDefault()?.Name.ToString() ?? "DefaultNamespace";

            var classes = root.DescendantNodes()
                .OfType<ClassDeclarationSyntax>()
                .Where(c => c.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword)))
                .ToList();

            if (!classes.Any())
                return null;

            var sb = new StringBuilder();

            sb.AppendLine("using System;");
            sb.AppendLine("using System.Collections.Generic;");
            sb.AppendLine("using System.Threading.Tasks;");
            sb.AppendLine("using FluentAssertions;");
            sb.AppendLine("using NSubstitute;");
            sb.AppendLine("using Xunit;");
            sb.AppendLine($"using {namespaceName};");
            sb.AppendLine();
            sb.AppendLine($"namespace {namespaceName}.Tests");
            sb.AppendLine("{");

            foreach (var classDecl in classes)
            {
                AppendClassTests(sb, classDecl, classes);
            }

            sb.AppendLine("}");

            return sb.ToString();
        }

        private static void AppendClassTests(
            StringBuilder sb,
            ClassDeclarationSyntax classDecl,
            List<ClassDeclarationSyntax> allClasses)
        {
            string className = classDecl.Identifier.Text;
            bool isStaticClass = classDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword));

            var publicCtors = classDecl.Members.OfType<ConstructorDeclarationSyntax>()
                .Where(c => c.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword)))
                .ToList();

            bool hasDefaultCtor = !isStaticClass &&
                                  publicCtors.Any(c => !c.ParameterList.Parameters.Any());

            var properties = classDecl.Members.OfType<PropertyDeclarationSyntax>()
                .Where(p => p.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword)))
                .ToList();

            var methods = classDecl.Members.OfType<MethodDeclarationSyntax>()
                .Where(m => m.Modifiers.Any(mm => mm.IsKind(SyntaxKind.PublicKeyword)))
                .ToList();

            string ctorArrange = isStaticClass
                ? string.Empty
                : GenerateCtorArrange(className, publicCtors, allClasses, hasDefaultCtor);

            sb.AppendLine($"    public class {className}Tests");
            sb.AppendLine("    {");

            if (!isStaticClass)
            {
                sb.Append(GenerateCtorTests(className, publicCtors, ctorArrange, hasDefaultCtor));
                sb.Append(GeneratePropertyTests(className, properties, hasDefaultCtor, allClasses));

                var instanceMethods = methods.Where(m => !IsStaticMethod(m)).ToList();
                sb.Append(GenerateMethodTests(className, instanceMethods, hasDefaultCtor, allClasses));

                if (!hasDefaultCtor)
                {
                    sb.Append(GenerateFactoryHelper(className, publicCtors, allClasses));
                }
            }

            var staticMethods = methods.Where(IsStaticMethod).ToList();
            sb.Append(GenerateStaticMethodTests(className, staticMethods, allClasses));

            sb.AppendLine("    }");
            sb.AppendLine();
        }

        // ---------- Constructors (instance) ----------

        private static string GenerateCtorTests(
            string className,
            List<ConstructorDeclarationSyntax> ctors,
            string ctorArrange,
            bool hasDefaultCtor)
        {
            var sb = new StringBuilder();

            if (hasDefaultCtor)
            {
                sb.AppendLine("        [Fact]");
                sb.AppendLine("        public void Ctor_Default_CreatesInstance()");
                sb.AppendLine("        {");
                sb.AppendLine($"            var instance = new {className}();");
                sb.AppendLine("            instance.Should().NotBeNull().And.BeOfType<" + className + ">();");
                sb.AppendLine("        }");
                sb.AppendLine();
            }

            foreach (var ctor in ctors.Where(c => c.ParameterList.Parameters.Any()))
            {
                var parameters = ctor.ParameterList.Parameters;
                var arrangeLines = new List<string>();
                var argNames = new List<string>();

                foreach (var p in parameters)
                {
                    string pType = p.Type?.ToString() ?? "object";
                    string pName = p.Identifier.Text;
                    bool isInterface = pType.StartsWith("I", StringComparison.Ordinal) && !IsPrimitiveType(pType);

                    if (isInterface)
                    {
                        arrangeLines.Add($"            var {pName} = Substitute.For<{pType}>();");
                    }
                    else
                    {
                        string sample = GetSampleValue(pType, IsCollectionType(pType), new List<ClassDeclarationSyntax>());
                        arrangeLines.Add($"            var {pName} = {sample};");
                    }

                    argNames.Add(pName);
                }

                sb.AppendLine("        [Fact]");
                sb.AppendLine("        public void Ctor_WithDependencies_CreatesInstance()");
                sb.AppendLine("        {");
                foreach (var line in arrangeLines)
                    sb.AppendLine(line);
                sb.AppendLine();
                sb.AppendLine($"            var instance = new {className}({string.Join(", ", argNames)});");
                sb.AppendLine();
                sb.AppendLine("            instance.Should().NotBeNull().And.BeOfType<" + className + ">();");
                sb.AppendLine("        }");
                sb.AppendLine();
            }

            return sb.ToString();
        }

        private static string GenerateCtorArrange(
            string className,
            List<ConstructorDeclarationSyntax> ctors,
            List<ClassDeclarationSyntax> allClasses,
            bool hasDefaultCtor)
        {
            if (hasDefaultCtor)
                return string.Empty;

            var primaryCtor = ctors
                .OrderByDescending(c => c.ParameterList.Parameters.Count)
                .FirstOrDefault();

            if (primaryCtor == null)
                return string.Empty;

            var lines = new List<string>();
            var subVars = new List<string>();

            foreach (var param in primaryCtor.ParameterList.Parameters.Take(3))
            {
                string paramType = param.Type?.ToString() ?? "object";
                string paramName = param.Identifier.Text;
                bool isInterface = paramType.StartsWith("I", StringComparison.Ordinal) &&
                                   !IsPrimitiveType(paramType);

                if (isInterface)
                {
                    lines.Add($"var {paramName} = Substitute.For<{paramType}>();");
                }
                else
                {
                    string sample = GetSampleValue(paramType, IsCollectionType(paramType), allClasses);
                    lines.Add($"var {paramName} = {sample};");
                }

                subVars.Add(paramName);
            }

            lines.Add($"var instance = new {className}({string.Join(", ", subVars)});");

            return string.Join(" ", lines);
        }

        // ---------- Properties (instance) ----------

        private static string GeneratePropertyTests(
            string className,
            List<PropertyDeclarationSyntax> properties,
            bool hasDefaultCtor,
            List<ClassDeclarationSyntax> allClasses)
        {
            var sb = new StringBuilder();

            if (!properties.Any())
                return string.Empty;

            string instanceExpr = hasDefaultCtor
                ? $"new {className}()"
                : $"Create{className}WithSubs()";

            foreach (var prop in properties)
            {
                string propName = prop.Identifier.Text;
                string propType = prop.Type.ToString();
                bool isCollection = IsCollectionType(propType);
                bool hasSetter = prop.AccessorList?.Accessors.Any(a => a.IsKind(SyntaxKind.SetAccessorDeclaration)) == true;

                sb.AppendLine("        [Fact]");
                sb.AppendLine($"        public void {propName}_DefaultValue_IsAccessible()");
                sb.AppendLine("        {");
                sb.AppendLine($"            var instance = {instanceExpr};");
                sb.AppendLine();
                if (isCollection)
                {
                    sb.AppendLine($"            instance.{propName}.Should().NotBeNull();");
                }
                else
                {
                    sb.AppendLine($"            _ = instance.{propName};");
                }
                sb.AppendLine("        }");
                sb.AppendLine();

                if (hasSetter)
                {
                    string sample = GetSampleValue(propType, isCollection, allClasses);

                    sb.AppendLine("        [Fact]");
                    sb.AppendLine($"        public void {propName}_Set_Get_ReturnsAssignedValue()");
                    sb.AppendLine("        {");
                    sb.AppendLine($"            var instance = {instanceExpr};");
                    sb.AppendLine($"            var expected = {sample};");
                    sb.AppendLine();
                    sb.AppendLine($"            instance.{propName} = expected;");
                    sb.AppendLine();
                    sb.AppendLine($"            instance.{propName}.Should().BeEquivalentTo(expected);");
                    sb.AppendLine("        }");
                    sb.AppendLine();
                }
            }

            return sb.ToString();
        }

        // ---------- Methods (instance) ----------

        private static string GenerateMethodTests(
            string className,
            List<MethodDeclarationSyntax> methods,
            bool hasDefaultCtor,
            List<ClassDeclarationSyntax> allClasses)
        {
            var sb = new StringBuilder();

            if (!methods.Any())
                return string.Empty;

            string instanceExpr = hasDefaultCtor
                ? $"new {className}()"
                : $"Create{className}WithSubs()";

            foreach (var method in methods)
            {
                string methodName = method.Identifier.Text;
                var parameters = method.ParameterList.Parameters;
                string returnType = method.ReturnType.ToString();
                bool isAsync = IsAsyncMethod(method, returnType);
                bool returnsVoidLike = returnType == "void" || returnType == "Task";
                string innerReturnType = ExtractTaskType(returnType);

                var arrangeLines = new List<string>();
                var argNames = new List<string>();

                foreach (var p in parameters)
                {
                    string pType = p.Type?.ToString() ?? "object";
                    string pName = p.Identifier.Text;
                    bool isCollection = IsCollectionType(pType);
                    string sample = p.Default != null
                        ? $"default({pType})"
                        : GetSampleValue(pType, isCollection, allClasses);

                    arrangeLines.Add($"            var {pName} = {sample};");
                    argNames.Add(pName);
                }

                sb.AppendLine("        [Fact]");
                sb.AppendLine($"        public async Task {methodName}_Executes()");
                sb.AppendLine("        {");
                sb.AppendLine($"            var instance = {instanceExpr};");
                foreach (var line in arrangeLines)
                    sb.AppendLine(line);
                sb.AppendLine();
                sb.AppendLine("            // Act");

                string call = $"instance.{methodName}({string.Join(", ", argNames)})";

                if (!isAsync)
                {
                    if (returnsVoidLike)
                        sb.AppendLine($"            {call};");
                    else
                        sb.AppendLine($"            var result = {call};");
                }
                else
                {
                    if (returnsVoidLike)
                        sb.AppendLine($"            await {call};");
                    else
                        sb.AppendLine($"            var result = await {call};");
                }

                sb.AppendLine();
                sb.AppendLine("            // Assert");
                if (!returnsVoidLike)
                {
                    if (!string.IsNullOrWhiteSpace(innerReturnType) &&
                        returnType.StartsWith("Task", StringComparison.Ordinal))
                    {
                        sb.AppendLine($"            result.Should().BeOfType<{innerReturnType}>();");
                    }
                    else
                    {
                        sb.AppendLine("            result.Should().NotBeNull();");
                    }
                }
                else
                {
                    sb.AppendLine("            instance.Should().NotBeNull();");
                }
                sb.AppendLine("        }");
                sb.AppendLine();

                if (methodName.Contains("Validate", StringComparison.OrdinalIgnoreCase) ||
                    methodName.Contains("Throw", StringComparison.OrdinalIgnoreCase))
                {
                    sb.AppendLine("        [Fact]");
                    sb.AppendLine($"        public async Task {methodName}_WhenInvalid_Throws()");
                    sb.AppendLine("        {");
                    sb.AppendLine($"            var instance = {instanceExpr};");
                    foreach (var line in arrangeLines)
                        sb.AppendLine(line);
                    sb.AppendLine();
                    sb.AppendLine("            Func<Task> act = async () =>");
                    sb.AppendLine("            {");
                    string innerCall = $"instance.{methodName}({string.Join(", ", argNames)})";
                    if (isAsync)
                        sb.AppendLine($"                await {innerCall};");
                    else
                        sb.AppendLine($"                {innerCall};");
                    sb.AppendLine("            };");
                    sb.AppendLine();
                    sb.AppendLine("            await act.Should().ThrowAsync<Exception>();");
                    sb.AppendLine("        }");
                    sb.AppendLine();
                }
            }

            return sb.ToString();
        }

        // ---------- Static methods (static or static class) ----------

        private static string GenerateStaticMethodTests(
            string className,
            List<MethodDeclarationSyntax> staticMethods,
            List<ClassDeclarationSyntax> allClasses)
        {
            var sb = new StringBuilder();
            if (!staticMethods.Any())
                return string.Empty;

            foreach (var method in staticMethods)
            {
                string methodName = method.Identifier.Text;
                var parameters = method.ParameterList.Parameters;
                string returnType = method.ReturnType.ToString();
                bool isAsync = IsAsyncMethod(method, returnType);
                bool returnsVoidLike = returnType == "void" || returnType == "Task";
                string innerReturnType = ExtractTaskType(returnType);

                var arrangeLines = new List<string>();
                var argNames = new List<string>();

                foreach (var p in parameters)
                {
                    string pType = p.Type?.ToString() ?? "object";
                    string pName = p.Identifier.Text;
                    bool isCollection = IsCollectionType(pType);
                    string sample = p.Default != null
                        ? $"default({pType})"
                        : GetSampleValue(pType, isCollection, allClasses);

                    arrangeLines.Add($"            var {pName} = {sample};");
                    argNames.Add(pName);
                }

                sb.AppendLine("        [Fact]");
                sb.AppendLine($"        public async Task {methodName}_Static_Executes()");
                sb.AppendLine("        {");
                foreach (var line in arrangeLines)
                    sb.AppendLine(line);
                sb.AppendLine();
                sb.AppendLine("            // Act");

                string call = $"{className}.{methodName}({string.Join(", ", argNames)})";

                if (!isAsync)
                {
                    if (returnsVoidLike)
                        sb.AppendLine($"            {call};");
                    else
                        sb.AppendLine($"            var result = {call};");
                }
                else
                {
                    if (returnsVoidLike)
                        sb.AppendLine($"            await {call};");
                    else
                        sb.AppendLine($"            var result = await {call};");
                }

                sb.AppendLine();
                sb.AppendLine("            // Assert");
                if (!returnsVoidLike)
                {
                    if (!string.IsNullOrWhiteSpace(innerReturnType) &&
                        returnType.StartsWith("Task", StringComparison.Ordinal))
                    {
                        sb.AppendLine($"            result.Should().BeOfType<{innerReturnType}>();");
                    }
                    else
                    {
                        sb.AppendLine("            result.Should().NotBeNull();");
                    }
                }
                sb.AppendLine("        }");
                sb.AppendLine();

                if (methodName.Contains("Validate", StringComparison.OrdinalIgnoreCase) ||
                    methodName.Contains("Throw", StringComparison.OrdinalIgnoreCase))
                {
                    sb.AppendLine("        [Fact]");
                    sb.AppendLine($"        public async Task {methodName}_Static_WhenInvalid_Throws()");
                    sb.AppendLine("        {");
                    foreach (var line in arrangeLines)
                        sb.AppendLine(line);
                    sb.AppendLine();
                    sb.AppendLine("            Func<Task> act = async () =>");
                    sb.AppendLine("            {");
                    string innerCall = $"{className}.{methodName}({string.Join(", ", argNames)})";
                    if (isAsync)
                        sb.AppendLine($"                await {innerCall};");
                    else
                        sb.AppendLine($"                {innerCall};");
                    sb.AppendLine("            };");
                    sb.AppendLine();
                    sb.AppendLine("            await act.Should().ThrowAsync<Exception>();");
                    sb.AppendLine("        }");
                    sb.AppendLine();
                }
            }

            return sb.ToString();
        }

        // ---------- Factory helper for non-default ctors ----------

        private static string GenerateFactoryHelper(
            string className,
            List<ConstructorDeclarationSyntax> ctors,
            List<ClassDeclarationSyntax> allClasses)
        {
            var sb = new StringBuilder();

            var primaryCtor = ctors
                .OrderByDescending(c => c.ParameterList.Parameters.Count)
                .FirstOrDefault();

            if (primaryCtor == null || !primaryCtor.ParameterList.Parameters.Any())
                return string.Empty;

            sb.AppendLine($"        private static {className} Create{className}WithSubs()");
            sb.AppendLine("        {");

            var parameters = primaryCtor.ParameterList.Parameters;
            var argNames = new List<string>();

            foreach (var p in parameters)
            {
                string pType = p.Type?.ToString() ?? "object";
                string pName = p.Identifier.Text;
                bool isInterface = pType.StartsWith("I", StringComparison.Ordinal) && !IsPrimitiveType(pType);

                if (isInterface)
                {
                    sb.AppendLine($"            var {pName} = Substitute.For<{pType}>();");
                }
                else
                {
                    string sample = GetSampleValue(pType, IsCollectionType(pType), allClasses);
                    sb.AppendLine($"            var {pName} = {sample};");
                }

                argNames.Add(pName);
            }

            sb.AppendLine();
            sb.AppendLine($"            return new {className}({string.Join(", ", argNames)});");
            sb.AppendLine("        }");
            sb.AppendLine();

            return sb.ToString();
        }

        // ---------- Helper utilities ----------

        private static bool IsStaticMethod(MethodDeclarationSyntax method) =>
            method.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword));

        private static string ExtractTaskType(string returnType)
        {
            if (returnType.StartsWith("Task<", StringComparison.Ordinal) &&
                returnType.EndsWith(">", StringComparison.Ordinal))
            {
                return returnType.Substring(5, returnType.Length - 6);
            }

            return string.Empty;
        }

        private static bool IsAsyncMethod(MethodDeclarationSyntax method, string returnType)
        {
            return method.Modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword)) ||
                   returnType.StartsWith("Task", StringComparison.Ordinal);
        }

        private static bool IsCollectionType(string type)
        {
            return type.Contains("List<", StringComparison.Ordinal) ||
                   type.Contains("IEnumerable<", StringComparison.Ordinal) ||
                   type.Contains("ICollection<", StringComparison.Ordinal) ||
                   type.Contains("IReadOnlyCollection<", StringComparison.Ordinal);
        }

        private static string GetSampleValue(
            string type,
            bool isCollection,
            List<ClassDeclarationSyntax> allClasses)
        {
            if (isCollection)
                return "new List<object> { new object() }";

            if (!IsPrimitiveType(type))
                return $"new {type}()";

            return type switch
            {
                "string" => ""test"",
                "int" or "int?" => "42",
                "long" or "long?" => "42L",
                "short" or "short?" => "(short)42",
                "decimal" or "decimal?" => "42m",
                "double" or "double?" => "42d",
                "float" or "float?" => "42f",
                "bool" or "bool?" => "true",
                "DateTime" or "DateTime?" => "DateTime.UtcNow",
                _ when type.EndsWith("?", StringComparison.Ordinal) => $"default({type})",
                _ => $"default({type})"
            };
        }

        private static bool IsPrimitiveType(string type)
        {
            if (string.IsNullOrWhiteSpace(type))
                return false;

            return type switch
            {
                "string" => true,
                "int" => true,
                "long" => true,
                "short" => true,
                "decimal" => true,
                "double" => true,
                "float" => true,
                "bool" => true,
                "DateTime" => true,
                _ when type.EndsWith("?", StringComparison.Ordinal) =>
                    IsPrimitiveType(type[..^1]),
                _ => false
            };
        }
    }
}