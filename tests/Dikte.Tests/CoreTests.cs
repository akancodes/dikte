using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Dikte.Core;
using Xunit;

public class WavTests
{
    [Fact]
    public void Header_describes_16khz_mono_pcm()
    {
        var pcm = new byte[32000];
        var wav = Wav.FromPcm16(pcm, 16000, 1);
        Assert.Equal(44 + pcm.Length, wav.Length);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(wav, 8, 4));
        Assert.Equal(16000, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(24)));
        Assert.Equal(32000, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(28)));
        Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(22)));
        Assert.Equal(pcm.Length, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(40)));
    }

    [Fact]
    public void Peak_reads_the_loudest_sample()
    {
        var pcm = new byte[8];
        BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(2), -16384);
        BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(4), 1000);
        Assert.Equal(0.5f, Wav.Peak(pcm), 3);
        Assert.Equal(0f, Wav.Peak(new byte[100]));
    }

    [Fact]
    public void Tones_are_valid_wav() => Assert.Equal("RIFF", Encoding.ASCII.GetString(Wav.Tones(44100, 0.2f, (440, 50)), 0, 4));
}

public class SettingsTests
{
    [Fact]
    public void Writes_defaults_then_reads_comments_and_partial_files()
    {
        var dir = Directory.CreateTempSubdirectory();
        var path = Path.Combine(dir.FullName, "settings.json");
        var fresh = Settings.LoadOrCreate(path);
        Assert.Equal("ctrl+win", fresh.Hotkey);
        Assert.Contains("\"hotkey\"", File.ReadAllText(path));

        File.WriteAllText(path, "{\n // my keys\n \"hotkey\": \"rctrl\",\n \"languages\": [\"tr\", \"en\"],\n}");
        var mine = Settings.LoadOrCreate(path);
        Assert.Equal("rctrl", mine.Hotkey);
        Assert.Equal(new[] { "tr", "en" }, mine.Languages);
        Assert.Equal("gpt-transcribe", mine.Model);
    }

    [Fact]
    public void Rejects_bad_values()
    {
        var dir = Directory.CreateTempSubdirectory();
        var path = Path.Combine(dir.FullName, "settings.json");
        File.WriteAllText(path, "{\"insertMode\": \"shout\"}");
        Assert.Throws<FormatException>(() => Settings.LoadOrCreate(path));
    }

    [Fact]
    public void History_keeps_the_newest_and_survives_a_restart()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory().FullName, "history.json");
        var h = new History(path, 2);
        h.Add("one");
        h.Add("two");
        h.Add("three");
        Assert.Equal(new[] { "three", "two" }, h.Items.Select(i => i.Text));
        Assert.Equal(new[] { "three", "two" }, new History(path, 2).Items.Select(i => i.Text));
    }
}

/// <summary>Runs the API client against a tiny local HTTP server that records what it was sent.</summary>
public sealed class TranscriberTests : IDisposable
{
    readonly HttpListener _server = new();
    readonly string _base;
    readonly Queue<(int status, string body)> _answers = new();
    readonly List<(string path, string auth, string body)> _requests = new();

    public TranscriberTests()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        _base = $"http://127.0.0.1:{port}/v1";
        _server.Prefixes.Add($"http://127.0.0.1:{port}/");
        _server.Start();
        _ = Task.Run(async () =>
        {
            while (_server.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _server.GetContextAsync(); } catch { return; }
                using var reader = new StreamReader(ctx.Request.InputStream, Encoding.Latin1);
                var body = await reader.ReadToEndAsync();
                lock (_requests) _requests.Add((ctx.Request.Url!.AbsolutePath, ctx.Request.Headers["Authorization"] ?? "", body));
                var (status, answer) = _answers.Count > 0 ? _answers.Dequeue() : (200, "{}");
                ctx.Response.StatusCode = status;
                var bytes = Encoding.UTF8.GetBytes(answer);
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
        });
    }

    Settings Config(Action<Settings>? edit = null)
    {
        var s = new Settings { ApiBase = _base };
        edit?.Invoke(s);
        return s;
    }

    [Fact]
    public async Task Sends_the_audio_as_multipart_with_model_and_vocabulary()
    {
        _answers.Enqueue((200, "{\"text\":\"  Merhaba, deploy et.  \"}"));
        var api = new Transcriber(new HttpClient());
        var text = await api.TranscribeAsync(Wav.FromPcm16(new byte[640], 16000, 1), Config(s => s.Languages = new() { "tr" }), "sk-test", default);
        Assert.Equal("Merhaba, deploy et.", text);
        var (path, auth, body) = Assert.Single(_requests);
        Assert.Equal("/v1/audio/transcriptions", path);
        Assert.Equal("Bearer sk-test", auth);
        Assert.Contains("name=model", body);
        Assert.Contains("gpt-transcribe", body);
        Assert.Contains("name=language", body);
        Assert.Contains("name=prompt", body);
        Assert.Contains("Claude Code", body);
        Assert.Contains("filename=dictation.wav", body);
        Assert.Contains("audio/wav", body);
        Assert.Contains("RIFF", body);
    }

    [Fact]
    public async Task Mixed_languages_go_in_the_prompt_not_the_language_field()
    {
        _answers.Enqueue((200, "{\"text\":\"hi\"}"));
        await new Transcriber(new HttpClient()).TranscribeAsync(new byte[44], Config(s => s.Languages = new() { "tr", "en" }), "k", default);
        var body = Assert.Single(_requests).body;
        Assert.DoesNotContain("name=language", body);
        Assert.Contains("mixes Turkish and English", body);
    }

    [Fact]
    public async Task Leaves_language_out_when_auto()
    {
        _answers.Enqueue((200, "{\"text\":\"hi\"}"));
        await new Transcriber(new HttpClient()).TranscribeAsync(new byte[44], Config(), "k", default);
        Assert.DoesNotContain("name=language", Assert.Single(_requests).body);
    }

    [Fact]
    public async Task Retries_once_on_a_server_error()
    {
        _answers.Enqueue((500, "{\"error\":{\"message\":\"boom\"}}"));
        _answers.Enqueue((200, "{\"text\":\"ok\"}"));
        Assert.Equal("ok", await new Transcriber(new HttpClient()).TranscribeAsync(new byte[44], Config(), "k", default));
        Assert.Equal(2, _requests.Count);
    }

    [Fact]
    public async Task Reports_a_refused_key_without_retrying()
    {
        _answers.Enqueue((401, "{\"error\":{\"message\":\"Incorrect API key\"}}"));
        var e = await Assert.ThrowsAsync<ApiException>(() => new Transcriber(new HttpClient()).TranscribeAsync(new byte[44], Config(), "k", default));
        Assert.Contains("refused", e.Message);
        Assert.Single(_requests);
    }

    [Fact]
    public async Task Reports_other_errors_with_the_api_message()
    {
        _answers.Enqueue((400, "{\"error\":{\"message\":\"Audio file is too short\"}}"));
        var e = await Assert.ThrowsAsync<ApiException>(() => new Transcriber(new HttpClient()).TranscribeAsync(new byte[44], Config(), "k", default));
        Assert.Equal("API error 400: Audio file is too short", e.Message);
    }

    [Fact]
    public async Task Key_check_reports_the_problem()
    {
        _answers.Enqueue((200, "{\"data\":[]}"));
        _answers.Enqueue((401, "{}"));
        var api = new Transcriber(new HttpClient());
        Assert.Null(await api.CheckKeyAsync(Config(), "good", default));
        Assert.NotNull(await api.CheckKeyAsync(Config(), "bad", default));
    }

    public void Dispose() => _server.Close();
}

public class LanguagesTests
{
    [Fact]
    public void One_language_is_pinned_several_are_hinted()
    {
        Assert.Equal("tr", Languages.Pinned(new[] { "tr" }));
        Assert.Null(Languages.Pinned(new[] { "tr", "en" }));
        Assert.Null(Languages.Pinned(Array.Empty<string>()));
        Assert.Equal("Docker", Languages.BuildPrompt(new[] { "tr" }, " Docker "));
        Assert.Equal("", Languages.BuildPrompt(Array.Empty<string>(), ""));
        var mixed = Languages.BuildPrompt(new[] { "tr", "en" }, "Docker");
        Assert.StartsWith("Konuşma Türkçe ve İngilizce karışık", mixed);
        Assert.EndsWith("Docker", mixed);
        Assert.Contains("mixes German, French and Spanish", Languages.BuildPrompt(new[] { "de", "fr", "es" }, ""));
    }

    [Fact]
    public void Rejects_language_names_instead_of_codes()
    {
        var s = new Settings { Languages = new() { "Turkish" } };
        Assert.Throws<FormatException>(s.Validate);
    }
}

public class StatsTests
{
    [Fact]
    public void Sums_the_week_and_prices_the_month()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory().FullName, "stats.json");
        var stats = new Stats(path);
        var now = new DateTime(2026, 10, 17, 12, 0, 0);
        stats.Record(now, 100, 60, "gpt-transcribe");
        stats.Record(now.AddDays(-6), 50, 30, "gpt-transcribe");
        stats.Record(now.AddDays(-7), 1000, 600, "gpt-transcribe"); // outside the 7 days
        stats.Record(new DateTime(2026, 9, 30), 10, 60, "whisper-1"); // last month

        var s = new Stats(path).Summary(now);
        Assert.Equal(150, s.WeekWords);
        Assert.Equal(1.5, s.WeekMinutes, 3);
        Assert.Equal((60 + 30 + 600) / 60.0 * 0.0045, s.MonthCost, 6);
        Assert.Equal(1160, s.TotalWords);
        Assert.Equal(4, s.TotalCount);
        Assert.Equal((int)Math.Round(1160 / (750 / 60.0)), s.Wpm);
    }

    [Fact]
    public void Other_servers_cost_nothing() => Assert.Equal(0, Stats.PricePerMinute("whisper-large-v3-turbo"));
}
