using System.Buffers.Binary;
using Dikte.Core;
using Xunit;

public class SegmenterTests
{
    const int Rate = 16000;

    /// <summary>16 kHz PCM: a 220 Hz tone (speech enough for the detector) or silence, for the given seconds.</summary>
    static byte[] Audio(double seconds, float volume)
    {
        int n = (int)(seconds * Rate);
        var pcm = new byte[n * 2];
        for (int i = 0; i < n; i++)
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), (short)(Math.Sin(2 * Math.PI * 220 * i / Rate) * volume * short.MaxValue));
        return pcm;
    }

    /// <summary>Talk-like: 200 ms sounds with 50 ms gaps between them.</summary>
    static byte[] Speech(double seconds)
    {
        var pcm = Audio(seconds, 0.3f);
        for (int ms = 200; ms < seconds * 1000; ms += 250) Array.Clear(pcm, ms * 32, Math.Min(50 * 32, pcm.Length - ms * 32));
        return pcm;
    }
    static byte[] Silence(double seconds) => Audio(seconds, 0);

    /// <summary>Feeds the parts back to back, in 40 ms buffers like a sound card, from the given clock time.</summary>
    static List<Utterance> Run(Segmenter s, double start, params byte[][] parts)
    {
        var all = parts.SelectMany(p => p).ToArray();
        var found = new List<Utterance>();
        for (int i = 0; i < all.Length; i += 1280)
            found.AddRange(s.Feed(all.AsSpan(i, Math.Min(1280, all.Length - i)), start + i / 2.0 / Rate));
        return found;
    }

    [Fact]
    public void Cuts_at_the_pauses_and_keeps_the_times()
    {
        var s = new Segmenter();
        var found = Run(s, 0, Silence(1), Speech(2), Silence(1.5), Speech(1), Silence(2));
        Assert.Equal(2, found.Count);
        Assert.Equal(1 - 0.24, found[0].Start, 2);   // a little before the first word
        Assert.InRange(found[0].End, 3.0, 3.3);      // and a little after the last
        Assert.Equal(4.5 - 0.24, found[1].Start, 2);
        Assert.Equal(found[0].Pcm.Length, (int)Math.Round(found[0].Seconds * Rate * 2));
    }

    [Fact]
    public void Silence_and_clicks_send_nothing()
    {
        var s = new Segmenter();
        Assert.Empty(Run(s, 0, Silence(5), Speech(0.1), Silence(3)));
        Assert.Empty(s.Flush());
    }

    [Fact]
    public void A_fan_is_not_speech_once_the_floor_has_learned_it()
    {
        var s = new Segmenter();
        // A steady low hum above the fixed minimum: taken for talk at first, then learned as the floor.
        Assert.True(Run(s, 0, Audio(30, 0.012f)).Count <= 1);
        Assert.Empty(Run(s, 30, Audio(10, 0.012f)));
        var hum = Audio(1, 0.012f);
        var talk = Speech(1);
        for (int i = 0; i < talk.Length; i += 2)
            BinaryPrimitives.WriteInt16LittleEndian(talk.AsSpan(i), (short)(BinaryPrimitives.ReadInt16LittleEndian(talk.AsSpan(i)) + BinaryPrimitives.ReadInt16LittleEndian(hum.AsSpan(i))));
        Assert.Single(Run(s, 40, talk, Audio(2, 0.012f)));
    }

    [Fact]
    public void Long_speech_is_cut_at_a_quiet_moment()
    {
        var s = new Segmenter(maxSeconds: 10);
        var found = Run(s, 0, Speech(8.5), Silence(0.3), Speech(8), Silence(2));
        Assert.Equal(2, found.Count);
        Assert.InRange(found[0].End, 8.5, 8.8);       // inside the short pause, not at 10 s
        Assert.Equal(found[0].End, found[1].Start, 3);
    }

    [Fact]
    public void A_gap_in_the_stream_is_a_pause()
    {
        var s = new Segmenter();
        var first = Run(s, 0, Speech(1));
        Assert.Empty(first);
        // The app played nothing for two seconds, then spoke again.
        var found = Run(s, 3, Speech(1), Silence(1.5));
        Assert.Equal(2, found.Count);
        Assert.Equal(3, found[1].Start, 2); // nothing was heard just before it to keep
    }

    [Fact]
    public void Flush_ends_the_open_utterance()
    {
        var s = new Segmenter();
        Assert.Empty(Run(s, 0, Speech(1.2)));
        var last = Assert.Single(s.Flush());
        Assert.Equal(1.2, last.End, 2);
    }
}

public class MeetingFileTests
{
    static MeetingDoc Sample() => new()
    {
        Title = "Fazly ve muhasebe",
        Started = new DateTime(2026, 10, 26, 14, 30, 0),
        Minutes = 42.4,
        App = "Discord",
        Me = "Bayram",
        With = "Ömer",
        Status = "done",
        Notes = "Kısa özet.\n\n## Yapılacaklar\n- [ ] Ömer: ekran kaydı",
        Lines =
        {
            new TranscriptLine(0, "Bayram", "Ömer selam, sesim geliyor mu?"),
            new TranscriptLine(62.7, "Ömer", "Geliyor: selam."),
            new TranscriptLine(3725, "Bayram", "Görüşürüz."),
        },
    };

    [Fact]
    public void Writes_and_reads_back_the_same_meeting()
    {
        var text = MeetingFile.Format(Sample());
        Assert.StartsWith("---\ndikte: meeting\ntitle: Fazly ve muhasebe\nstarted: 2026-10-26 14:30\nminutes: 42\n", text);
        Assert.Contains("# Fazly ve muhasebe\n\nKısa özet.", text);
        Assert.Contains("**[01:02] Ömer:** Geliyor: selam.", text);
        Assert.Contains("**[1:02:05] Bayram:** Görüşürüz.", text);

        var back = MeetingFile.Parse(text.Replace("\n", "\r\n"))!; // edited on Windows
        Assert.Equal("Fazly ve muhasebe", back.Title);
        Assert.Equal(new DateTime(2026, 10, 26, 14, 30, 0), back.Started);
        Assert.Equal(42, back.Minutes);
        Assert.Equal(("Discord", "Bayram", "Ömer", "done"), (back.App, back.Me, back.With, back.Status));
        Assert.Equal("Kısa özet.\n\n## Yapılacaklar\n- [ ] Ömer: ekran kaydı", back.Notes);
        Assert.Equal(new[] { 0.0, 62, 3725 }, back.Lines.Select(l => l.At));
        Assert.Equal("Geliyor: selam.", back.Lines[1].Text);
        Assert.Equal("Ömer", back.Lines[1].Speaker);
    }

    [Fact]
    public void A_meeting_still_recording_has_no_notes_yet()
    {
        var d = Sample();
        d.Notes = "";
        d.Status = "recording";
        d.Pending = 2;
        var back = MeetingFile.Parse(MeetingFile.Format(d))!;
        Assert.Equal("", back.Notes);
        Assert.Equal(3, back.Lines.Count);
        Assert.Equal(("recording", 2), (back.Status, back.Pending));
    }

    [Fact]
    public void Other_markdown_is_not_a_meeting()
    {
        Assert.Null(MeetingFile.Parse("# Shopping\n- milk"));
        Assert.Null(MeetingFile.Parse("---\ntitle: a blog post\n---\nhello"));
    }

    [Fact]
    public void File_names_sort_by_date_and_are_safe()
    {
        var at = new DateTime(2026, 10, 26, 9, 5, 0);
        Assert.Equal("2026-10-26 09.05 Fazly ve muhasebe.md", MeetingFile.FileName(at, "Fazly ve muhasebe"));
        Assert.Equal("2026-10-26 09.05 Q4 plan why now.md", MeetingFile.FileName(at, "Q4: plan / why now?"));
        Assert.Equal("2026-10-26 09.05.md", MeetingFile.FileName(at, "  "));
        Assert.True(MeetingFile.FileName(at, new string('a', 200)).Length <= 80);
    }
}

public class MeetingNotesTests
{
    [Fact]
    public void Drops_what_the_microphone_heard_from_the_speakers()
    {
        var lines = MeetingNotes.Clean(new[]
        {
            new MeetingLine(10, 14, false, "Almanca açıklamayı cuma gününe kadar hazırlarım."),
            new MeetingLine(10.4, 14.2, true, "Almanca açıklamayı cuma günü hazırlarım"),   // echo
            new MeetingLine(15, 16, true, "Tamam, süper, teşekkürler."),
            new MeetingLine(2, 4, true, "Ömer selam, sesim geliyor mu?"),
            new MeetingLine(5, 6, false, "Altyazı M.K."),                               // noise
            new MeetingLine(30, 32, true, "Almanca açıklamayı cuma gününe kadar sen hazırla"), // later, so said again on purpose
        });
        Assert.Equal(new[] { 2.0, 10, 15, 30 }, lines.Select(l => l.Start));
        Assert.False(lines[1].Mine);
    }

    [Fact]
    public void Short_answers_on_both_sides_are_both_kept() =>
        Assert.Equal(2, MeetingNotes.Clean(new[] { new MeetingLine(1, 2, true, "Evet."), new MeetingLine(1.2, 2, false, "Evet.") }).Count);

    [Fact]
    public void Noise_is_recognised()
    {
        Assert.True(MeetingNotes.IsNoise(" ... "));
        Assert.True(MeetingNotes.IsNoise("Thanks for watching!"));
        Assert.True(MeetingNotes.IsNoise("İzlediğiniz için teşekkür ederim."));
        Assert.False(MeetingNotes.IsNoise("Evet."));
    }

    [Fact]
    public void The_request_says_who_was_there_then_what_was_said()
    {
        var d = new MeetingDoc
        {
            Started = new DateTime(2026, 10, 26, 14, 30, 0), App = "Discord", Me = "Bayram", With = "Ömer",
            Lines = { new TranscriptLine(65, "Ömer", "Cuma olur.") },
        };
        var r = MeetingNotes.Request(d);
        Assert.StartsWith("<meeting>\nDate: Monday 26 October 2026, 14:30\nRecorded in: Discord\nRecorded by: Bayram\nWith: Ömer\n</meeting>", r);
        Assert.EndsWith("<transcript>\n[01:05] Ömer: Cuma olur.\n</transcript>", r);
        Assert.Contains("the room", MeetingNotes.Request(new MeetingDoc()));
    }

    [Fact]
    public void Splits_the_title_from_the_notes()
    {
        Assert.Equal(("Fazly", "Özet.\n\n## Kararlar"), MeetingNotes.Split("# Fazly\n\nÖzet.\n\n## Kararlar\n"));
        Assert.Equal(("Fazly", "Özet."), MeetingNotes.Split("```markdown\n# Fazly\nÖzet.\n```"));
        Assert.Equal(("", "Just notes."), MeetingNotes.Split("Just notes."));
    }

    [Fact]
    public void Names_the_other_side()
    {
        Assert.Equal("Ömer", MeetingNotes.TheirName(" Ömer ", "Discord"));
        Assert.Equal("Discord", MeetingNotes.TheirName("Ömer, Ali", "Discord"));
        Assert.Equal("Discord", MeetingNotes.TheirName("", "Discord"));
        Assert.Equal("Them", MeetingNotes.TheirName("", ""));
    }

    [Fact]
    public void Matches_the_apps_variants()
    {
        Assert.True(MeetingNotes.IsApp("DiscordPTB", "Discord"));
        Assert.True(MeetingNotes.IsApp("discord", "Discord"));
        Assert.False(MeetingNotes.IsApp("Zoom", "Discord"));
        Assert.False(MeetingNotes.IsApp("Discord", ""));
    }
}

public class CallWatchTests
{
    static readonly DateTime T0 = new(2026, 10, 26, 14, 0, 0);

    [Fact]
    public void A_call_starts_after_a_few_seconds_and_ends_a_minute_after_the_microphone_is_let_go()
    {
        var w = new CallWatch(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(60));
        Assert.Empty(w.Observe(new[] { "Discord" }, T0));
        Assert.Empty(w.Observe(new[] { "Discord" }, T0.AddSeconds(2)));
        Assert.Equal(new[] { ("Discord", true) }, w.Observe(new[] { "Discord" }, T0.AddSeconds(4)));
        Assert.Equal(new[] { "Discord" }, w.InCall);
        // The microphone is let go briefly (a device switch): still in the call.
        Assert.Empty(w.Observe(Array.Empty<string>(), T0.AddSeconds(30)));
        Assert.Empty(w.Observe(new[] { "Discord" }, T0.AddSeconds(40)));
        Assert.Empty(w.Observe(Array.Empty<string>(), T0.AddSeconds(90)));
        Assert.Equal(new[] { ("Discord", false) }, w.Observe(Array.Empty<string>(), T0.AddSeconds(101)));
        Assert.Empty(w.InCall);
    }

    [Fact]
    public void A_moment_of_microphone_is_not_a_call()
    {
        var w = new CallWatch(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(60));
        w.Observe(new[] { "Discord" }, T0);
        w.Observe(Array.Empty<string>(), T0.AddSeconds(4));
        Assert.Empty(w.Observe(new[] { "Discord" }, T0.AddSeconds(20)));
        Assert.Empty(w.InCall);
    }
}

public class MeetingSettingsTests
{
    [Fact]
    public void Discord_is_listened_to_out_of_the_box()
    {
        var s = new Settings();
        s.Normalize();
        Assert.Equal(new[] { "Discord" }, s.MeetingApps);
        Assert.True(s.MeetingOffer);
        Assert.Equal("gpt-6-luna", s.MeetingModel);
    }

    [Fact]
    public void The_meeting_shortcut_cannot_be_another_ones()
    {
        var s = new Settings { ToggleHotkey = "f9", MeetingHotkey = "f9" };
        Assert.Contains("Meeting notes", Assert.Throws<FormatException>(s.Validate).Message);
    }

    [Fact]
    public void Apps_are_process_names()
    {
        var s = new Settings { MeetingApps = new() { @"C:\Discord\Discord.exe" } };
        Assert.Throws<FormatException>(s.Validate);
        s = new Settings { MeetingApps = new() { " Discord ", "discord", "", "Zoom" } };
        s.Normalize();
        Assert.Equal(new[] { "Discord", "Zoom" }, s.MeetingApps);
    }

    [Fact]
    public void Meeting_audio_costs_but_is_not_dictation()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory().FullName, "stats.json");
        var stats = new Stats(path);
        var now = new DateTime(2026, 10, 26, 15, 0, 0);
        stats.RecordSpend(now, 600, "gpt-transcribe");
        var s = stats.Summary(now);
        Assert.Equal(10 * 0.0045, s.MonthCost, 6);
        Assert.Equal((0, 0), (s.WeekWords, s.TotalCount));
    }
}
