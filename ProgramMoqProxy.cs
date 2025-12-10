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
                Console.WriteLine("This tool generates unit tests (using NUnit and Moq) for public properties and methods in the specified proxy class file.");
                Console.WriteLine("Assumes the proxy has a public constructor with one parameter (the target to mock).");
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
                Console.WriteLine("No tests generated. Ensure the file contains a public class with a suitable constructor, and public properties or methods.");
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

            // Find constructor with one parameter (assumed to be the target)
            ConstructorDeclarationSyntax constructor = classDeclaration.Members
                .OfType<ConstructorDeclarationSyntax>()
                .FirstOrDefault(c => c.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword)) &&
                                      c.ParameterList.Parameters.Count == 1);

            string targetType = "object"; // Fallback
            if (constructor != null)
            {
                var targetParam = constructor.ParameterList.Parameters.First();
                targetType = targetParam.Type.ToString();
            }

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
            sb.AppendLine("using Moq;");
            sb.AppendLine("using " + namespaceName + ";");
            sb.AppendLine();
            sb.AppendLine("namespace " + namespaceName + ".Tests");
            sb.AppendLine("{");
            sb.AppendLine("    [TestFixture]");
            sb.AppendLine("    public class " + className + "Tests");
            sb.AppendLine("    {");

            // Generate property tests (setup mock getter/setter, verify delegation)
            foreach (var prop in properties)
            {
                string propName = prop.Identifier.Text;
                string propType = prop.Type.ToString();
                string camelPropName = char.ToLowerInvariant(propName[0]) + propName.Substring(1);

                sb.AppendLine("        [Test]");
                sb.AppendLine("        public void Get_" + propName + "_DelegatesToTarget()");
                sb.AppendLine("        {");
                sb.AppendLine("            // Arrange");
                sb.AppendLine("            var mockTarget = new Mock<" + targetType + ">();");
                sb.AppendLine("            var expected = default(" + propType + "); // Replace with actual test value, e.g., (string)\"test\";");
                sb.AppendLine("            mockTarget.Setup(m => m." + propName + ").Returns(expected);");
                sb.AppendLine("            var target = mockTarget.Object;");
                sb.AppendLine("            var proxy = new " + className + "(target);");
                sb.AppendLine("");
                sb.AppendLine("            // Act");
                sb.AppendLine("            var actual = proxy." + propName + ";");
                sb.AppendLine("");
                sb.AppendLine("            // Assert");
                sb.AppendLine("            Assert.AreEqual(expected, actual);");
                sb.AppendLine("            mockTarget.Verify(m => m." + propName + ", Times.Once);");
                sb.AppendLine("        }");
                sb.AppendLine();
                if (prop.AccessorList?.Accessors.Any(a => a.IsKind(SyntaxKind.SetAccessorDeclaration)) == true)
                {
                    sb.AppendLine("        [Test]");
                    sb.AppendLine("        public void Set_" + propName + "_DelegatesToTarget()");
                    sb.AppendLine("        {");
                    sb.AppendLine("            // Arrange");
                    sb.AppendLine("            var mockTarget = new Mock<" + targetType + ">();");
                    sb.AppendLine("            var value = default(" + propType + "); // Replace with actual test value");
                    sb.AppendLine("            var target = mockTarget.Object;");
                    sb.AppendLine("            var proxy = new " + className + "(target);");
                    sb.AppendLine("");
                    sb.AppendLine("            // Act");
                    sb.AppendLine("            proxy." + camelPropName + " = value;");
                    sb.AppendLine("");
                    sb.AppendLine("            // Assert");
                    sb.AppendLine("            mockTarget.VerifySet(m => m." + camelPropName + " = value, Times.Once);");
                    sb.AppendLine("        }");
                    sb.AppendLine();
                }
            }

            // Generate method tests (setup mock, call on proxy, verify invocation)
            foreach (var method in methods)
            {
                string methodName = method.Identifier.Text;
                var paramList = method.ParameterList.Parameters;
                bool hasParams = paramList.Count > 0;
                string paramSetup = "";
                string paramAct = "";
                string returnType = method.ReturnType.ToString();

                if (hasParams)
                {
                    // Simple handling: assume first param for setup; extend for more
                    string firstParamType = paramList.First().Type.ToString();
                    string firstParamName = paramList.First().Identifier.Text;
                    paramSetup = "var arg = default(" + firstParamType + "); // Replace with actual test arg\n            mockTarget.Setup(m => m." + methodName + "(It.IsAny<" + firstParamType + ">())).Returns(default(" + returnType + ")); // Adjust return if needed";
                    paramAct = "proxy." + methodName + "(arg);";
                }
                else
                {
                    paramSetup = "mockTarget.Setup(m => m." + methodName + "()).Returns(default(" + returnType + ")); // Adjust return if needed";
                    paramAct = "proxy." + methodName + "();";
                }

                sb.AppendLine("        [Test]");
                sb.AppendLine("        public void " + methodName + "_DelegatesToTarget()");
                sb.AppendLine("        {");
                sb.AppendLine("            // Arrange");
                sb.AppendLine("            var mockTarget = new Mock<" + targetType + ">();");
                sb.AppendLine("            " + paramSetup);
                sb.AppendLine("            var target = mockTarget.Object;");
                sb.AppendLine("            var proxy = new " + className + "(target);");
                sb.AppendLine("");
                sb.AppendLine("            // Act");
                sb.AppendLine("            " + paramAct);
                sb.AppendLine("");
                sb.AppendLine("            // Assert");
                sb.AppendLine("            mockTarget.Verify(m => m." + methodName + "(" + (hasParams ? "It.IsAny<" + paramList.First().Type + ">()" : "") + "), Times.Once);");
                sb.AppendLine("        }");
                sb.AppendLine();
            }

            sb.AppendLine("    }");
            sb.AppendLine("}");

            return sb.ToString();
        }
    }
}
