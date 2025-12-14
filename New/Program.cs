// Program.cs
using System;
using System.IO;

namespace ClassTestGenerator
{
    internal class Program
    {
        static void Main(string[] args)
        {
            if (args.Length < 1)
            {
                Console.WriteLine("Usage: dotnet run <path-to-source.cs> [output-path]");
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
            string generatedTests = TestGenerator.GenerateUnitTests(sourceCode);

            if (string.IsNullOrWhiteSpace(generatedTests))
            {
                Console.WriteLine("No tests generated.");
                return;
            }

            if (outputPath != null)
            {
                File.WriteAllText(outputPath, generatedTests);
                Console.WriteLine($"Saved tests to '{outputPath}'.");
            }
            else
            {
                Console.WriteLine("Generated Tests:");
                Console.WriteLine(generatedTests);
            }
        }
    }
}