using System.Globalization;
using System.Text;
using System.Xml;

namespace SoundMaster.Core.Notation;

/// <summary>Xuất bản nhạc ra MusicXML (partwise, khuông lớn, có lời) để mở bằng MuseScore / Finale / Sibelius.</summary>
public static class MusicXmlExporter
{
    private static readonly string[] Steps = ["C", "D", "E", "F", "G", "A", "B"];

    public static void Export(string path, Score score, string title)
    {
        var settings = new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false) };
        using var writer = XmlWriter.Create(path, settings);
        Write(writer, score, title);
    }

    public static string ToXml(Score score, string title)
    {
        var sb = new StringBuilder();
        using (var writer = XmlWriter.Create(sb, new XmlWriterSettings { Indent = true, OmitXmlDeclaration = false }))
            Write(writer, score, title);
        return sb.ToString();
    }

    private static void Write(XmlWriter w, Score score, string title)
    {
        w.WriteStartDocument();
        w.WriteDocType("score-partwise", "-//Recordare//DTD MusicXML 3.1 Partwise//EN", "http://www.musicxml.org/dtds/partwise.dtd", null);
        w.WriteStartElement("score-partwise");
        w.WriteAttributeString("version", "3.1");

        w.WriteStartElement("work");
        w.WriteElementString("work-title", title);
        w.WriteEndElement();

        w.WriteStartElement("identification");
        w.WriteStartElement("encoding");
        w.WriteElementString("software", "SoundMaster");
        w.WriteEndElement();
        w.WriteEndElement();

        w.WriteStartElement("part-list");
        w.WriteStartElement("score-part");
        w.WriteAttributeString("id", "P1");
        w.WriteElementString("part-name", title);
        w.WriteEndElement();
        w.WriteEndElement();

        w.WriteStartElement("part");
        w.WriteAttributeString("id", "P1");

        for (int m = 0; m < score.Measures; m++)
        {
            w.WriteStartElement("measure");
            w.WriteAttributeString("number", (m + 1).ToString(CultureInfo.InvariantCulture));

            if (m == 0)
            {
                w.WriteStartElement("attributes");
                w.WriteElementString("divisions", "4");
                w.WriteStartElement("key"); w.WriteElementString("fifths", "0"); w.WriteEndElement();
                w.WriteStartElement("time"); w.WriteElementString("beats", "4"); w.WriteElementString("beat-type", "4"); w.WriteEndElement();
                w.WriteElementString("staves", "2");
                Clef(w, 1, "G", 2);
                Clef(w, 2, "F", 4);
                w.WriteEndElement();

                w.WriteStartElement("direction");
                w.WriteAttributeString("placement", "above");
                w.WriteStartElement("sound");
                w.WriteAttributeString("tempo", Math.Round(score.Bpm, 1).ToString(CultureInfo.InvariantCulture));
                w.WriteEndElement();
                w.WriteEndElement();
            }

            WriteStaff(w, score.Treble.Where(e => e.Measure == m), staff: 1);
            w.WriteStartElement("backup");
            w.WriteElementString("duration", Score.UnitsPerMeasure.ToString(CultureInfo.InvariantCulture));
            w.WriteEndElement();
            WriteStaff(w, score.Bass.Where(e => e.Measure == m), staff: 2);

            w.WriteEndElement(); // measure
        }

        w.WriteEndElement(); // part
        w.WriteEndElement(); // score-partwise
        w.WriteEndDocument();
    }

    private static void Clef(XmlWriter w, int number, string sign, int line)
    {
        w.WriteStartElement("clef");
        w.WriteAttributeString("number", number.ToString(CultureInfo.InvariantCulture));
        w.WriteElementString("sign", sign);
        w.WriteElementString("line", line.ToString(CultureInfo.InvariantCulture));
        w.WriteEndElement();
    }

    private static void WriteStaff(XmlWriter w, IEnumerable<ScoreEvent> events, int staff)
    {
        foreach (var e in events.OrderBy(x => x.Start16))
        {
            if (e.IsRest)
            {
                w.WriteStartElement("note");
                w.WriteElementString("rest", null);
                Tail(w, e, staff, tieStart: false, tieStop: false, lyric: false);
                w.WriteEndElement();
                continue;
            }

            for (int i = 0; i < e.Midis.Length; i++)
            {
                int midi = e.Midis[i];
                var (step, alter) = ScoreBuilder.Spell(midi);

                w.WriteStartElement("note");
                if (i > 0) w.WriteElementString("chord", null);
                w.WriteStartElement("pitch");
                w.WriteElementString("step", Steps[step]);
                if (alter != 0) w.WriteElementString("alter", alter.ToString(CultureInfo.InvariantCulture));
                w.WriteElementString("octave", ScoreBuilder.Octave(midi).ToString(CultureInfo.InvariantCulture));
                w.WriteEndElement();
                Tail(w, e, staff, e.TieToNext, e.TieFromPrevious, lyric: i == 0);
                w.WriteEndElement();
            }
        }
    }

    /// <summary>Phần chung của &lt;note&gt;: duration, tie, voice, type, dot, staff, notations, lyric (đúng thứ tự của lược đồ).</summary>
    private static void Tail(XmlWriter w, ScoreEvent e, int staff, bool tieStart, bool tieStop, bool lyric)
    {
        w.WriteElementString("duration", e.Duration16.ToString(CultureInfo.InvariantCulture));
        if (tieStop) { w.WriteStartElement("tie"); w.WriteAttributeString("type", "stop"); w.WriteEndElement(); }
        if (tieStart) { w.WriteStartElement("tie"); w.WriteAttributeString("type", "start"); w.WriteEndElement(); }
        w.WriteElementString("voice", staff.ToString(CultureInfo.InvariantCulture));

        var (type, dotted) = TypeOf(e.Duration16);
        w.WriteElementString("type", type);
        if (dotted) w.WriteElementString("dot", null);
        w.WriteElementString("staff", staff.ToString(CultureInfo.InvariantCulture));

        if (tieStart || tieStop)
        {
            w.WriteStartElement("notations");
            if (tieStop) { w.WriteStartElement("tied"); w.WriteAttributeString("type", "stop"); w.WriteEndElement(); }
            if (tieStart) { w.WriteStartElement("tied"); w.WriteAttributeString("type", "start"); w.WriteEndElement(); }
            w.WriteEndElement();
        }

        if (lyric && e.Lyric is { } s)
        {
            w.WriteStartElement("lyric");
            w.WriteAttributeString("number", "1");
            w.WriteElementString("syllabic", s.Syllabic);
            w.WriteElementString("text", s.Text);
            w.WriteEndElement();
        }
    }

    /// <summary>Tên giá trị nốt MusicXML cho độ dài chuẩn (đơn vị 1/4 phách) và có chấm dôi hay không.</summary>
    public static (string Type, bool Dotted) TypeOf(int duration16) => duration16 switch
    {
        16 => ("whole", false),
        12 => ("half", true),
        8 => ("half", false),
        6 => ("quarter", true),
        4 => ("quarter", false),
        3 => ("eighth", true),
        2 => ("eighth", false),
        _ => ("16th", false),
    };
}
