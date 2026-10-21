using System.Text.RegularExpressions;

namespace Dikte.Core;

/// <summary>A transcript with its spoken commands taken out.</summary>
public readonly record struct Spoken(string Text, bool PressEnter, bool DeleteLast);

/// <summary>
/// Words that act instead of being written, in English and Turkish:
/// "new line" / "yeni satır" and "new paragraph" / "yeni paragraf" anywhere become line breaks;
/// "send" / "gönder" at the end presses Enter after the text (said as its own sentence, so "can you send"
/// stays text); "delete that" / "sil" said alone removes the last dictation.
/// </summary>
public static partial class VoiceCommands
{
    [GeneratedRegex(@"^\W*(delete that|scratch that|undo that|sil|sil onu|onu sil|geri al)\W*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DeleteLast();

    [GeneratedRegex(@"[ \t]*[,;:]?[ \t]*\b(new paragraph|yeni paragraf)\b[.,;:!]?[ \t]*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Paragraph();

    [GeneratedRegex(@"[ \t]*[,;:]?[ \t]*\b(new line|newline|yeni satır|alt satır)\b[.,;:!]?[ \t]*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Line();

    [GeneratedRegex(@"(?:^|(?<p>[.,!?;:]))\s*\b(send it|send|press enter|gönder|yolla)\s*[.!]?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Send();

    public static Spoken Apply(string text)
    {
        if (DeleteLast().IsMatch(text)) return new Spoken("", false, true);

        bool enter = false;
        var send = Send().Match(text);
        if (send.Success)
        {
            enter = true;
            var p = send.Groups["p"].Value;
            // A full stop stays with the sentence before it; a comma before "send" was only a pause.
            text = text[..send.Index] + (p is "." or "!" or "?" ? p : "");
        }
        text = Paragraph().Replace(text, "\n\n");
        text = Line().Replace(text, "\n");
        return new Spoken(text.Trim(' ', '\t'), enter, false);
    }
}
