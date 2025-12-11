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
                Console.WriteLine("This tool generates basic unit tests (using NUnit) for public translation methods in the specified model translator class file.");
                Console.WriteLine("Assumes methods with one input parameter (source model) and a return type (target model).");
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
                Console.WriteLine("No tests generated. Ensure the file contains a public class with suitable translation methods (one param, returns target).");
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
                    MetadataReference.CreateFromFile(typeof(object).Assembly.Location))
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
            var translationMethods = new List<MethodDeclarationSyntax>();

            foreach (var member in classDeclaration.Members)
            {
                if (member.IsKind(SyntaxKind.MethodDeclaration))
                {
                    var meth = (MethodDeclarationSyntax)member;
                    if (meth.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword)) &&
                        !meth.ReturnType.IsKind(SyntaxKind.VoidKeyword) &&
                        meth.ParameterList.Parameters.Count == 1)
                    {
                        translationMethods.Add(meth);
                    }
                }
            }

            if (!translationMethods.Any())
            {
                return null;
            }

            var sb = new StringBuilder();
            sb.AppendLine("using NUnit.Framework;");
            sb.AppendLine("using " + namespaceName + ";");
            sb.AppendLine();
            sb.AppendLine("namespace " + namespaceName + ".Tests");
            sb.AppendLine("{");
            sb.AppendLine("    [TestFixture]");
            sb.AppendLine("    public class " + className + "Tests");
            sb.AppendLine("    {");

            // Generate tests for each translation method
            foreach (var method in translationMethods)
            {
                string methodName = method.Identifier.Text;
                string sourceType = method.ParameterList.Parameters.First().Type.ToString();
                string targetType = method.ReturnType.ToString();
                string paramName = method.ParameterList.Parameters.First().Identifier.Text;
                bool isStatic = method.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword));

                sb.AppendLine("        [Test]");
                sb.AppendLine("        public void " + methodName + "_MapsPropertiesCorrectly()");
                sb.AppendLine("        {");
                sb.AppendLine("            // Arrange");
                sb.AppendLine("            var source = new " + sourceType + "()");
                sb.AppendLine("            {");
                sb.AppendLine("                // Set test values for source properties, e.g.:");
                sb.AppendLine("                // Prop1 = \"testValue\",");
                sb.AppendLine("                // Prop2 = 42");
                sb.AppendLine("            };");
                sb.AppendLine("            " + (isStatic ? className + "." : "var translator = new " + className + "();"));
                sb.AppendLine("");
                sb.AppendLine("            // Act");
                sb.AppendLine("            var target = " + (isStatic ? className + "." : "translator.") + methodName + "(" + paramName + ": source);");
                sb.AppendLine("");
                sb.AppendLine("            // Assert");
                sb.AppendLine("            Assert.IsNotNull(target);");
                sb.AppendLine("            // Add property assertions, e.g.:");
                sb.AppendLine("            // Assert.AreEqual(\"testValue\", target.Prop1);");
                sb.AppendLine("            // Assert.AreEqual(42, target.Prop2);");
                sb.AppendLine("        }");
                sb.AppendLine();
            }

            sb.AppendLine("    }");
            sb.AppendLine("}");

            return sb.ToString();
        }
    }
}