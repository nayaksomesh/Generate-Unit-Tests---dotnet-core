using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ClassTestGenerator
{
    internal class Program
    {
        static void Main(string[] args)
        {
            if (args.Length < 1)
            {
                Console.WriteLine("Usage: dotnet run <path-to-source.cs> [output-path]");
                Console.WriteLine("Generates xUnit tests with FluentAssertions and NSubstitute for all public classes.");
                return;
            }

            string inputPath = args[0];
            string outputPath = args.Length > 1 ? args[1] : null;

            if (!File.Exists(inputPath))
            {
                Console.WriteLine($"Error: File '{inputPath}' not found.");
                return;
            }

            string sourceCode = File.ReadAllText(inputPath);
            string generatedTests = GenerateUnitTests(sourceCode);

            if (string.IsNullOrWhiteSpace(generatedTests))
            {
                Console.WriteLine("No tests generated.");
                return;
            }

            if (outputPath != null)
            {
                File.WriteAllText(outputPath, generatedTests);
                Console.WriteLine($"Saved to '{outputPath}'.");
            }
            else
            {
                Console.WriteLine("Generated Tests:");
                Console.WriteLine(generatedTests);
            }
        }

        static string GenerateUnitTests(string sourceCode)
        {
            var tree = CSharpSyntaxTree.ParseText(sourceCode);
            var root = tree.GetRoot();

            var compilation = CSharpCompilation.Create("ClassTestGen")
                .AddReferences(
                    MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                    MetadataReference.CreateFromFile(typeof(List<>).Assembly.Location),
                    MetadataReference.CreateFromFile(typeof(Task).Assembly.Location))
                .AddSyntaxTrees(tree);

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
                string className = classDecl.Identifier.Text;
                var publicCtors = classDecl.Members.OfType<ConstructorDeclarationSyntax>()
                    .Where(c => c.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword)))
                    .ToList();
                bool hasDefaultCtor = publicCtors.Any(c => !c.ParameterList.Parameters.Any());

                var properties = classDecl.Members.OfType<PropertyDeclarationSyntax>()
                    .Where(p => p.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword)))
                    .ToList();

                var methods = classDecl.Members.OfType<MethodDeclarationSyntax>()
                    .Where(m => m.Modifiers.Any(mm => mm.IsKind(SyntaxKind.PublicKeyword))
                                && !m.Modifiers.Any(mm => mm.IsKind(SyntaxKind.StaticKeyword)))
                    .ToList();

                string ctorArrange = GenerateCtorArrange(className, publicCtors, classes, hasDefaultCtor);

                sb.AppendLine($"    public class {className}Tests");
                sb.AppendLine("    {");

                // ctor tests
                sb.AppendLine(GenerateCtorTests(className, publicCtors, ctorArrange, hasDefaultCtor));

                // property tests
                sb.AppendLine(GeneratePropertyTests(className, properties, ctorArrange, hasDefaultCtor, classes));

                // method tests
                sb.AppendLine(GenerateMethodTests(className, methods, ctorArrange, hasDefaultCtor, classes));

                sb.AppendLine("    }");
                sb.AppendLine();
            }

            sb.AppendLine("}");
            return sb.ToString();
        }

        static string GenerateCtorTests(
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
                    bool isInterface = pType.StartsWith("I", StringComparison.Ordinal) &&
                                       !IsPrimitiveType(pType);

                    string varName = pName;

                    if (isInterface)
                    {
                        arrangeLines.Add($"            var {varName} = Substitute.For<{pType}>();");
                    }
                    else
                    {
                        string sample = GetSampleValue(pType, IsCollectionType(pType), new List<ClassDeclarationSyntax>());
                        arrangeLines.Add($"            var {varName} = {sample};");
                    }

                    argNames.Add(varName);
                }

                sb.AppendLine("        [Fact]");
                sb.AppendLine("        public void Ctor_WithDependencies_CreatesInstance()");
                sb.AppendLine("        {");
                if (!string.IsNullOrWhiteSpace(ctorArrange))
                    sb.AppendLine("            // Arrange from generator:");
                if (!string.IsNullOrWhiteSpace(ctorArrange))
                    sb.AppendLine("            // " + ctorArrange);

                foreach (var line in arrangeLines)
                    sb.AppendLine(line);

                sb.AppendLine();
                sb.AppendLine("            // Act");
                sb.AppendLine($"            var instance = new {className}({string.Join(", ", argNames)});");
                sb.AppendLine();
                sb.AppendLine("            // Assert");
                sb.AppendLine("            instance.Should().NotBeNull().And.BeOfType<" + className + ">();");
                sb.AppendLine("        }");
                sb.AppendLine();
            }

            return sb.ToString();
        }

        static string GeneratePropertyTests(
            string className,
            List<PropertyDeclarationSyntax> properties,
            string ctorArrange,
            bool hasDefaultCtor,
            List<ClassDeclarationSyntax> allClasses)
        {
            var sb = new StringBuilder();

            foreach (var prop in properties)
            {
                string propName = prop.Identifier.Text;
                string propType = prop.Type.ToString();
                bool isCollection = IsCollectionType(propType);
                bool hasSetter = prop.AccessorList?.Accessors.Any(a => a.IsKind(SyntaxKind.SetAccessorDeclaration)) == true;

                string instanceExpr = hasDefaultCtor
                    ? $"new {className}()"
                    : $"Create{className}WithSubs()";

                // getter
                sb.AppendLine("        [Fact]");
                sb.AppendLine($"        public void {propName}_DefaultValue_IsReasonable()");
                sb.AppendLine("        {");
                sb.AppendLine($"            var instance = {instanceExpr};");
                sb.AppendLine();

                if (isCollection)
                {
                    sb.AppendLine($"            instance.{propName}.Should().NotBeNull("collection property should not be null by default");");
                }
                else
                {
                    sb.AppendLine($"            // Default value assertion; adjust as needed.");
                    sb.AppendLine($"            _ = instance.{propName};");
                }

                sb.AppendLine("        }");
                sb.AppendLine();

                // setter
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
                    sb.AppendLine("            instance." + propName + ".Should().BeEquivalentTo(expected);");
                    sb.AppendLine("        }");
                    sb.AppendLine();
                }
            }

            // helper factory for non-default ctors
            if (!hasDefaultCtor)
            {
                sb.AppendLine($"        private static {className} Create{className}WithSubs()");
                sb.AppendLine("        {");
                sb.AppendLine("            // TODO: Improve constructor selection logic if multiple public ctors exist.");
                sb.AppendLine("            // For now, prefer the one with most parameters.");
                sb.AppendLine("            throw new NotImplementedException("Constructor factory not implemented; wire NSubstitute manually.");");
                sb.AppendLine("        }");
                sb.AppendLine();
            }

            return sb.ToString();
        }

        static string GenerateMethodTests(
            string className,
            List<MethodDeclarationSyntax> methods,
            string ctorArrange,
            bool hasDefaultCtor,
            List<ClassDeclarationSyntax> allClasses)
        {
            var sb = new StringBuilder();

            foreach (var method in methods)
            {
                string methodName = method.Identifier.Text;
                var parameters = method.ParameterList.Parameters;
                string returnType = method.ReturnType.ToString();
                bool isAsync = IsAsyncMethod(method, returnType);

                bool returnsVoidLike = returnType == "void" || returnType == "Task";
                string innerReturnType = ExtractTaskType(returnType);

                string instanceExpr = hasDefaultCtor
                    ? $"new {className}()"
                    : $"Create{className}WithSubs()";

                // Arrange parameters
                var arrangeLines = new List<string>();
                var argExpressions = new List<string>();

                foreach (var p in parameters)
                {
                    string pType = p.Type?.ToString() ?? "object";
                    string pName = p.Identifier.Text;
                    bool isCollection = IsCollectionType(pType);

                    string sample = p.Default != null
                        ? $"default({pType})"
                        : GetSampleValue(pType, isCollection, allClasses);

                    arrangeLines.Add($"            var {pName} = {sample};");
                    argExpressions.Add($"{pName}");
                }

                string testNamePrefix = returnsVoidLike ? "ExecutesWithoutException" : "ReturnsExpectedDefault";

                sb.AppendLine("        [Fact]");
                sb.AppendLine($"        public async Task {methodName}_{testNamePrefix}()");
                sb.AppendLine("        {");
                sb.AppendLine($"            var instance = {instanceExpr};");

                foreach (var line in arrangeLines)
                    sb.AppendLine(line);

                sb.AppendLine();
                sb.AppendLine("            // Act");

                string call = $"instance.{methodName}({string.Join(", ", argExpressions)})";

                if (!isAsync)
                {
                    if (returnsVoidLike)
                    {
                        sb.AppendLine($"            {call};");
                    }
                    else
                    {
                        sb.AppendLine($"            var result = {call};");
                    }
                }
                else
                {
                    if (returnsVoidLike)
                    {
                        sb.AppendLine($"            await {call};");
                    }
                    else
                    {
                        sb.AppendLine($"            var result = await {call};");
                    }
                }

                sb.AppendLine();
                sb.AppendLine("            // Assert");
                if (!returnsVoidLike)
                {
                    if (!string.IsNullOrWhiteSpace(innerReturnType) && returnType.StartsWith("Task", StringComparison.Ordinal))
                    {
                        sb.AppendLine($"            result.Should().BeOfType<{innerReturnType}>();");
                    }
                    else
                    {
                        sb.AppendLine($"            result.Should().NotBeNull();");
                    }
                }
                else
                {
                    sb.AppendLine("            instance.Should().NotBeNull();");
                }

                sb.AppendLine("        }");
                sb.AppendLine();

                // Exception test heuristic
                if (methodName.Contains("Validate", StringComparison.OrdinalIgnoreCase) ||
                    methodName.Contains("Throw", StringComparison.OrdinalIgnoreCase))
                {
                    sb.AppendLine("        [Fact]");
                    sb.AppendLine($"        public async Task {methodName}_WhenInvalid_ThrowsException()");
                    sb.AppendLine("        {");
                    sb.AppendLine($"            var instance = {instanceExpr};");

                    foreach (var line in arrangeLines)
                        sb.AppendLine(line);

                    sb.AppendLine();
                    sb.AppendLine("            Func<Task> act = async () =>");
                    sb.AppendLine("            {");
                    string callInner = $"instance.{methodName}({string.Join(", ", argExpressions)})";
                    if (isAsync)
                    {
                        sb.AppendLine($"                await {callInner};");
                    }
                    else
                    {
                        sb.AppendLine($"                {callInner};");
                    }

                    sb.AppendLine("            };");
                    sb.AppendLine();
                    sb.AppendLine("            await act.Should().ThrowAsync<Exception>(); // refine exception type/message as needed");
                    sb.AppendLine("        }");
                    sb.AppendLine();
                }
            }

            return sb.ToString();
        }

        static string GenerateCtorArrange(
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
                bool isInterface = paramType.StartsWith("I", StringComparison.Ordinal) &&
                                   !IsPrimitiveType(paramType);

                string varName = param.Identifier.Text;
                if (isInterface)
                {
                    lines.Add($"var {varName} = Substitute.For<{paramType}>();");
                }
                else
                {
                    string sample = GetSampleValue(paramType, IsCollectionType(paramType), allClasses);
                    lines.Add($"var {varName} = {sample};");
                }

                subVars.Add(varName);
            }

            lines.Add($"var instance = new {className}({string.Join(", ", subVars)});");

            return string.Join(" ", lines);
        }

        static string ExtractTaskType(string returnType)
        {
            if (returnType.StartsWith("Task<", StringComparison.Ordinal) &&
                returnType.EndsWith(">", StringComparison.Ordinal))
            {
                return returnType.Substring(5, returnType.Length - 6);
            }

            return string.Empty;
        }

        static bool IsAsyncMethod(MethodDeclarationSyntax method, string returnType)
        {
            return method.Modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword)) ||
                   returnType.StartsWith("Task", StringComparison.Ordinal);
        }

        static bool IsCollectionType(string type)
        {
            return type.Contains("List<", StringComparison.Ordinal) ||
                   type.Contains("IEnumerable<", StringComparison.Ordinal) ||
                   type.Contains("ICollection<", StringComparison.Ordinal) ||
                   type.Contains("IReadOnlyCollection<", StringComparison.Ordinal);
        }

        static string GetSampleValue(string type, bool isCollection, List<ClassDeclarationSyntax> allClasses)
        {
            if (isCollection)
            {
                // Very generic; you can specialize based on element type
                return "new List<object> { new object() }";
            }

            if (!IsPrimitiveType(type))
            {
                return $"new {type}()";
            }

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
                _ when type.EndsWith("?", StringComparison.Ordinal) =>
                    $"default({type})",
                _ => $"default({type})"
            };
        }

        static bool IsPrimitiveType(string type)
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
                    IsPrimitiveType(type.Substring(0, type.Length - 1)),
                _ => false
            };
        }
    }
}