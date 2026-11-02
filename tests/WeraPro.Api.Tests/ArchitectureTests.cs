namespace WeraPro.Api.Tests;

using System.Text.RegularExpressions;
using Xunit;

/// <summary>
/// فحوص معمارية ملزمة لبوابة G3 (شرط القبول: لا اتصال مباشر من Desktop بـPostgreSQL):
/// 1. مشروع Desktop لا يرجع Npgsql/EF إطلاقاً (لا حزمة ولا using ولا سلسلة اتصال).
/// 2. طبقات الحل تحترم التبعيات المسموحة (Domain لا يعتمد على شيء · Api لا يتخطى إلى قاعدة).
/// 3. لا كلمات مرور/أسرار صريحة في كود Desktop أو Api.
/// تعمل بفحص الملفات — لا تحتاج بناء Windows (B-05 لا يعطل هذا الفحص).
/// </summary>
public sealed class ArchitectureTests
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string SrcRoot = Path.Combine(RepoRoot, "src");
    private static readonly string DesktopRoot = Path.Combine(SrcRoot, "WeraPro.Desktop");

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "WeraPro.sln")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("جذر الحل غير موجود");
    }

    private static IEnumerable<string> ReadAllFiles(string root, string pattern)
    {
        return Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories);
    }

    /// <summary>يحذف التعليقات (XML و//) قبل الفحص — التعليق التوثيقي ليس مرجعاً برمجياً.</summary>
    private static string StripComments(string content)
    {
        content = System.Text.RegularExpressions.Regex.Replace(content, @"<!--.*?-->", string.Empty, System.Text.RegularExpressions.RegexOptions.Singleline);
        content = System.Text.RegularExpressions.Regex.Replace(content, @"/\*.*?\*/", string.Empty, System.Text.RegularExpressions.RegexOptions.Singleline);
        content = System.Text.RegularExpressions.Regex.Replace(content, @"//[^\r\n]*", string.Empty);
        return content;
    }

    [Fact]
    public void Desktop_HasNoDatabasePackages_OrUsings_OrConnectionStrings()
    {
        var csproj = StripComments(File.ReadAllText(Path.Combine(DesktopRoot, "WeraPro.Desktop.csproj")));
        Assert.DoesNotContain("Npgsql", csproj, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("EntityFrameworkCore", csproj, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("WeraPro.Infrastructure", csproj, StringComparison.OrdinalIgnoreCase);

        foreach (var file in ReadAllFiles(DesktopRoot, "*.cs"))
        {
            var code = StripComments(File.ReadAllText(file));
            Assert.DoesNotContain("Npgsql", code, StringComparison.Ordinal);
            Assert.DoesNotContain("Microsoft.EntityFrameworkCore", code, StringComparison.Ordinal);
            Assert.DoesNotContain("WeraPro.Infrastructure", code, StringComparison.Ordinal);
            Assert.DoesNotContain("WeraPro.Application", code, StringComparison.Ordinal);
        }

        foreach (var file in ReadAllFiles(DesktopRoot, "*.json"))
        {
            var config = File.ReadAllText(file);
            Assert.DoesNotMatch("postgresql?://", config);
            Assert.DoesNotContain("ConnectionString", config, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Desktop_ReferencesContractsOnly_ForSharedTypes()
    {
        var csproj = StripComments(File.ReadAllText(Path.Combine(DesktopRoot, "WeraPro.Desktop.csproj")));
        Assert.Contains("WeraPro.Contracts.csproj", csproj, StringComparison.Ordinal);

        // عقود API فقط — لا Domain ولا Application ولا Infrastructure
        Assert.DoesNotContain("WeraPro.Domain.csproj", csproj, StringComparison.Ordinal);
        Assert.DoesNotContain("WeraPro.Application.csproj", csproj, StringComparison.Ordinal);
    }

    [Fact]
    public void Domain_Project_HasNoInfrastructureOrUiDependencies()
    {
        var csproj = File.ReadAllText(Path.Combine(SrcRoot, "WeraPro.Domain", "WeraPro.Domain.csproj"));
        Assert.DoesNotContain("PackageReference", csproj, StringComparison.Ordinal);   // صفر حزم — قواعد صرفة

        foreach (var file in ReadAllFiles(Path.Combine(SrcRoot, "WeraPro.Domain"), "*.cs"))
        {
            var code = StripComments(File.ReadAllText(file));
            Assert.DoesNotContain("Microsoft.EntityFrameworkCore", code, StringComparison.Ordinal);
            Assert.DoesNotContain("Microsoft.AspNetCore", code, StringComparison.Ordinal);
            Assert.DoesNotContain("Npgsql", code, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Api_DoesNotBypassApplicationLayer_WithDirectSqlInEndpoints()
    {
        foreach (var file in ReadAllFiles(Path.Combine(SrcRoot, "WeraPro.Api", "Endpoints"), "*.cs"))
        {
            var code = StripComments(File.ReadAllText(file));
            Assert.DoesNotContain("WeraDbContext", code, StringComparison.Ordinal);   // البوابة الإلزامية Application
            Assert.DoesNotContain("NpgsqlConnection", code, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void NoHardcodedSecrets_InSourceTree()
    {
        var secretPatterns = new Regex[]
        {
            new("password\\s*=\\s*\"(?!\\{|\\$)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new("Host=[^;\"]+;Password=", RegexOptions.Compiled),
        };

        foreach (var project in new[] { "WeraPro.Api", "WeraPro.Desktop", "WeraPro.Infrastructure" })
        {
            foreach (var file in ReadAllFiles(Path.Combine(SrcRoot, project), "*.cs"))
            {
                var code = File.ReadAllText(file);
                foreach (var pattern in secretPatterns)
                {
                    Assert.False(pattern.IsMatch(code),
                        $"نمط سر محتمل في {file}: {pattern}");
                }
            }
        }
    }

    [Fact]
    public void Desktop_AllIoIsAsync_NoBlockingCalls()
    {
        // قاعدة «لا تجميد UI» (شرط بوابة G3): ممنوع .Result/.Wait()/GetAwaiter().GetResult()
        foreach (var file in ReadAllFiles(DesktopRoot, "*.cs"))
        {
            var code = File.ReadAllText(file);
            Assert.DoesNotContain(".Result", code, StringComparison.Ordinal);
            Assert.DoesNotContain(".Wait()", code, StringComparison.Ordinal);
            Assert.DoesNotContain("GetAwaiter().GetResult()", code, StringComparison.Ordinal);
        }
    }
}
