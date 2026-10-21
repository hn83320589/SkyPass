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
    public void Core_references_no_other_skypass_assembly()
    {
        var references = Assembly.Load("SkyPass.Core").GetReferencedAssemblies().Select(a => a.Name!);

        Assert.DoesNotContain(references, r => r.StartsWith("SkyPass.", StringComparison.Ordinal));
    }
}
