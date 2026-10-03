using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

if (args.Length != 1 || !File.Exists(Path.Combine(args[0], "package.json")))
{
    Console.Error.WriteLine("Usage: dotnet run --project Tools~/SourceChecks -- <package-root>");
    return 2;
}

string root = Path.GetFullPath(args[0]);
string[] ignoredDirectories = { ".git", "Tools~", "Validation~", "bin", "obj", "Library", "Temp" };
string[] files = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
    .Where(path => !Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar)
        .Any(segment => ignoredDirectories.Contains(segment, StringComparer.Ordinal)))
    .OrderBy(path => path, StringComparer.Ordinal).ToArray();

if (files.Length == 0)
{
    Console.Error.WriteLine("No Unity C# files found; refusing an empty validation run.");
    return 2;
}

string[] baseline = { "UNITY_6000", "UNITY_6000_0_OR_NEWER", "UNITY_6000_6_OR_NEWER", "UNITY_64" };
var configurations = new[]
{
    (Name: "6.6 editor", Defines: baseline.Concat(new[] { "UNITY_EDITOR", "UNITY_EDITOR_LINUX", "UNITY_INCLUDE_TESTS", "UNITY_STANDALONE", "UNITY_STANDALONE_LINUX" }).ToArray()),
    (Name: "6.6 player", Defines: baseline.Concat(new[] { "UNITY_STANDALONE", "UNITY_STANDALONE_WIN" }).ToArray()),
    (Name: "6.7 editor syntax", Defines: baseline.Concat(new[] { "UNITY_6000_7_OR_NEWER", "UNITY_EDITOR", "UNITY_EDITOR_LINUX", "UNITY_INCLUDE_TESTS", "UNITY_STANDALONE", "UNITY_STANDALONE_LINUX" }).ToArray()),
    (Name: "6.7 player syntax", Defines: baseline.Concat(new[] { "UNITY_6000_7_OR_NEWER", "UNITY_STANDALONE", "UNITY_STANDALONE_WIN" }).ToArray())
};

int errors = 0;
foreach (var configuration in configurations)
{
    var options = new CSharpParseOptions(LanguageVersion.CSharp9, preprocessorSymbols: configuration.Defines);
    foreach (string path in files)
    {
        string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        SyntaxTree tree = CSharpSyntaxTree.ParseText(File.ReadAllText(path), options, relative);
        foreach (Diagnostic diagnostic in tree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error))
        {
            Console.Error.WriteLine($"[{configuration.Name}] {diagnostic}");
            errors++;
        }

        // These checks inspect active player syntax only. Editor-only directories
        // are deliberately parsed above, but Unity does not compile them in a player.
        if (!configuration.Defines.Contains("UNITY_EDITOR") && !relative.Split('/').Contains("Editor") && !relative.StartsWith("Tests/", StringComparison.Ordinal))
        {
            var editorReferences = tree.GetRoot().DescendantNodes().OfType<IdentifierNameSyntax>()
                .Where(node => node.Identifier.ValueText == "UnityEditor");
            foreach (IdentifierNameSyntax reference in editorReferences)
            {
                var line = tree.GetLineSpan(reference.Span).StartLinePosition;
                Console.Error.WriteLine($"[{configuration.Name}] {relative}:{line.Line + 1}: active UnityEditor reference in player source.");
                errors++;
            }
        }
    }
}

Console.WriteLine($"Parsed {files.Length} C# files in {configurations.Length} conditional configurations using C# 9; {errors} errors.");
Console.WriteLine("This is Roslyn syntax validation. Unity API binding, shader compilation, builds, and rendering require the Unity validation job.");
return errors == 0 ? 0 : 1;
