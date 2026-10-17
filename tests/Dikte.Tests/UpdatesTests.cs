using Dikte.Core;
using Xunit;

public class UpdatesTests
{
    const string Hash = "4df85caed0a5f5fc66fd3b6e3ca7a7a5d692c3802fcf30c70e306f2253cc52a9";

    // The shape of GitHub's /releases/latest answer, cut down to what is read.
    static string Json(string tag = "v0.5.0", string? digest = "sha256:" + Hash, bool prerelease = false,
                       string url = "https://github.com/akancodes/dikte/releases/download/v0.5.0/Dikte.exe") => $$"""
        {
          "tag_name": "{{tag}}", "draft": false, "prerelease": {{(prerelease ? "true" : "false")}},
          "assets": [
            { "name": "Dikte-mac.zip", "size": 562582, "digest": "sha256:{{Hash}}",
              "browser_download_url": "https://github.com/akancodes/dikte/releases/download/v0.5.0/Dikte-mac.zip" },
            { "name": "Dikte.exe", "size": 52333225, {{(digest is null ? "" : $"\"digest\": \"{digest}\",")}}
              "browser_download_url": "{{url}}" }
          ]
        }
        """;

    static readonly Version Installed = new(0, 4, 1, 0); // what the assembly reports

    [Fact]
    public void A_newer_release_is_found_with_its_exe()
    {
        var r = Updates.Newer(Json(), Installed, "Dikte.exe");
        Assert.NotNull(r);
        Assert.Equal(new Version(0, 5, 0, 0), r.Version);
        Assert.Equal(52333225, r.Size);
        Assert.Equal(Hash, r.Sha256);
        Assert.EndsWith("/Dikte.exe", r.Url);
    }

    [Theory]
    [InlineData("v0.4.1")]   // the same version: 0.4.1 equals 0.4.1.0
    [InlineData("v0.4.0")]   // older
    [InlineData("v0.5.0-beta")]
    [InlineData("0.5.0")]
    public void Not_newer_or_odd_tags_are_ignored(string tag) => Assert.Null(Updates.Newer(Json(tag), Installed, "Dikte.exe"));

    [Fact]
    public void Prereleases_are_ignored() => Assert.Null(Updates.Newer(Json(prerelease: true), Installed, "Dikte.exe"));

    [Theory]
    [InlineData(null)]
    [InlineData("sha1:abc")]
    [InlineData("sha256:nothex")]
    public void An_exe_without_a_proper_digest_is_never_offered(string? digest) =>
        Assert.Null(Updates.Newer(Json(digest: digest), Installed, "Dikte.exe"));

    [Fact]
    public void Only_github_downloads_are_taken() =>
        Assert.Null(Updates.Newer(Json(url: "https://example.com/Dikte.exe"), Installed, "Dikte.exe"));

    [Fact]
    public void A_missing_asset_means_no_update() => Assert.Null(Updates.Newer(Json(), Installed, "Dikte-arm64.exe"));
}
