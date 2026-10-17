using System.Globalization;
using System.Text.Json;

namespace Dikte.Core;

public sealed class DayStats
{
    public int Words { get; set; }
    public double Seconds { get; set; }
    public double Cost { get; set; }
    public int Count { get; set; }
}

public sealed record StatsSummary(int WeekWords, double WeekMinutes, double MonthCost, int Wpm, int TotalWords, int TotalCount);

/// <summary>What has been dictated, by day, kept on disk: words, speaking time and what it cost.</summary>
public sealed class Stats(string path)
{
    readonly Dictionary<string, DayStats> _days = Load(path);

    /// <summary>US dollars per minute of audio; unknown models (other servers) count as free.</summary>
    public static double PricePerMinute(string model) => model switch
    {
        "gpt-transcribe" => 0.0045,
        "gpt-4o-transcribe" or "whisper-1" or "gpt-4o-transcribe-diarize" => 0.006,
        "gpt-4o-mini-transcribe" => 0.003,
        _ => 0,
    };

    public void Record(DateTime at, int words, double seconds, string model)
    {
        var key = at.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (!_days.TryGetValue(key, out var day)) _days[key] = day = new DayStats();
        day.Words += words;
        day.Seconds += seconds;
        day.Cost += seconds / 60 * PricePerMinute(model);
        day.Count++;
        try { File.WriteAllText(path, JsonSerializer.Serialize(_days, Settings.Json)); }
        catch (IOException) { }
    }

    public StatsSummary Summary(DateTime now)
    {
        var today = now.Date;
        int weekWords = 0, totalWords = 0, totalCount = 0;
        double weekSeconds = 0, monthCost = 0, totalSeconds = 0;
        foreach (var (key, day) in _days)
        {
            if (!DateTime.TryParseExact(key, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) continue;
            totalWords += day.Words;
            totalSeconds += day.Seconds;
            totalCount += day.Count;
            if (date > today.AddDays(-7) && date <= today)
            {
                weekWords += day.Words;
                weekSeconds += day.Seconds;
            }
            if (date.Year == today.Year && date.Month == today.Month) monthCost += day.Cost;
        }
        int wpm = totalSeconds >= 20 ? (int)Math.Round(totalWords / (totalSeconds / 60)) : 0;
        return new StatsSummary(weekWords, weekSeconds / 60, monthCost, wpm, totalWords, totalCount);
    }

    static Dictionary<string, DayStats> Load(string path)
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, DayStats>>(File.ReadAllText(path), Settings.Json) ?? new(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }
}
