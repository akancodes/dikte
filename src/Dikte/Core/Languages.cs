namespace Dikte.Core;

/// <summary>
/// Turns "the languages I speak" into what the transcription API understands. One language is pinned with
/// the language parameter; several are described in the prompt, because the API takes only one code and
/// pinning one makes the model translate the other.
/// </summary>
public static class Languages
{
    public static readonly IReadOnlyList<(string Code, string Name)> Known = new[]
    {
        ("tr", "Türkçe"), ("en", "English"), ("de", "Deutsch"), ("fr", "Français"), ("es", "Español"),
        ("it", "Italiano"), ("pt", "Português"), ("nl", "Nederlands"), ("ru", "Русский"), ("ar", "العربية"),
        ("az", "Azərbaycanca"), ("ku", "Kurdî"), ("pl", "Polski"), ("uk", "Українська"), ("ja", "日本語"),
        ("zh", "中文"), ("ko", "한국어"), ("hi", "हिन्दी"),
    };

    static readonly Dictionary<string, string> EnglishNames = new()
    {
        ["tr"] = "Turkish", ["en"] = "English", ["de"] = "German", ["fr"] = "French", ["es"] = "Spanish",
        ["it"] = "Italian", ["pt"] = "Portuguese", ["nl"] = "Dutch", ["ru"] = "Russian", ["ar"] = "Arabic",
        ["az"] = "Azerbaijani", ["ku"] = "Kurdish", ["pl"] = "Polish", ["uk"] = "Ukrainian", ["ja"] = "Japanese",
        ["zh"] = "Chinese", ["ko"] = "Korean", ["hi"] = "Hindi",
    };

    /// <summary>The language parameter to send: only when exactly one language is chosen.</summary>
    public static string? Pinned(IReadOnlyList<string> codes) => codes.Count == 1 ? codes[0] : null;

    /// <summary>The prompt: a mixing hint (measured to keep English terms English in Turkish speech) plus the vocabulary.</summary>
    public static string BuildPrompt(IReadOnlyList<string> codes, string vocabulary)
    {
        var parts = new List<string>();
        if (codes.Count >= 2)
        {
            var names = codes.Select(c => EnglishNames.GetValueOrDefault(c, c)).ToList();
            var list = names.Count == 2 ? $"{names[0]} and {names[1]}" : string.Join(", ", names[..^1]) + " and " + names[^1];
            if (codes.Contains("tr") && codes.Contains("en"))
                parts.Add("Konuşma Türkçe ve İngilizce karışık; her kelimeyi söylendiği dilde yaz, çevirme.");
            parts.Add($"The speaker mixes {list} mid-sentence: write every word in the language it was spoken, never translate.");
            if (codes.Contains("tr") && codes.Contains("en"))
                parts.Add("Örnek: Şimdi bu branch'i merge et, then run the tests ve sonucu bana söyle.");
        }
        if (!string.IsNullOrWhiteSpace(vocabulary)) parts.Add(vocabulary.Trim());
        return string.Join(" ", parts);
    }
}
