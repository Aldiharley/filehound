using System.Globalization;
using System.Reflection;
using System.Text;
using System.Xml;

namespace FileHound.Core.Recovery;

/// <summary>RFC 4180 CSV writers (UTF-8, LF line ends, ISO-8601 UTC timestamps).</summary>
public static class CsvExport
{
    public static void WriteCandidates(TextWriter w, IEnumerable<RecoveryCandidate> items)
    {
        Row(w, "source", "name", "original_folder", "size", "modified_utc", "deleted_utc", "grade", "percent_intact", "is_directory", "detail");
        foreach (var c in items)
            Row(w, c.Source.ToString(), c.Name, c.OriginalFolder ?? "", c.Size.ToString(CultureInfo.InvariantCulture), Iso(c.ModifiedUtc), Iso(c.DeletedUtc),
                c.Grade.ToString(), c.PercentIntact.ToString(CultureInfo.InvariantCulture), Bool(c.IsDirectory), c.Detail ?? "");
    }

    public static void WriteDeletions(TextWriter w, IEnumerable<DeletionEntry> items)
    {
        Row(w, "deleted_utc", "name", "parent_path", "size", "is_directory", "kind", "record_no", "sequence", "usn");
        foreach (var e in items)
            Row(w, Iso(Ticks(e.DeletedUtcTicks)), e.Name, e.ParentPath, e.Size.ToString(CultureInfo.InvariantCulture), Bool(e.IsDirectory), e.Kind.ToString(),
                e.RecordNo.ToString(CultureInfo.InvariantCulture), e.Sequence.ToString(CultureInfo.InvariantCulture), e.Usn.ToString(CultureInfo.InvariantCulture));
    }

    public static void WriteManifest(TextWriter w, IEnumerable<RecoveredFile> items)
    {
        Row(w, "original_path", "recovered_path", "size", "sha256", "grade", "source", "error");
        foreach (var r in items)
            Row(w, r.Candidate.OriginalPath, r.RecoveredPath, r.Bytes.ToString(CultureInfo.InvariantCulture), r.Sha256Hex, r.FinalGrade.ToString(), r.Candidate.Source.ToString(), r.Error ?? "");
    }

    private static DateTime? Ticks(long t) => t <= 0 ? null : new DateTime(t, DateTimeKind.Utc);
    private static string Iso(DateTime? d) => d is null ? "" : d.Value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
    private static string Bool(bool b) => b ? "true" : "false";

    private static void Row(TextWriter w, params string[] fields)
    {
        for (int i = 0; i < fields.Length; i++)
        {
            if (i > 0) w.Write(',');
            w.Write(Quote(fields[i]));
        }
        w.Write('\n');
    }

    private static string Quote(string s)
    {
        if (s.AsSpan().IndexOfAny(",\"\r\n") < 0) return s;
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }
}

/// <summary>Digital Forensics XML (DFXML 1.2) report of a recovery session: one fileobject per recovered file with its SHA-256.</summary>
public static class DfxmlExport
{
    private const string Ns = "http://www.forensicswiki.org/wiki/Category:Digital_Forensics_XML";

    public static void Write(TextWriter w, string volumeDescription, IEnumerable<RecoveredFile> items, DateTime startedUtc, DateTime finishedUtc)
    {
        using var x = XmlWriter.Create(w, new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false), OmitXmlDeclaration = false });
        x.WriteStartDocument();
        x.WriteStartElement("dfxml", Ns);
        x.WriteAttributeString("version", "1.2.0");

        x.WriteStartElement("metadata", Ns);
        x.WriteElementString("type", Ns, "recovery");
        x.WriteEndElement();

        x.WriteStartElement("creator", Ns);
        x.WriteElementString("program", Ns, "FileHound");
        x.WriteElementString("version", Ns, Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "1.0.0");
        x.WriteStartElement("execution_environment", Ns);
        x.WriteElementString("os_sysname", Ns, "Windows");
        x.WriteElementString("start_time", Ns, Iso(startedUtc));
        x.WriteElementString("end_time", Ns, Iso(finishedUtc));
        x.WriteEndElement();
        x.WriteEndElement();

        x.WriteStartElement("source", Ns);
        x.WriteElementString("image_filename", Ns, volumeDescription);
        x.WriteEndElement();

        foreach (var r in items)
        {
            x.WriteStartElement("fileobject", Ns);
            x.WriteElementString("filename", Ns, r.Candidate.OriginalPath);
            x.WriteElementString("filesize", Ns, r.Bytes.ToString(CultureInfo.InvariantCulture));
            x.WriteElementString("name_type", Ns, r.Candidate.IsDirectory ? "d" : "r");
            x.WriteElementString("alloc", Ns, "0");
            if (r.Candidate.ModifiedUtc is { } m) x.WriteElementString("mtime", Ns, Iso(m));
            if (r.Candidate.DeletedUtc is { } d) x.WriteElementString("dtime", Ns, Iso(d));
            x.WriteStartElement("byte_runs", Ns);
            foreach (var run in r.Runs ?? [])
            {
                x.WriteStartElement("byte_run", Ns);
                x.WriteAttributeString("file_offset", run.FileOffset.ToString(CultureInfo.InvariantCulture));
                x.WriteAttributeString("len", run.Length.ToString(CultureInfo.InvariantCulture));
                if (run.ImageOffset >= 0) x.WriteAttributeString("img_offset", run.ImageOffset.ToString(CultureInfo.InvariantCulture));
                x.WriteEndElement();
            }
            x.WriteEndElement();
            if (r.Sha256Hex.Length > 0)
            {
                x.WriteStartElement("hashdigest", Ns);
                x.WriteAttributeString("type", "sha256");
                x.WriteString(r.Sha256Hex);
                x.WriteEndElement();
            }
            x.WriteStartElement("recovery", Ns);
            x.WriteElementString("source", Ns, r.Candidate.Source.ToString());
            x.WriteElementString("grade", Ns, r.FinalGrade.ToString());
            x.WriteElementString("recovered_path", Ns, r.RecoveredPath);
            if (r.Error is not null) x.WriteElementString("error", Ns, r.Error);
            x.WriteEndElement();
            x.WriteEndElement();
        }

        x.WriteEndElement();
        x.WriteEndDocument();
    }

    private static string Iso(DateTime d) => d.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
