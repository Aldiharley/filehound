using System.Collections.Frozen;
using System.Text;
using FileHound.Core.Index;

namespace FileHound.Core.Query;

/// <summary>
/// Parses Everything-style queries. Space = AND, <c>|</c> = OR (binds tighter than AND), leading <c>!</c> = NOT,
/// double quotes keep spaces and special characters literal. See the design spec FR-9 for supported filters.
/// </summary>
public static class QueryParser
{
    private sealed class RawToken
    {
        public readonly List<(string Text, bool QuotedStart)> Alts = [];
        public bool Negated;
        public bool StartsWithPipe;
        public bool EndsWithPipe;
    }

    public static SearchQuery Parse(string text, IClock? clock = null)
    {
        clock ??= SystemClock.Instance;
        var errors = new List<string>();
        var clauses = new List<Clause>();
        foreach (var token in MergePipes(Tokenize(text ?? string.Empty)))
        {
            var alts = new List<Alternative>();
            foreach (var (altText, quoted) in token.Alts)
            {
                var alt = ParseAlternative(altText, quoted, clock, errors);
                if (alt is not null) alts.Add(alt);
            }
            if (alts.Count > 0) clauses.Add(new Clause(alts, token.Negated));
        }
        return new SearchQuery(clauses, errors);
    }

    private static List<RawToken> Tokenize(string text)
    {
        var tokens = new List<RawToken>();
        var cur = new RawToken();
        var sb = new StringBuilder();
        bool inQuotes = false, quotedStart = false, tokenHasContent = false;

        void FinishAlt()
        {
            if (sb.Length > 0) cur.Alts.Add((sb.ToString(), quotedStart));
            sb.Clear();
            quotedStart = false;
        }
        void FinishToken()
        {
            FinishAlt();
            if (cur.Alts.Count > 0 || cur.StartsWithPipe || cur.EndsWithPipe) tokens.Add(cur);
            cur = new RawToken();
            tokenHasContent = false;
        }

        foreach (char c in text)
        {
            if (c == '"')
            {
                if (sb.Length == 0) quotedStart = true;
                inQuotes = !inQuotes;
                tokenHasContent = true;
                cur.EndsWithPipe = false;
                continue;
            }
            if (!inQuotes && char.IsWhiteSpace(c)) { FinishToken(); continue; }
            if (!inQuotes && c == '|')
            {
                if (!tokenHasContent) cur.StartsWithPipe = true;
                FinishAlt();
                cur.EndsWithPipe = true;
                tokenHasContent = true;
                continue;
            }
            if (!inQuotes && c == '!' && !tokenHasContent) { cur.Negated = true; tokenHasContent = true; continue; }
            sb.Append(c);
            tokenHasContent = true;
            cur.EndsWithPipe = false;
        }
        FinishToken();
        return tokens;
    }

    /// <summary>Joins tokens around standalone or edge pipes: "b | c", "b| c", "b |c" all become one OR clause.</summary>
    private static List<RawToken> MergePipes(List<RawToken> tokens)
    {
        var result = new List<RawToken>();
        foreach (var t in tokens)
        {
            if (result.Count > 0 && (t.StartsWithPipe || result[^1].EndsWithPipe))
            {
                var prev = result[^1];
                prev.Alts.AddRange(t.Alts);
                prev.EndsWithPipe = t.EndsWithPipe;
                continue;
            }
            result.Add(t);
        }
        result.RemoveAll(t => t.Alts.Count == 0);
        return result;
    }

    private static Alternative? ParseAlternative(string text, bool quotedStart, IClock clock, List<string> errors)
    {
        if (!quotedStart)
        {
            int colon = text.IndexOf(':');
            if (colon > 0)
            {
                string prefix = text[..colon].ToLowerInvariant();
                string value = text[(colon + 1)..];
                switch (prefix)
                {
                    case "ext":
                        var exts = value.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                            .Select(e => e.TrimStart('.').ToLowerInvariant()).Where(e => e.Length > 0).ToArray();
                        if (exts.Length == 0) { errors.Add($"ext: needs an extension, e.g. ext:pdf"); return null; }
                        return new Alternative(new ExtFilter(exts.ToFrozenSet(StringComparer.Ordinal)));

                    case "type" or "kind":
                        var cats = new List<FileCategory>();
                        bool? kindOnly = null;
                        foreach (var part in value.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        {
                            if (part.Equals("file", StringComparison.OrdinalIgnoreCase) || part.Equals("files", StringComparison.OrdinalIgnoreCase)) kindOnly = false;
                            else if (Categorizer.TryParseCategory(part, out var cat)) cats.Add(cat);
                            else { errors.Add($"Unknown type '{part}'"); }
                        }
                        if (cats.Count > 0) return new Alternative(new CategoryFilter(cats.Distinct().ToArray()));
                        if (kindOnly is not null) return new Alternative(new KindFilter(false));
                        if (value.Length == 0) errors.Add("type: needs a value, e.g. type:image");
                        return null;

                    case "file" or "folder":
                        bool folders = prefix == "folder";
                        if (value.Length == 0) return new Alternative(new KindFilter(folders));
                        return new Alternative(new Term(value, folders ? KindConstraint.Folder : KindConstraint.File));

                    case "size":
                        if (SizeParser.TryParse(value, out long min, out long max)) return new Alternative(new SizeFilter(min, max));
                        errors.Add($"Couldn't read size '{value}' (try size:>10mb)");
                        return null;

                    case "dm" or "datemodified" or "modified":
                        if (DateRangeParser.TryParse(value, clock, out long dmin, out long dmax)) return new Alternative(new DateFilter(dmin, dmax));
                        errors.Add($"Couldn't read date '{value}' (try dm:today or dm:2026-05)");
                        return null;

                    case "path" or "parent" or "infolder":
                        if (value.Length == 0) { errors.Add("path: needs a value"); return null; }
                        return new Alternative(new PathFilter(new Term(value)));

                    case "drive":
                        var letter = value.TrimEnd(':', '\\');
                        if (letter.Length == 1 && char.IsAsciiLetter(letter[0])) return new Alternative(new DriveFilter(char.ToUpperInvariant(letter[0])));
                        errors.Add($"drive: needs a letter, e.g. drive:E");
                        return null;
                }
            }
        }
        return text.Length == 0 ? null : new Alternative(new Term(text));
    }
}
