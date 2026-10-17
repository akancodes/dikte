using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using Dikte.Core;

namespace Dikte;

/// <summary>
/// Keeps Dikte.exe current from the GitHub releases. A newer exe is downloaded next to the running one
/// and checked against the release's SHA-256. A running exe can be renamed but not overwritten, so the
/// swap renames this one to Dikte.exe.old, gives the new file its name and starts it; the new process
/// deletes the .old file once this one has gone.
/// </summary>
sealed class Updater : IDisposable
{
    public const string AssetName = "Dikte.exe";

    // Its own client: the transcription one gives up after 90 s, a 50 MB download on a slow line takes longer.
    readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(10) };

    public static Version Current => typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0);
    public static string CurrentText => Current.ToString(3);

    static string Exe => Environment.ProcessPath ?? throw new InvalidOperationException("The exe's path is unknown.");
    static string Staged => Exe + ".new";
    static string Old => Exe + ".old";

    /// <summary>The newer release, or null when this one is the latest (or nothing is published yet).</summary>
    public async Task<Release?> CheckAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Updates.LatestUrl);
        request.Headers.UserAgent.ParseAdd($"Dikte/{CurrentText}");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await _http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return Updates.Newer(await response.Content.ReadAsStringAsync(ct), Current, AssetName);
    }

    /// <summary>Downloads the release's exe beside this one and returns its path once size and SHA-256 match.</summary>
    public async Task<string> DownloadAsync(Release release, CancellationToken ct)
    {
        var path = Staged;
        try
        {
            using (var response = await _http.GetAsync(release.Url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                await using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
                await response.Content.CopyToAsync(file, ct);
            }
            long size = new FileInfo(path).Length;
            if (size != release.Size) throw new InvalidDataException($"The download is {size} bytes, the release says {release.Size}.");
            string hash;
            await using (var file = File.OpenRead(path)) hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct));
            if (hash != release.Sha256) throw new InvalidDataException("The download does not match the release's SHA-256.");
            return path;
        }
        catch
        {
            TryDelete(path);
            throw;
        }
    }

    /// <summary>
    /// Swaps the downloaded exe in and starts it. On true the caller quits at once (the new process waits
    /// for it). On false everything is back as it was.
    /// </summary>
    public static bool InstallAndRestart(string staged, bool openWindow)
    {
        var exe = Exe;
        try { File.Move(exe, Old, overwrite: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Write($"Update not installed, the exe could not be renamed: {e.Message}");
            TryDelete(staged);
            return false;
        }
        try
        {
            File.Move(staged, exe);
            var args = $"--updated-from {CurrentText}" + (openWindow ? "" : " --background");
            Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false })?.Dispose();
            Log.Write($"Update installed, restarting from {CurrentText}.");
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Win32Exception)
        {
            Log.Write($"Update not installed, putting the old exe back: {e.Message}");
            try
            {
                if (File.Exists(exe)) File.Move(exe, staged, overwrite: true);
                File.Move(Old, exe);
            }
            catch (Exception back) when (back is IOException or UnauthorizedAccessException)
            {
                Log.Write($"Could not put the old exe back ({back.Message}); it is at {Old}.");
            }
            TryDelete(staged);
            return false;
        }
    }

    /// <summary>Deletes what an update left behind. The old process may still be quitting, so it retries for a while.</summary>
    public static async Task CleanUpAsync()
    {
        TryDelete(Staged);
        for (int i = 0; i < 20 && File.Exists(Old); i++)
        {
            if (TryDelete(Old)) break;
            await Task.Delay(500);
        }
    }

    static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    public void Dispose() => _http.Dispose();
}
