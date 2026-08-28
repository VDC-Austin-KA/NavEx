using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using NavEx.Core;

namespace NavEx.FourD
{
    /// <summary>How an <see cref="IdentifierRule"/> compares itself to a name.</summary>
    public enum RuleMatch
    {
        /// <summary>The pattern must equal one of the name's tokens. Safest, and the default.</summary>
        Token = 0,

        /// <summary>The pattern must appear anywhere in the name with separators ignored.</summary>
        Contains = 1,

        /// <summary>The pattern is a .NET regular expression, matched case-insensitively.</summary>
        Regex = 2
    }

    /// <summary>
    /// One user-supplied identifier: "when you see this in a name, it means this".
    ///
    /// The built-in dictionary can only ever cover vocabulary that is common across
    /// projects. Every office has its own — a level called <c>P-DECK</c>, a trade
    /// package called <c>WD-01</c>, a subcontractor's initials standing in for a
    /// scope. A rule is how that local vocabulary gets taught to the classifier
    /// once instead of being corrected set by set, forever.
    ///
    /// Rules are consulted before the built-in aliases and win outright, which is
    /// the point: your name for something beats the shipped guess.
    /// </summary>
    public class IdentifierRule
    {
        public string Pattern = "";
        public RuleMatch Match = RuleMatch.Token;
        public string DisciplineCode = "";
        public string ActivityCode = "";
        public string LevelTag = "";          // optional, e.g. "ROOF" or "L-02"
        public int Priority;                  // 0 = derive from pattern length
        public bool Enabled = true;
        public string Note = "";

        public IdentifierRule() { }

        public IdentifierRule(string pattern, string disciplineCode, string activityCode)
        {
            Pattern = pattern ?? "";
            DisciplineCode = disciplineCode ?? "";
            ActivityCode = activityCode ?? "";
        }

        /// <summary>
        /// Longer patterns win by default, for the same reason the built-in matcher
        /// prefers longer aliases: a specific phrase is better evidence than a
        /// fragment of it. An explicit priority overrides that entirely.
        /// </summary>
        public int EffectivePriority
        {
            get { return Priority > 0 ? Priority : 100 + (Pattern == null ? 0 : Pattern.Length); }
        }

        public bool IsUsable
        {
            get
            {
                return !string.IsNullOrWhiteSpace(Pattern)
                    && (!string.IsNullOrWhiteSpace(DisciplineCode)
                     || !string.IsNullOrWhiteSpace(ActivityCode)
                     || !string.IsNullOrWhiteSpace(LevelTag));
            }
        }

        /// <summary>
        /// True when this rule recognises the name. <paramref name="squashed"/> is
        /// the name reduced to bare alphanumerics and uppercased — the same form
        /// the built-in matcher uses so "Curtain Wall" and "CURTAINWALL" behave
        /// identically.
        /// </summary>
        public bool Matches(IList<string> tokens, string squashed, string raw)
        {
            if (!Enabled || string.IsNullOrWhiteSpace(Pattern)) return false;

            switch (Match)
            {
                case RuleMatch.Regex:
                    try
                    {
                        return Regex.IsMatch(raw ?? "", Pattern,
                            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                    }
                    catch (ArgumentException)
                    {
                        // A malformed expression must not take the whole run down;
                        // the editor flags it separately via Validate().
                        return false;
                    }

                case RuleMatch.Contains:
                    string needle = NameClassifier.Squash(Pattern);
                    return needle.Length > 0 && (squashed ?? "").IndexOf(needle, StringComparison.Ordinal) >= 0;

                default:
                    string token = NameClassifier.Squash(Pattern);
                    if (token.Length == 0 || tokens == null) return false;
                    for (int i = 0; i < tokens.Count; i++)
                        if (string.Equals(tokens[i], token, StringComparison.OrdinalIgnoreCase)) return true;
                    return false;
            }
        }

        /// <summary>Null when the rule is fine; otherwise why it will not be used.</summary>
        public string Validate()
        {
            if (string.IsNullOrWhiteSpace(Pattern)) return "no pattern";
            if (!IsUsable) return "matches nothing to (set a discipline, activity or level)";

            if (Match == RuleMatch.Regex)
            {
                try { Regex.IsMatch("", Pattern); }
                catch (ArgumentException ex) { return "invalid regular expression: " + ex.Message; }
            }

            if (!string.IsNullOrWhiteSpace(LevelTag) &&
                FourDName.LevelIndexFromTag(LevelTag) == 0 &&
                !string.Equals(LevelTag, "L00", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(LevelTag, "BLDG", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(LevelTag, "ALL", StringComparison.OrdinalIgnoreCase))
                return "'" + LevelTag + "' is not a level tag (use L01, L-02, SITE, ROOF)";

            return null;
        }

        public override string ToString()
        {
            var sb = new StringBuilder(Pattern);
            sb.Append(" → ");
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(DisciplineCode)) parts.Add(DisciplineCode);
            if (!string.IsNullOrEmpty(ActivityCode)) parts.Add(ActivityCode);
            if (!string.IsNullOrEmpty(LevelTag)) parts.Add(LevelTag);
            sb.Append(parts.Count == 0 ? "(nothing)" : string.Join(" ", parts.ToArray()));
            return sb.ToString();
        }
    }

    /// <summary>
    /// The project's own vocabulary: extra disciplines, extra activities, and the
    /// rules that map local names onto both.
    ///
    /// Stored as one flat, line-oriented file so it can be diffed, mailed to the
    /// next project, or hand-edited when someone would rather do that than click
    /// thirty rows. As everywhere else in NavEx, a line that cannot be read is
    /// skipped with a note rather than taking the whole file down — a library that
    /// half-loads is far better than a 4D tab that will not open.
    /// </summary>
    public class IdentifierLibrary
    {
        public readonly List<Discipline> Disciplines = new List<Discipline>();
        public readonly List<Activity> Activities = new List<Activity>();
        public readonly List<IdentifierRule> Rules = new List<IdentifierRule>();

        /// <summary>Lines the loader could not understand, surfaced in the UI.</summary>
        public readonly List<string> Warnings = new List<string>();

        public bool IsEmpty
        {
            get { return Disciplines.Count == 0 && Activities.Count == 0 && Rules.Count == 0; }
        }

        public void Clear()
        {
            Disciplines.Clear();
            Activities.Clear();
            Rules.Clear();
            Warnings.Clear();
        }

        // ── Where it lives ───────────────────────────────────────────────────

        /// <summary>
        /// One library per machine, not per document. Local vocabulary belongs to
        /// the office, not to the file that happened to be open when it was typed.
        /// </summary>
        public static string DefaultPath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "NavEx", "4D", "identifiers.txt");
            }
        }

        public static IdentifierLibrary Load() { return Load(DefaultPath); }

        public static IdentifierLibrary Load(string path)
        {
            var library = new IdentifierLibrary();
            try
            {
                if (!File.Exists(path)) return library;
                library.Read(File.ReadAllLines(path));
            }
            catch (Exception ex)
            {
                Log.Debug("Could not load the identifier library: " + ex.Message);
                library.Warnings.Add("Could not read " + path + ": " + ex.Message);
            }
            return library;
        }

        public bool Save() { return Save(DefaultPath); }

        public bool Save(string path)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
                File.WriteAllText(path, Write(), new UTF8Encoding(false));
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("Could not save the identifier library", ex);
                return false;
            }
        }

        // ── Serialisation ────────────────────────────────────────────────────

        public string Write()
        {
            var sb = new StringBuilder();
            sb.AppendLine("# NavEx 4D identifier library.");
            sb.AppendLine("# discipline = CODE | Display name | ALIAS;ALIAS");
            sb.AppendLine("# activity   = CODE | Display name | DISC | lag | order | ALIAS;ALIAS");
            sb.AppendLine("# rule       = pattern | token|contains|regex | DISC | ACT | LEVEL | priority | on/off | note");
            sb.AppendLine();

            foreach (Discipline discipline in Disciplines)
                sb.AppendLine("discipline=" + string.Join("|", new[]
                {
                    Clean(discipline.Code),
                    Clean(discipline.DisplayName),
                    string.Join(";", (discipline.Aliases ?? new string[0]).Select(Clean).ToArray())
                }));

            foreach (Activity activity in Activities)
                sb.AppendLine("activity=" + string.Join("|", new[]
                {
                    Clean(activity.Code),
                    Clean(activity.DisplayName),
                    Clean(activity.DisciplineCode),
                    activity.LagFloors.ToString(CultureInfo.InvariantCulture),
                    activity.CycleOrder.ToString(CultureInfo.InvariantCulture),
                    string.Join(";", (activity.Aliases ?? new string[0]).Select(Clean).ToArray())
                }));

            foreach (IdentifierRule rule in Rules)
                sb.AppendLine("rule=" + string.Join("|", new[]
                {
                    Clean(rule.Pattern),
                    rule.Match.ToString().ToLowerInvariant(),
                    Clean(rule.DisciplineCode),
                    Clean(rule.ActivityCode),
                    Clean(rule.LevelTag),
                    rule.Priority.ToString(CultureInfo.InvariantCulture),
                    rule.Enabled ? "on" : "off",
                    Clean(rule.Note)
                }));

            return sb.ToString();
        }

        public void Read(IEnumerable<string> lines)
        {
            if (lines == null) return;

            foreach (string raw in lines)
            {
                string line = (raw ?? "").Trim();
                if (line.Length == 0 || line[0] == '#') continue;

                int separator = line.IndexOf('=');
                if (separator <= 0) { Warnings.Add("ignored: " + line); continue; }

                string key = line.Substring(0, separator).Trim().ToLowerInvariant();
                string[] fields = line.Substring(separator + 1).Split('|');

                try
                {
                    if (key == "discipline") ReadDiscipline(fields, line);
                    else if (key == "activity") ReadActivity(fields, line);
                    else if (key == "rule") ReadRule(fields, line);
                    else Warnings.Add("unknown entry '" + key + "': " + line);
                }
                catch (Exception ex)
                {
                    Warnings.Add("could not read '" + line + "': " + ex.Message);
                }
            }
        }

        private void ReadDiscipline(string[] fields, string line)
        {
            if (fields.Length < 2) { Warnings.Add("discipline needs a code and a name: " + line); return; }

            string code = Code(fields[0]);
            if (code.Length == 0) { Warnings.Add("discipline has no usable code: " + line); return; }

            Disciplines.Add(new Discipline(code, fields[1].Trim(),
                Split(fields.Length > 2 ? fields[2] : "", code)));
        }

        private void ReadActivity(string[] fields, string line)
        {
            if (fields.Length < 3) { Warnings.Add("activity needs a code, a name and a discipline: " + line); return; }

            string code = Code(fields[0]);
            if (code.Length == 0) { Warnings.Add("activity has no usable code: " + line); return; }

            Activities.Add(new Activity(code, fields[1].Trim(), Code(fields[2]),
                ParseInt(fields.Length > 3 ? fields[3] : "", 0),
                ParseInt(fields.Length > 4 ? fields[4] : "", 50),
                Split(fields.Length > 5 ? fields[5] : "", code)));
        }

        private void ReadRule(string[] fields, string line)
        {
            if (fields.Length < 1 || fields[0].Trim().Length == 0)
            {
                Warnings.Add("rule has no pattern: " + line);
                return;
            }

            var rule = new IdentifierRule
            {
                Pattern = fields[0].Trim(),
                Match = ParseMatch(fields.Length > 1 ? fields[1] : ""),
                DisciplineCode = Code(fields.Length > 2 ? fields[2] : ""),
                ActivityCode = Code(fields.Length > 3 ? fields[3] : ""),
                LevelTag = (fields.Length > 4 ? fields[4] : "").Trim().ToUpperInvariant(),
                Priority = ParseInt(fields.Length > 5 ? fields[5] : "", 0),
                Enabled = !string.Equals((fields.Length > 6 ? fields[6] : "on").Trim(), "off",
                                         StringComparison.OrdinalIgnoreCase),
                Note = (fields.Length > 7 ? fields[7] : "").Trim()
            };

            Rules.Add(rule);
        }

        private static RuleMatch ParseMatch(string value)
        {
            switch ((value ?? "").Trim().ToLowerInvariant())
            {
                case "contains": return RuleMatch.Contains;
                case "regex": return RuleMatch.Regex;
                default: return RuleMatch.Token;
            }
        }

        /// <summary>Pipes and newlines are the field separators, so they cannot survive in a value.</summary>
        private static string Clean(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            return value.Replace('|', '/').Replace(';', ',').Replace('\r', ' ').Replace('\n', ' ').Trim();
        }

        private static string Code(string value)
        {
            return (value ?? "").Trim().ToUpperInvariant();
        }

        /// <summary>
        /// Aliases, always including the code itself so a rule-free custom activity
        /// still answers to its own name.
        /// </summary>
        private static string[] Split(string value, string code)
        {
            var aliases = new List<string>();
            if (!string.IsNullOrEmpty(code)) aliases.Add(code);

            foreach (string part in (value ?? "").Split(';', ','))
            {
                string alias = NameClassifier.Squash(part);
                if (alias.Length > 0 && !aliases.Contains(alias)) aliases.Add(alias);
            }

            return aliases.ToArray();
        }

        private static int ParseInt(string value, int fallback)
        {
            int parsed;
            return int.TryParse((value ?? "").Trim(), NumberStyles.Integer,
                                CultureInfo.InvariantCulture, out parsed) ? parsed : fallback;
        }

        // ── A starter set ────────────────────────────────────────────────────

        /// <summary>
        /// What a new library offers on first open. These are examples to edit,
        /// not defaults NavEx depends on — everything here could be deleted and the
        /// built-in dictionary would still classify the same names.
        /// </summary>
        public static IdentifierLibrary Sample()
        {
            var library = new IdentifierLibrary();

            library.Rules.Add(new IdentifierRule("Misc", "MISC", "MISC")
            { Note = "catch-all sets" });
            library.Rules.Add(new IdentifierRule("Generic Models", "MISC", "MISC")
            { Match = RuleMatch.Contains, Note = "Revit's Generic Models category" });
            library.Rules.Add(new IdentifierRule("Railing", "ARCS", "RAIL")
            { Match = RuleMatch.Contains });
            library.Rules.Add(new IdentifierRule(@"^WD-?\d+", "ARCS", "DRYW")
            { Match = RuleMatch.Regex, Note = "example: a work-package prefix" });

            return library;
        }
    }
}
