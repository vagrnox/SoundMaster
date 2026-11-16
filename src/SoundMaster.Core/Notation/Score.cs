using SoundMaster.Core.Transcription;

namespace SoundMaster.Core.Notation;

/// <summary>Một âm tiết của lời hát.</summary>
/// <param name="Text">Chữ hiển thị (không có dấu nối).</param>
/// <param name="ContinuesWord">Từ còn tiếp ở nốt sau (ví dụ "ba-" trong "ba-by").</param>
/// <param name="FromPrevious">Âm tiết tiếp nối nốt trước trong cùng một từ.</param>
public sealed record Syllable(string Text, bool ContinuesWord, bool FromPrevious)
{
    /// <summary>Giá trị &lt;syllabic&gt; của MusicXML.</summary>
    public string Syllabic => ContinuesWord ? (FromPrevious ? "middle" : "begin") : (FromPrevious ? "end" : "single");

    /// <summary>Chữ vẽ dưới nốt: có dấu gạch nối khi từ còn tiếp.</summary>
    public string Display => ContinuesWord ? Text + "-" : Text;
}

/// <summary>Cách chia lời: cách nhau bằng dấu cách, "-" tách âm tiết trong một từ, "_" bỏ qua một nốt.</summary>
public static class Lyrics
{
    public static IReadOnlyList<Syllable?> Parse(string? text)
    {
        var result = new List<Syllable?>();
        if (string.IsNullOrWhiteSpace(text)) return result;

        foreach (var word in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (word == "_") { result.Add(null); continue; }

            var parts = word.Split('-', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;
            for (int j = 0; j < parts.Length; j++)
            {
                bool continues = j < parts.Length - 1 || (word.EndsWith('-') && parts.Length > 0);
                result.Add(new Syllable(parts[j], continues, FromPrevious: j > 0));
            }
        }

        return result;
    }
}

/// <summary>Một sự kiện trên khuông: nốt / hợp âm (một hoặc nhiều cao độ) hoặc dấu lặng, trong đúng một ô nhịp.</summary>
public sealed class ScoreEvent
{
    public int Measure { get; init; }

    /// <summary>Vị trí trong ô nhịp, đơn vị 1/16 nốt tròn = 1/4 phách (một ô 4/4 = 16 đơn vị).</summary>
    public int Start16 { get; init; }

    public int Duration16 { get; init; }

    /// <summary>Các cao độ MIDI (tăng dần); rỗng nếu là dấu lặng.</summary>
    public int[] Midis { get; init; } = [];

    public int Staff { get; init; }

    public bool TieToNext { get; set; }
    public bool TieFromPrevious { get; set; }

    /// <summary>Lời gắn dưới nốt này (nếu có).</summary>
    public Syllable? Lyric { get; set; }

    public bool IsRest => Midis.Length == 0;

    public long Absolute16 => Measure * Score.UnitsPerMeasure + Start16;
}

/// <summary>Bản nhạc 4/4 trên khuông lớn (khóa Sol + khóa Fa), dựng từ danh sách nốt.</summary>
public sealed class Score
{
    public const int UnitsPerMeasure = 16;

    public int Measures { get; init; }
    public double Bpm { get; init; }
    public List<ScoreEvent> Treble { get; } = [];
    public List<ScoreEvent> Bass { get; } = [];

    /// <summary>Các "chỗ có lời": mỗi vị trí vào nốt (không tính nốt nối tiếp) theo thứ tự thời gian.</summary>
    public List<List<ScoreEvent>> Slots { get; } = [];

    public IEnumerable<ScoreEvent> All => Treble.Concat(Bass);

    public IReadOnlyList<ScoreEvent> Staff(int index) => index == 0 ? Treble : Bass;

    /// <summary>Thời điểm (giây) của một vị trí 1/4 phách.</summary>
    public double SecondsOf(long absolute16) => absolute16 / 4.0 * 60.0 / Math.Max(1, Bpm);

    /// <summary>Gán lời lần lượt vào các chỗ có lời. Trả về số âm tiết đã gán.</summary>
    public int AssignLyrics(IReadOnlyList<Syllable?> syllables)
    {
        foreach (var e in All) e.Lyric = null;
        int assigned = 0;
        for (int i = 0; i < Slots.Count && i < syllables.Count; i++)
        {
            if (syllables[i] is not { } s) continue;
            Slots[i][0].Lyric = s;
            assigned++;
        }
        return assigned;
    }
}

public static class ScoreBuilder
{
    private static readonly int[] Standard = [16, 12, 8, 6, 4, 3, 2, 1];
    private const int TrebleLowest = 60; // từ C4 trở lên vào khuông Sol

    public static Score Build(IReadOnlyList<TranscribedNote> notes, double bpm)
    {
        bpm = Math.Clamp(bpm, 20, 400);
        var q = notes.Select(n =>
        {
            long s = Math.Max(0, (long)Math.Round(n.StartSeconds * bpm / 60.0 * 4));
            long e = Math.Max(s + 1, (long)Math.Round(n.EndSeconds * bpm / 60.0 * 4));
            return (S: s, E: e, M: n.MidiNote);
        }).ToList();

        long end = q.Count == 0 ? 0 : q.Max(x => x.E);
        int measures = (int)Math.Max(1, (end + Score.UnitsPerMeasure - 1) / Score.UnitsPerMeasure);
        var score = new Score { Measures = measures, Bpm = bpm };

        BuildStaff(score, score.Treble, 0, q.Where(x => x.M >= TrebleLowest).ToList(), measures);
        BuildStaff(score, score.Bass, 1, q.Where(x => x.M < TrebleLowest).ToList(), measures);

        // Chỗ có lời: mỗi vị trí vào nốt, ưu tiên khuông Sol.
        var slots = score.All.Where(e => !e.IsRest && !e.TieFromPrevious)
            .OrderBy(e => e.Absolute16).ThenBy(e => e.Staff)
            .GroupBy(e => e.Absolute16);
        foreach (var g in slots) score.Slots.Add(g.ToList());
        return score;
    }

    private static void BuildStaff(Score score, List<ScoreEvent> staff, int staffIndex,
        List<(long S, long E, int M)> notes, int measures)
    {
        var groups = notes.GroupBy(n => n.S).OrderBy(g => g.Key).ToList();
        long cursor = 0;
        long total = (long)measures * Score.UnitsPerMeasure;

        for (int i = 0; i < groups.Count; i++)
        {
            var g = groups[i];
            if (g.Key > cursor) Emit(staff, staffIndex, cursor, g.Key - cursor, []);

            long next = i + 1 < groups.Count ? groups[i + 1].Key : long.MaxValue;
            long endAt = Math.Min(g.Max(n => n.E), next);
            long dur = Math.Max(1, endAt - g.Key);
            Emit(staff, staffIndex, g.Key, dur, g.Select(n => n.M).Distinct().OrderBy(m => m).ToArray());
            cursor = g.Key + dur;
        }

        if (cursor < total) Emit(staff, staffIndex, cursor, total - cursor, []);
    }

    /// <summary>Chia một đoạn [start, start+dur) thành các nốt / lặng có giá trị chuẩn, không vắt qua vạch nhịp, nối bằng dây legato.</summary>
    private static void Emit(List<ScoreEvent> staff, int staffIndex, long start, long dur, int[] midis)
    {
        bool first = true;
        while (dur > 0)
        {
            int measure = (int)(start / Score.UnitsPerMeasure);
            int offset = (int)(start % Score.UnitsPerMeasure);
            long room = Score.UnitsPerMeasure - offset;
            int take = Standard.First(v => v <= Math.Min(dur, room));

            bool more = dur - take > 0;
            staff.Add(new ScoreEvent
            {
                Measure = measure,
                Start16 = offset,
                Duration16 = take,
                Midis = midis,
                Staff = staffIndex,
                TieFromPrevious = midis.Length > 0 && !first,
                TieToNext = midis.Length > 0 && more,
            });

            start += take;
            dur -= take;
            first = false;
        }
    }

    /// <summary>Tên bước (0=C..6=B), dấu thăng (0/1) của một nốt MIDI; luôn ghi bằng dấu thăng.</summary>
    public static (int Step, int Alter) Spell(int midi)
    {
        int pc = ((midi % 12) + 12) % 12;
        int[] step = [0, 0, 1, 1, 2, 3, 3, 4, 4, 5, 5, 6];
        int[] alter = [0, 1, 0, 1, 0, 0, 1, 0, 1, 0, 1, 0];
        return (step[pc], alter[pc]);
    }

    public static int Octave(int midi) => midi / 12 - 1;

    /// <summary>Số bậc âm (diatonic) tuyệt đối: C0 = 0, mỗi bước là 1.</summary>
    public static int DiatonicNumber(int midi)
    {
        var (step, _) = Spell(midi);
        return Octave(midi) * 7 + step;
    }
}
