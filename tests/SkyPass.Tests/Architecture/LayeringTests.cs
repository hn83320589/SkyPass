using System.Reflection;

namespace SkyPass.Tests.Architecture;

public class LayeringTests
{
    private static readonly string[] ForbiddenPrefixes =
    [
        "Silk.NET", "ImGui", "System.Windows", "PresentationFramework", "PresentationCore",
        "WindowsBase", "Microsoft.Maui", "Avalonia", "Uno", "Microsoft.Data.Sqlite", "SkyPass.App",
    ];

    [Theory]
    [InlineData("SkyPass.Core")]
    [InlineData("SkyPass.Presenters")]
    public void Pure_libraries_do_not_reference_ui_or_storage_assemblies(string assemblyName)
    {
        var references = Assembly.Load(assemblyName).GetReferencedAssemblies().Select(a => a.Name!).ToList();

        var offending = references.Where(r => ForbiddenPrefixes.Any(p => r.StartsWith(p, StringComparison.Ordinal)));

        Assert.Empty(offending);
    }

    [Fact]
    public void Source_never_reads_the_system_clock_directly()
    {
        // 規格：所有「現在時間」都必須透過 TimeProvider
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "SkyPass.sln")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        var pattern = new System.Text.RegularExpressions.Regex(@"DateTime(Offset)?\.(Now|UtcNow|Today)\b|Stopwatch\.");
        var offenders = Directory.EnumerateFiles(Path.Combine(root.FullName, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (f, i, line)))
            .Where(x => pattern.IsMatch(x.line))
            .Select(x => $"{Path.GetFileName(x.f)}:{x.i + 1}")
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Core_references_no_other_skypass_assembly()
    {
        var references = Assembly.Load("SkyPass.Core").GetReferencedAssemblies().Select(a => a.Name!);

        Assert.DoesNotContain(references, r => r.StartsWith("SkyPass.", StringComparison.Ordinal));
    }
}
