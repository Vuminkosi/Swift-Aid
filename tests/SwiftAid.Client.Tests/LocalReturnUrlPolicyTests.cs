using SwiftAid.Client.Navigation;

namespace SwiftAid.Client.Tests;

public sealed class LocalReturnUrlPolicyTests
{
    private const string Fallback = "/account";

    [Theory]
    [InlineData("/", "/")]
    [InlineData("/case/123", "/case/123")]
    [InlineData("/case/123?tab=history#latest", "/case/123?tab=history#latest")]
    public void Resolve_PreservesLocalReturnPath(string candidate, string expected)
    {
        Assert.Equal(expected, LocalReturnUrlPolicy.Resolve(candidate, Fallback));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("https://attacker.example/path")]
    [InlineData("//attacker.example/path")]
    [InlineData("///attacker.example/path")]
    [InlineData("/\\attacker.example/path")]
    [InlineData("\\\\attacker.example/path")]
    [InlineData("/case\\123")]
    [InlineData("/case/123\rnext")]
    [InlineData("/case/123\nnext")]
    [InlineData("/case/123\tvalue")]
    [InlineData("/case/123\0value")]
    public void Resolve_UsesFallbackForUnsafeReturnPath(string? candidate)
    {
        Assert.Equal(Fallback, LocalReturnUrlPolicy.Resolve(candidate, Fallback));
    }

    [Fact]
    public void Resolve_UsesFallbackForOverlongReturnPath()
    {
        var candidate = $"/{new string('a', 2048)}";

        Assert.Equal(Fallback, LocalReturnUrlPolicy.Resolve(candidate, Fallback));
    }

    [Theory]
    [InlineData(null, "/account")]
    [InlineData("", "/account")]
    [InlineData("cases/123?tab=history#latest", "/cases/123?tab=history#latest")]
    [InlineData("/cases/123?tab=history#latest", "/cases/123?tab=history#latest")]
    [InlineData("//attacker.example/path", "/account")]
    [InlineData("/\\attacker.example/path", "/account")]
    public void FromAppRelativePath_PrefixesOnlySafeLocalPaths(string? appRelativePath, string expected)
    {
        Assert.Equal(expected, LocalReturnUrlPolicy.FromAppRelativePath(appRelativePath, Fallback));
    }

    [Fact]
    public void Resolve_UsesRootWhenFallbackIsUnsafe()
    {
        Assert.Equal("/", LocalReturnUrlPolicy.Resolve("//attacker.example", "https://attacker.example"));
    }
}
