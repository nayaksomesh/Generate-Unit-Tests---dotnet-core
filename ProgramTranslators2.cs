using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ModelTranslatorTestGenerator
{
    class Program
    {
        static void Main(string[] args)
        {
            if (args.Length < 1)
            {
                Console.WriteLine("Usage: dotnet run <path-to-translator-class.cs> [output-path]");
                Console.WriteLine("This tool generates unit tests (using NUnit) for public translation methods in the specified model translator class file.");
                Console.WriteLine("Supports single objects and lists. Parses source/target models (if defined in the same file) to auto-generate sample data and assertions.");
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

            if (string.IsNullOrEmpty(generatedTests))
            {
                Console.WriteLine("No tests generated. Ensure the file contains a public class with suitable translation methods and models.");
                return;
            }

            if (outputPath != null)
            {
                File.WriteAllText(outputPath, generatedTests);
                Console.WriteLine($"Tests generated and saved to '{outputPath}'.");
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
            var compilation = CSharpCompilation.Create("TranslatorTestGen")
                .AddReferences(
                    MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                    MetadataReference.CreateFromFile(typeof(System.Collections.Generic.List<>).Assembly.Location)) // For List support
                .AddSyntaxTrees(tree);

            var model = compilation.GetSemanticModel(tree);

            // Find the first public class (assuming it's the translator class)
            var classDeclaration = root.DescendantNodes()
                .OfType<ClassDeclarationSyntax>()
                .FirstOrDefault(c => c.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword)));

            if (classDeclaration == null)
            {
                return null;
            }

            string className = classDeclaration.Identifier.Text;
            string namespaceName = root.DescendantNodes()
                .OfType<NamespaceDeclarationSyntax>()
                .FirstOrDefault()?.Name.ToString() ?? "DefaultNamespace";

            // Collect public methods that look like translators: one param, non-void return
            var singleTranslationMethods = new List<MethodDeclarationSyntax>();
            var listTranslationMethods = new List<MethodDeclarationSyntax>();

            foreach (var member in classDeclaration.Members)
            {
                if (member.IsKind(SyntaxKind.MethodDeclaration))
                {
                    var meth = (MethodDeclarationSyntax)member;
                    if (meth.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword)) &&
                        !meth.ReturnType.IsKind(SyntaxKind.VoidKeyword) &&
                        meth.ParameterList.Parameters.Count == 1)
                    {
                        string paramType = meth.ParameterList.Parameters.First().Type.ToString();
                        if (paramType.Contains("IEnumerable") || paramType.Contains("List<"))
                        {
                            listTranslationMethods.Add(meth);
                        }
                        else
                        {
                            singleTranslationMethods.Add(meth);
                        }
                    }
                }
            }

            if (!singleTranslationMethods.Any() && !listTranslationMethods.Any())
            {
                return null;
            }

            // Helper to find class by name
            Dictionary<string, ClassDeclarationSyntax> classes = root.DescendantNodes()
                .OfType<ClassDeclarationSyntax>()
                .Where(c => c.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword)))
                .ToDictionary(c => c.Identifier.Text, c => c);

            var sb = new StringBuilder();
            sb.AppendLine("using NUnit.Framework;");
            sb.AppendLine("using System.Collections.Generic;");
            sb.AppendLine("using " + namespaceName + ";");
            sb.AppendLine();
            sb.AppendLine("namespace " + namespaceName + ".Tests");
            sb.AppendLine("{");
            sb.AppendLine("    [TestFixture]");
            sb.AppendLine("    public class " + className + "Tests");
            sb.AppendLine("    {");

            // Generate tests for single object translations
            foreach (var method in singleTranslationMethods)
            {
                string methodName = method.Identifier.Text;
                string sourceType = method.ParameterList.Parameters.First().Type.ToString();
                string targetType = method.ReturnType.ToString();
                string paramName = method.ParameterList.Parameters.First().Identifier.Text;
                bool isStatic = method.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword));

                // Get source class properties
                var sourceProps = GetPublicProperties(classes.ContainsKey(sourceType) ? classes[sourceType] : null);
                var targetProps = GetPublicProperties(classes.ContainsKey(targetType) ? classes[targetType] : null);

                // Generate source init
                string sourceInit = GenerateObjectInit(sourceType, sourceProps, 0); // depth 0 for top-level

                // Generate assertions (assume same prop names)
                string assertions = GenerateAssertions(sourceProps, targetProps);

                sb.AppendLine("        [Test]");
                sb.AppendLine("        public void " + methodName + "_MapsPropertiesCorrectly()");
                sb.AppendLine("        {");
                sb.AppendLine("            // Arrange");
                sb.AppendLine("            " + sourceInit);
                sb.AppendLine("            " + (isStatic ? className + "." : "var translator = new " + className + "();"));
                sb.AppendLine("");
                sb.AppendLine("            // Act");
                sb.AppendLine("            var target = " + (isStatic ? className + "." : "translator.") + methodName + "(" + paramName + ": source);");
                sb.AppendLine("");
                sb.AppendLine("            // Assert");
                sb.AppendLine("            Assert.IsNotNull(target);");
                sb.AppendLine(assertions);
                sb.AppendLine("        }");
                sb.AppendLine();
            }

            // Generate tests for list translations
            foreach (var method in listTranslationMethods)
            {
                string methodName = method.Identifier.Text;
                string paramType = method.ParameterList.Parameters.First().Type.ToString();
                string sourceItemType = ExtractGenericType(paramType); // e.g., "User" from "List<User>"
                string targetType = method.ReturnType.ToString();
                string targetItemType = ExtractGenericType(targetType);
                string paramName = method.ParameterList.Parameters.First().Identifier.Text;
                bool isStatic = method.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword));

                // Get source item properties
                var sourceProps = GetPublicProperties(classes.ContainsKey(sourceItemType) ? classes[sourceItemType] : null);

                // Generate sample sources (two items)
                string source1Init = GenerateObjectInit(sourceItemType, sourceProps, 0);
                string source2Init = GenerateObjectInit(sourceItemType, sourceProps, 0); // Same for simplicity
                string sourcesList = $"var sources = new List<{sourceItemType}> {{\n                {source1Init.Replace("var ", "")},\n                {source2Init.Replace("var ", "")}\n            }};";

                sb.AppendLine("        [Test]");
                sb.AppendLine("        public void " + methodName + "_MapsListPropertiesCorrectly()");
                sb.AppendLine("        {");
                sb.AppendLine("            // Arrange");
                sb.AppendLine("            " + sourcesList);
                sb.AppendLine("            " + (isStatic ? className + "." : "var translator = new " + className + "();"));
                sb.AppendLine("");
                sb.AppendLine("            // Act");
                sb.AppendLine("            var targets = " + (isStatic ? className + "." : "translator.") + methodName + "(" + paramName + ": sources);");
                sb.AppendLine("");
                sb.AppendLine("            // Assert");
                sb.AppendLine("            Assert.IsNotNull(targets);");
                sb.AppendLine("            Assert.AreEqual(2, targets.Count()); // Assuming two items");
                sb.AppendLine("            // Add detailed assertions for first item, e.g.:");
                sb.AppendLine("            // Assert.AreEqual(\"test\", targets.First().Name);");
                sb.AppendLine("        }");
                sb.AppendLine();
            }

            sb.AppendLine("    }");
            sb.AppendLine("}");

            return sb.ToString();
        }

        // Helper to get public properties from class syntax
        static List<(string Name, string Type)> GetPublicProperties(ClassDeclarationSyntax classDecl)
        {
            if (classDecl == null) return new List<(string, string)>();

            return classDecl.Members
                .OfType<PropertyDeclarationSyntax>()
                .Where(p => p.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword)))
                .Select(p => (p.Identifier.Text, p.Type.ToString()))
                .ToList();
        }

        // Generate object initialization with sample values
        static string GenerateObjectInit(string typeName, List<(string Name, string Type)> props, int depth)
        {
            if (depth > 1) return $"new {typeName}()"; // Limit recursion

            var initLines = new List<string> { $"new {typeName}()" };
            foreach (var (name, type) in props.Take(3)) // Limit to 3 props for brevity
            {
                string value = GetDefaultValue(type, depth + 1);
                initLines.Add($"    {name} = {value},");
            }
            return string.Join("\n                ", initLines) + (initLines.Count > 1 ? "\n            " : "");
        }

        // Get default/sample value based on type
        static string GetDefaultValue(string type, int depth)
        {
            if (type == "string") return "\"testValue\"";
            if (type == "int" || type == "int?") return "42";
            if (type == "bool" || type == "bool?") return "true";
            if (type == "DateTime" || type == "DateTime?") return "DateTime.Now";
            if (type.StartsWith("List<") || type.StartsWith("IEnumerable<"))
            {
                string itemType = ExtractGenericType(type);
                string itemInit = GetDefaultValue(itemType, depth);
                return $"new List<{itemType}> {{ {itemInit} }}";
            }
            // For custom types, recurse
            if (!type.Contains(".") && char.IsUpper(type[0])) // Assume custom class
            {
                return $"new {type}()"; // Or recurse if props known, but simplify
            }
            return "default(" + type + ")";
        }

        // Extract inner type from generic, e.g., "User" from "List<User>"
        static string ExtractGenericType(string genericType)
        {
            int open = genericType.IndexOf('<');
            if (open > 0)
            {
                int close = genericType.IndexOf('>', open);
                if (close > open)
                {
                    string inner = genericType.Substring(open + 1, close - open - 1);
                    return inner.Replace(" ", ""); // Clean up
                }
            }
            return genericType;
        }

        // Generate assertion lines assuming matching prop names
        static string GenerateAssertions(List<(string Name, string Type)> sourceProps, List<(string Name, string Type)> targetProps)
        {
            var assertions = new List<string>();
            var commonProps = sourceProps.Select(p => p.Name).Intersect(targetProps.Select(p => p.Name)).Take(3); // Limit
            foreach (string prop in commonProps)
            {
                assertions.Add($"            Assert.AreEqual(source.{prop}, target.{prop});");
            }
            if (!assertions.Any())
            {
                assertions.Add("            // No matching properties found; add custom assertions");
            }
            return string.Join("\n", assertions) + "\n";
        }
    }
}