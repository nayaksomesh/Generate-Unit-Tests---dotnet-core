using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ProxyTestGenerator
{
    class Program
    {
        static void Main(string[] args)
        {
            if (args.Length < 1)
            {
                Console.WriteLine("Usage: dotnet run <path-to-proxy-class.cs> [output-path]");
                Console.WriteLine("This tool generates basic unit tests (using NUnit) for public properties and methods in the specified proxy class file.");
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
                Console.WriteLine("No tests generated. Ensure the file contains a public class with public properties or methods.");
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
            var compilation = CSharpCompilation.Create("ProxyTestGen")
                .AddReferences(
                    MetadataReference.CreateFromFile(typeof(object).Assembly.Location))
                .AddSyntaxTrees(tree);

            var model = compilation.GetSemanticModel(tree);

            // Find the first public class (assuming it's the proxy class)
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

            // Collect public properties and methods
            var properties = new List<PropertyDeclarationSyntax>();
            var methods = new List<MethodDeclarationSyntax>();

            foreach (var member in classDeclaration.Members)
            {
                if (member.IsKind(SyntaxKind.PropertyDeclaration))
                {
                    var prop = (PropertyDeclarationSyntax)member;
                    if (prop.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword)))
                    {
                        properties.Add(prop);
                    }
                }
                else if (member.IsKind(SyntaxKind.MethodDeclaration))
                {
                    var meth = (MethodDeclarationSyntax)member;
                    if (meth.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword)))
                    {
                        methods.Add(meth);
                    }
                }
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

            // Generate property tests (basic getter/setter)
            foreach (var prop in properties)
            {
                string propName = prop.Identifier.Text;
                string propType = prop.Type.ToString();
                string camelPropName = char.ToLowerInvariant(propName[0]) + propName.Substring(1);

                sb.AppendLine("        [Test]");
                sb.AppendLine("        public void Get_" + propName + "_ReturnsValue()");
                sb.AppendLine("        {");
                sb.AppendLine("            // Arrange");
                sb.AppendLine("            var target = new " + className + "();");
                sb.AppendLine("            var expected = default(" + propType + "); // Replace with actual test value");
                sb.AppendLine("            // If setter exists, set it: target." + camelPropName + " = expected;");
                sb.AppendLine("");
                sb.AppendLine("            // Act");
                sb.AppendLine("            var actual = target." + propName + ";");
                sb.AppendLine("");
                sb.AppendLine("            // Assert");
                sb.AppendLine("            Assert.AreEqual(expected, actual);");
                sb.AppendLine("        }");
                sb.AppendLine();
            }

            // Generate method tests (basic invocation with no params; extend for params if needed)
            foreach (var method in methods)
            {
                string methodName = method.Identifier.Text;
                var paramList = method.ParameterList.Parameters;
                bool hasParams = paramList.Count > 0;

                sb.AppendLine("        [Test]");
                sb.AppendLine("        public void " + methodName + "_ExecutesSuccessfully()");
                sb.AppendLine("        {");
                sb.AppendLine("            // Arrange");
                sb.AppendLine("            var target = new " + className + "();");
                if (hasParams)
                {
                    sb.AppendLine("            // Provide test parameters here, e.g.:");
                    sb.AppendLine("            // var arg1 = ...;");
                }
                sb.AppendLine("");
                sb.AppendLine("            // Act");
                if (hasParams)
                {
                    sb.AppendLine("            // target." + methodName + "(arg1);");
                }
                else
                {
                    sb.AppendLine("            target." + methodName + "();");
                }
                sb.AppendLine("");
                sb.AppendLine("            // Assert");
                sb.AppendLine("            // Add assertions based on expected behavior, e.g., no exceptions thrown");
                sb.AppendLine("            Assert.Pass(); // Placeholder - replace with actual assertion");
                sb.AppendLine("        }");
                sb.AppendLine();
            }

            sb.AppendLine("    }");
            sb.AppendLine("}");

            return sb.ToString();
        }
    }
}
