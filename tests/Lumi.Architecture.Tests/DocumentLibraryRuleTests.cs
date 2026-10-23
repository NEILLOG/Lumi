using System.Reflection;
using System.Text.RegularExpressions;

namespace Lumi.Architecture.Tests;

/// <summary>
/// M10 10C：文件讀取的套件（DocumentFormat.OpenXml、PdfPig）只能由 Lumi.Documents 引用；
/// Core 與 Tools 不依賴 Lumi.Documents（Tools 只認 Core 的 IDocumentReader），由 App 組裝。
/// </summary>
public class DocumentLibraryRuleTests
{
    private const string OpenXmlPackage = "DocumentFormat.OpenXml";
    private const string PdfPigPackage = "PdfPig";
    // OpenXml SDK 拆成 DocumentFormat.OpenXml 與 DocumentFormat.OpenXml.Framework 等組件，用前綴比對
    private const string OpenXmlAssemblyPrefix = "DocumentFormat.OpenXml";
    private const string PdfPigAssemblyPrefix = "UglyToad.PdfPig";
    private const string DocumentsProject = "Lumi.Documents";

    private static readonly Regex PackageReference = new("<PackageReference\\s+Include=\"([^\"]+)\"", RegexOptions.Compiled);

    private static string[] ReferencedAssemblyNames(Assembly assembly) =>
        assembly.GetReferencedAssemblies().Select(a => a.Name!).ToArray();

    private static IEnumerable<string> ProjectFiles()
    {
        foreach (var folder in new[] { "src", "tests" })
        {
            var dir = Path.Combine(RepoPaths.Root, folder);
            foreach (var file in Directory.EnumerateFiles(dir, "*.csproj", SearchOption.AllDirectories))
            {
                var parts = file.Split(Path.DirectorySeparatorChar);
                if (parts.Contains("bin") || parts.Contains("obj"))
                    continue;
                yield return file;
            }
        }
    }

    [Fact]
    public void Only_the_documents_project_file_references_the_document_packages()
    {
        var ownProject = Path.Combine(RepoPaths.Root, "src", DocumentsProject, $"{DocumentsProject}.csproj");

        // 對照組：確認偵測方式真的找得到
        var own = PackageReference.Matches(File.ReadAllText(ownProject)).Select(m => m.Groups[1].Value).ToArray();
        Assert.Contains(OpenXmlPackage, own);
        Assert.Contains(PdfPigPackage, own);

        var offenders = ProjectFiles()
            .Where(f => f != ownProject)
            .Where(f => PackageReference.Matches(File.ReadAllText(f))
                .Any(m => m.Groups[1].Value is OpenXmlPackage or PdfPigPackage))
            .Select(Path.GetFileName)
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Documents_project_references_both_document_assemblies()
    {
        var refs = ReferencedAssemblyNames(Assembly.Load(DocumentsProject));

        Assert.Contains(refs, n => n.StartsWith(OpenXmlAssemblyPrefix, StringComparison.Ordinal));
        Assert.Contains(refs, n => n.StartsWith(PdfPigAssemblyPrefix, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Lumi.Core")]
    [InlineData("Lumi.Tools")]
    [InlineData("Lumi.Providers")]
    [InlineData("Lumi.Infrastructure")]
    [InlineData("Lumi.Platform")]
    [InlineData("Lumi.Speech")]
    [InlineData("Lumi.App")]
    public void Other_assemblies_do_not_reference_document_libraries(string name)
    {
        var refs = ReferencedAssemblyNames(Assembly.Load(name));

        Assert.DoesNotContain(refs, n => n.StartsWith(OpenXmlAssemblyPrefix, StringComparison.Ordinal));
        Assert.DoesNotContain(refs, n => n.StartsWith(PdfPigAssemblyPrefix, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Lumi.Core")]
    [InlineData("Lumi.Tools")]
    public void Core_and_Tools_do_not_reference_documents(string name)
    {
        var refs = ReferencedAssemblyNames(Assembly.Load(name));

        Assert.DoesNotContain(DocumentsProject, refs);
    }
}
