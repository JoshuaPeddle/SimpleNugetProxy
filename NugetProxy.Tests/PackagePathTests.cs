using NugetProxy.Services;

namespace NugetProxy.Tests;

public class PackagePathTests
{
    [Theory]
    [InlineData("newtonsoft.json/index.json", "newtonsoft.json/index.json")]
    [InlineData("Newtonsoft.Json/index.json", "newtonsoft.json/index.json")]
    [InlineData("newtonsoft.json/13.0.3/newtonsoft.json.13.0.3.nupkg", "newtonsoft.json/13.0.3/newtonsoft.json.13.0.3.nupkg")]
    [InlineData("Newtonsoft.Json/13.0.3/Newtonsoft.Json.13.0.3.nupkg", "newtonsoft.json/13.0.3/newtonsoft.json.13.0.3.nupkg")]
    [InlineData("newtonsoft.json/13.0.3/newtonsoft.json.nuspec", "newtonsoft.json/13.0.3/newtonsoft.json.nuspec")]
    [InlineData("townsuite.foo/26.1.0-beta.1+build.5/townsuite.foo.26.1.0-beta.1+build.5.nupkg", "townsuite.foo/26.1.0-beta.1+build.5/townsuite.foo.26.1.0-beta.1+build.5.nupkg")]
    public void Accepts_what_NuGet_asks_for_and_lower_cases_it(string input, string expected)
    {
        Assert.True(PackagePath.TryParse(input, out var parsed));
        Assert.Equal(expected, parsed!.RelativePath);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("index.json")]
    [InlineData("../index.json")]
    [InlineData("a/../index.json")]
    [InlineData("./index.json")]
    [InlineData("pkg/../../etc/passwd")]
    [InlineData("pkg/1.0.0/../../../etc/passwd")]
    [InlineData("..%2f..%2fetc/index.json")]
    [InlineData("pkg%2f..%2f/index.json")]
    [InlineData("pkg\\..\\index.json")]
    [InlineData("pkg/1.0.0/pkg.1.0.0.nupkg/extra")]
    [InlineData("pkg/1.0.0/other.1.0.0.nupkg")]
    [InlineData("pkg/1.0.0/pkg.1.0.1.nupkg")]
    [InlineData("pkg/1.0.0/pkg.1.0.0.zip")]
    [InlineData("pkg/1.0.0/pkg.1.0.0.nupkg.sha512")]
    [InlineData("pkg/1.0.0/index.json")]
    [InlineData("pkg/index.json/")]
    [InlineData(".hidden/index.json")]
    [InlineData("pkg/..hidden/pkg...hidden.nupkg")]
    [InlineData("pkg name/index.json")]
    [InlineData("pkg/1.0.0\0/pkg.1.0.0\0.nupkg")]
    public void Rejects_anything_else(string? input)
    {
        Assert.False(PackagePath.TryParse(input, out _));
    }

    [Fact]
    public void Rejects_overlong_ids()
    {
        Assert.False(PackagePath.TryParse(new string('a', 101) + "/index.json", out _));
    }

    [Fact]
    public void Version_list_and_files_are_told_apart()
    {
        Assert.True(PackagePath.TryParse("pkg/index.json", out var list));
        Assert.True(list!.IsVersionList);
        Assert.True(PackagePath.TryParse("pkg/1.0.0/pkg.1.0.0.nupkg", out var file));
        Assert.False(file!.IsVersionList);
    }
}
