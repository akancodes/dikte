using System.Text.Json;
using Dikte.Core;
using Xunit;

public class ModesTests
{
    static Settings Fresh()
    {
        var s = new Settings();
        s.Normalize();
        return s;
    }

    [Fact]
    public void A_fresh_install_writes_as_heard_with_the_built_in_modes_ready()
    {
        var s = Fresh();
        Assert.Equal(Modes.Plain, s.ActiveMode);
        Assert.Equal(new[] { "clean", "prompt", "list", "commit", "email" }, s.Modes.Select(m => m.Id));
        s.Validate();
    }

    [Fact]
    public void The_old_tidy_up_switch_becomes_the_clean_up_mode()
    {
        var on = JsonSerializer.Deserialize<Settings>("""{ "cleanup": true }""", Settings.Json)!;
        on.Normalize();
        Assert.Equal("clean", on.ActiveMode);
        var off = JsonSerializer.Deserialize<Settings>("""{ "cleanup": false }""", Settings.Json)!;
        off.Normalize();
        Assert.Equal(Modes.Plain, off.ActiveMode);
    }

    [Fact]
    public void A_deleted_active_mode_falls_back_to_as_heard()
    {
        var s = JsonSerializer.Deserialize<Settings>("""{ "activeMode": "gone" }""", Settings.Json)!;
        s.Normalize();
        Assert.Equal(Modes.Plain, s.ActiveMode);
    }

    [Fact]
    public void The_modes_round_trip_through_the_file()
    {
        var s = Fresh();
        s.Modes.Add(new Mode { Id = "tweet", Name = "Tweet", Instruction = "Under 280 characters.", Apps = { "chrome" }, Hotkey = "f10" });
        s.ActiveMode = "tweet";
        var back = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(s, Settings.Json), Settings.Json)!;
        back.Normalize();
        Assert.Equal("tweet", back.ActiveMode);
        var tweet = back.Modes.Single(m => m.Id == "tweet");
        Assert.Equal(new[] { "chrome" }, tweet.Apps);
        Assert.Equal("f10", tweet.Hotkey);
        Assert.Equal(6, back.Modes.Count);
    }

    [Fact]
    public void A_shortcut_beats_the_app_which_beats_the_active_mode()
    {
        var s = Fresh();
        s.ActiveMode = "list";
        Assert.Equal("email", Modes.Resolve(s, "email", "WindowsTerminal")!.Id);
        Assert.Equal("prompt", Modes.Resolve(s, null, "WindowsTerminal")!.Id);
        Assert.Equal("prompt", Modes.Resolve(s, null, "Code.exe")!.Id);
        Assert.Equal("prompt", Modes.Resolve(s, null, "iterm2")!.Id);
        Assert.Equal("list", Modes.Resolve(s, null, "notepad")!.Id);
        Assert.Equal("list", Modes.Resolve(s, null, null)!.Id);
    }

    [Fact]
    public void As_heard_is_no_mode_at_all()
    {
        var s = Fresh();
        Assert.Null(Modes.Resolve(s, null, "notepad"));
        Assert.Null(Modes.Resolve(s, Modes.Plain, "WindowsTerminal"));
        Assert.Equal("As spoken", Modes.NameOf(s, Modes.Plain));
    }

    [Fact]
    public void Next_goes_round_through_as_heard()
    {
        var s = Fresh();
        var seen = new List<string>();
        for (int i = 0; i < 7; i++)
        {
            s.ActiveMode = Modes.Next(s);
            seen.Add(s.ActiveMode);
        }
        Assert.Equal(new[] { "clean", "prompt", "list", "commit", "email", Modes.Plain, "clean" }, seen);
    }

    [Theory]
    [InlineData("Slack reply", "slack-reply")]
    [InlineData("Türkçe özet", "t-rk-e-zet")]
    [InlineData("!!!", "mode")]
    [InlineData("Plain", "mode")]
    [InlineData("List", "list-2")]
    public void New_ids_are_unique_slugs(string name, string id) =>
        Assert.Equal(id, Modes.NewId(name, Fresh().Modes.Select(m => m.Id)));

    [Fact]
    public void Modes_need_a_name_an_instruction_and_their_own_shortcut()
    {
        var s = Fresh();
        s.Modes[0].Name = "";
        Assert.Throws<FormatException>(s.Validate);

        s = Fresh();
        s.Modes[1].Instruction = "";
        Assert.Throws<FormatException>(s.Validate);

        s = Fresh();
        s.Modes[1].Hotkey = s.Hotkey;
        Assert.Throws<FormatException>(s.Validate);

        s = Fresh();
        s.Modes[1].Hotkey = "ctrl+mouse4";
        s.NextModeHotkey = "f8";
        s.Validate();
        s.NextModeHotkey = "ctrl+mouse4";
        Assert.Throws<FormatException>(s.Validate);

        s = Fresh();
        s.Modes[2].Id = "clean";
        Assert.Throws<FormatException>(s.Validate);
    }

    [Fact]
    public void A_hand_written_mode_without_an_id_gets_one()
    {
        var s = JsonSerializer.Deserialize<Settings>("""{ "modes": [ { "name": "Slack", "instruction": "Casual.", "apps": [" slack ", "Slack"] } ] }""", Settings.Json)!;
        s.Normalize();
        var m = Assert.Single(s.Modes);
        Assert.Equal("slack", m.Id);
        Assert.Equal(new[] { "slack" }, m.Apps);
        s.Validate();
    }
}

public class RewriteTests
{
    sealed class Stub(string reply) : HttpMessageHandler
    {
        public string? Body;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Body = await request.Content!.ReadAsStringAsync(ct);
            var json = JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = reply } } } });
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(json) };
        }
    }

    [Fact]
    public async Task The_mode_instruction_and_model_go_to_the_chat_model()
    {
        var stub = new Stub("- one\n- two");
        var s = new Settings();
        s.Normalize();
        var mode = new Mode { Id = "list", Name = "List", Instruction = "As a list.", Model = "gpt-4.1" };
        var text = await new Transcriber(new HttpClient(stub)).RewriteAsync("one and two", mode, s, "k", default);
        Assert.Equal("- one\n- two", text);
        var body = JsonDocument.Parse(stub.Body!).RootElement;
        Assert.Equal("gpt-4.1", body.GetProperty("model").GetString());
        Assert.Contains("<mode>\nAs a list.\n</mode>", body.GetProperty("messages")[1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task Without_its_own_model_a_mode_uses_the_clean_up_model()
    {
        var stub = new Stub("ok");
        var s = new Settings { CleanupModel = "gpt-4.1-mini" };
        await new Transcriber(new HttpClient(stub)).RewriteAsync("ok", new Mode { Instruction = "x" }, s, "k", default);
        Assert.Equal("gpt-4.1-mini", JsonDocument.Parse(stub.Body!).RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task An_answer_instead_of_a_rewrite_keeps_the_words_as_heard()
    {
        var stub = new Stub(new string('x', 2000));
        var text = await new Transcriber(new HttpClient(stub)).RewriteAsync("what is a monad", new Mode { Instruction = "x" }, new Settings(), "k", default);
        Assert.Equal("what is a monad", text);
    }
}
