using System.Text.Json;

namespace Dikte.Core;

public sealed record HistoryEntry(DateTime At, string Text);

/// <summary>The last few transcripts, kept on disk so a paste that landed nowhere is never lost.</summary>
public sealed class History(string path, int size)
{
    List<HistoryEntry> _items = Load(path);

    public IReadOnlyList<HistoryEntry> Items => _items;

    public void Add(string text)
    {
        _items.Insert(0, new HistoryEntry(DateTime.Now, text));
        if (_items.Count > Math.Max(1, size)) _items = _items.Take(Math.Max(1, size)).ToList();
        try { File.WriteAllText(path, JsonSerializer.Serialize(_items, Settings.Json)); }
        catch (IOException) { }
    }

    public void Clear()
    {
        _items.Clear();
        try { File.Delete(path); }
        catch (IOException) { }
    }

    static List<HistoryEntry> Load(string path)
    {
        try { return JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(path), Settings.Json) ?? new(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }
}
