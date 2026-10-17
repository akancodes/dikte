using System.Text.Json;

namespace Dikte.Core;

/// <summary>A published release's download, as GitHub lists it.</summary>
public sealed record Release(Version Version, string Url, long Size, string Sha256);

/// <summary>Reads GitHub's "latest release" answer. No network here, so it is tested on any machine.</summary>
public static class Updates
{
    public const string LatestUrl = "https://api.github.com/repos/akancodes/dikte/releases/latest";

    /// <summary>
    /// The release in <paramref name="json"/> if it is newer than <paramref name="current"/> and carries
    /// <paramref name="assetName"/> with a SHA-256 digest; otherwise null. A download without a digest is
    /// never installed: the digest is what proves the file is the one the release built.
    /// </summary>
    public static Release? Newer(string json, Version current, string assetName)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (Bool(root, "draft") || Bool(root, "prerelease")) return null;
        if (!root.TryGetProperty("tag_name", out var tag) || ParseTag(tag.GetString()) is not { } version) return null;
        if (version <= Normalize(current)) return null;
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;

        foreach (var asset in assets.EnumerateArray())
        {
            if (Str(asset, "name") != assetName) continue;
            var url = Str(asset, "browser_download_url");
            var digest = Str(asset, "digest");
            long size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out var n) ? n : 0;
            if (url is null || !url.StartsWith("https://github.com/", StringComparison.Ordinal) || size <= 0) return null;
            if (digest is null || !digest.StartsWith("sha256:", StringComparison.Ordinal)) return null;
            var hex = digest["sha256:".Length..].ToLowerInvariant();
            if (hex.Length != 64 || !hex.All(Uri.IsHexDigit)) return null;
            return new Release(version, url, size, hex);
        }
        return null;
    }

    /// <summary>"v0.5.0" → 0.5.0.0; anything else (a pre-release suffix too) → null.</summary>
    public static Version? ParseTag(string? tag)
    {
        if (tag is null || !tag.StartsWith('v')) return null;
        return Version.TryParse(tag[1..], out var v) ? Normalize(v) : null;
    }

    /// <summary>Unset parts compare as 0, so the assembly's 0.5.0.0 equals the tag's 0.5.0.</summary>
    static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));

    static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.True;

    static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
}
