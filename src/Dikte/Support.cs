using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace Dikte;

static class Paths
{
    public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Dikte");
    public static readonly string Settings = Path.Combine(Dir, "settings.json");
    public static readonly string ApiKey = Path.Combine(Dir, "api-key.bin");
    public static readonly string History = Path.Combine(Dir, "history.json");
    public static readonly string Stats = Path.Combine(Dir, "stats.json");
    public static readonly string FailedAudio = Path.Combine(Dir, "last-failed.wav");
    public static readonly string Log = Path.Combine(Dir, "dikte.log");
    public static readonly string Pending = Path.Combine(Dir, "pending");

    /// <summary>Where meeting notes go: the folder set in Settings (environment variables allowed), else Documents\Dikte Meetings.</summary>
    public static string Meetings(Core.Settings s) => s.MeetingsFolder.Length > 0
        ? Path.GetFullPath(Environment.ExpandEnvironmentVariables(s.MeetingsFolder))
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Dikte Meetings");
}

static class Log
{
    static readonly object Gate = new();

    /// <summary>Errors and timings only; transcripts never go in the log.</summary>
    public static void Write(string line)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Paths.Dir);
                var info = new FileInfo(Paths.Log);
                if (info.Exists && info.Length > 1_000_000) File.Move(Paths.Log, Paths.Log + ".old", overwrite: true);
                File.AppendAllText(Paths.Log, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
            }
            catch (IOException) { }
        }
    }
}

/// <summary>The API key, encrypted with Windows DPAPI so only this Windows user can read it.</summary>
static class KeyStore
{
    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("dikte-api-key");

    /// <summary>The saved key, else OPENAI_API_KEY from the environment.</summary>
    public static string? Get()
    {
        try
        {
            if (File.Exists(Paths.ApiKey))
            {
                var plain = ProtectedData.Unprotect(File.ReadAllBytes(Paths.ApiKey), Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plain);
            }
        }
        catch (CryptographicException e)
        {
            Log.Write($"Saved API key unreadable: {e.Message}");
        }
        var env = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        return string.IsNullOrWhiteSpace(env) ? null : env.Trim();
    }

    public static void Save(string key)
    {
        Directory.CreateDirectory(Paths.Dir);
        File.WriteAllBytes(Paths.ApiKey, ProtectedData.Protect(Encoding.UTF8.GetBytes(key.Trim()), Entropy, DataProtectionScope.CurrentUser));
    }
}

static class Autostart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "Dikte";

    public static bool Enabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(Name) is string;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            // --background: at sign-in it waits in the tray instead of opening its window.
            if (value) key.SetValue(Name, $"\"{Environment.ProcessPath}\" --background");
            else key.DeleteValue(Name, throwOnMissingValue: false);
        }
    }
}

/// <summary>Short soft blips, made in memory: no sound files to ship.</summary>
static class Sounds
{
    // Players live as long as the app: PlaySound reads their buffer while the blip plays.
    static readonly System.Media.SoundPlayer StartPlayer = Make(Core.Wav.Tones(44100, 0.18f, (660, 55), (880, 70)));
    static readonly System.Media.SoundPlayer StopPlayer = Make(Core.Wav.Tones(44100, 0.18f, (880, 55), (660, 70)));
    static readonly System.Media.SoundPlayer ErrorPlayer = Make(Core.Wav.Tones(44100, 0.2f, (330, 110), (247, 160)));

    public static void Start() => Play(StartPlayer);
    public static void Stop() => Play(StopPlayer);
    public static void Error() => Play(ErrorPlayer);

    static System.Media.SoundPlayer Make(byte[] wav)
    {
        var player = new System.Media.SoundPlayer(new MemoryStream(wav));
        player.Load();
        return player;
    }

    static void Play(System.Media.SoundPlayer player)
    {
        try { player.Play(); }
        catch (Exception e) when (e is InvalidOperationException or IOException)
        {
            Log.Write($"Sound failed: {e.Message}");
        }
    }
}
