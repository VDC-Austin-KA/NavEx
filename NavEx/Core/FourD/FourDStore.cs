using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using NavEx.Core;

namespace NavEx.FourD
{
    /// <summary>
    /// Persists the decisions a user makes on the 4D tab, per document.
    ///
    /// This is what makes "re-import the updated schedule" a one-click operation
    /// rather than a repeat of the whole matching exercise: manual matches,
    /// exclusions, learned discipline/activity tokens and any edited trade lags all
    /// survive, so a refreshed export only changes dates.
    ///
    /// Same flat key=value format as <see cref="SettingsStore"/>, for the same
    /// reason: a half-written or hand-edited file degrades to defaults instead of
    /// throwing.
    /// </summary>
    public class FourDState
    {
        public readonly Dictionary<string, string> MatchOverrides =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, string> DisciplineOverrides =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, string> ActivityOverrides =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, int> LagOverrides =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, int> OrderOverrides =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        public string LastSchedulePath = "";

        private static string Folder
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "NavEx", "4D");
            }
        }

        /// <summary>One state file per document, named from its title.</summary>
        public static string PathFor(string documentTitle)
        {
            string safe = ExportOptions.SanitizeFileName(
                string.IsNullOrWhiteSpace(documentTitle) ? "untitled" : documentTitle);
            return Path.Combine(Folder, safe + ".txt");
        }

        public static FourDState Load(string documentTitle)
        {
            var state = new FourDState();
            string path = PathFor(documentTitle);

            try
            {
                if (!File.Exists(path)) return state;

                foreach (string line in File.ReadAllLines(path))
                {
                    int separator = line.IndexOf('=');
                    if (separator <= 0) continue;

                    string key = line.Substring(0, separator).Trim();
                    string value = line.Substring(separator + 1).Trim();

                    if (key.StartsWith("match:", StringComparison.OrdinalIgnoreCase))
                        state.MatchOverrides[key.Substring(6)] = value;
                    else if (key.StartsWith("disc:", StringComparison.OrdinalIgnoreCase))
                        state.DisciplineOverrides[key.Substring(5)] = value;
                    else if (key.StartsWith("act:", StringComparison.OrdinalIgnoreCase))
                        state.ActivityOverrides[key.Substring(4)] = value;
                    else if (key.StartsWith("lag:", StringComparison.OrdinalIgnoreCase))
                        state.LagOverrides[key.Substring(4)] = ParseInt(value);
                    else if (key.StartsWith("order:", StringComparison.OrdinalIgnoreCase))
                        state.OrderOverrides[key.Substring(6)] = ParseInt(value);
                    else if (string.Equals(key, "schedule", StringComparison.OrdinalIgnoreCase))
                        state.LastSchedulePath = value;
                }
            }
            catch (Exception ex)
            {
                Log.Debug("Could not load 4D state: " + ex.Message);
                return new FourDState();
            }

            return state;
        }

        public void Save(string documentTitle)
        {
            string path = PathFor(documentTitle);

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path) ?? Folder);

                var sb = new StringBuilder();
                sb.AppendLine("# NavEx 4D state — manual matches and learned tokens.");
                sb.AppendLine("schedule=" + LastSchedulePath);

                foreach (KeyValuePair<string, string> entry in MatchOverrides)
                    sb.AppendLine("match:" + entry.Key + "=" + entry.Value);
                foreach (KeyValuePair<string, string> entry in DisciplineOverrides)
                    sb.AppendLine("disc:" + entry.Key + "=" + entry.Value);
                foreach (KeyValuePair<string, string> entry in ActivityOverrides)
                    sb.AppendLine("act:" + entry.Key + "=" + entry.Value);
                foreach (KeyValuePair<string, int> entry in LagOverrides)
                    sb.AppendLine("lag:" + entry.Key + "=" + entry.Value.ToString(CultureInfo.InvariantCulture));
                foreach (KeyValuePair<string, int> entry in OrderOverrides)
                    sb.AppendLine("order:" + entry.Key + "=" + entry.Value.ToString(CultureInfo.InvariantCulture));

                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Log.Debug("Could not save 4D state: " + ex.Message);
            }
        }

        public void ApplyTo(NameClassifier classifier, SequenceProfile profile)
        {
            if (classifier != null)
            {
                foreach (KeyValuePair<string, string> entry in DisciplineOverrides)
                    classifier.DisciplineOverrides[entry.Key] = entry.Value;
                foreach (KeyValuePair<string, string> entry in ActivityOverrides)
                    classifier.ActivityOverrides[entry.Key] = entry.Value;
            }

            if (profile != null)
            {
                foreach (KeyValuePair<string, int> entry in LagOverrides)
                    profile.LagOverrides[entry.Key] = entry.Value;
                foreach (KeyValuePair<string, int> entry in OrderOverrides)
                    profile.OrderOverrides[entry.Key] = entry.Value;
            }
        }

        private static int ParseInt(string value)
        {
            int parsed;
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) ? parsed : 0;
        }
    }

    /// <summary>Editable row backing the sequencing table on the 4D tab.</summary>
    public class ActivityRow
    {
        public string Code { get; set; }
        public string DisplayName { get; set; }
        public string DisciplineCode { get; set; }
        public int Lag { get; set; }
        public int Order { get; set; }

        public ActivityRow() { }

        public ActivityRow(Activity activity)
        {
            Code = activity.Code;
            DisplayName = activity.DisplayName;
            DisciplineCode = activity.DisciplineCode;
            Lag = activity.LagFloors;
            Order = activity.CycleOrder;
        }
    }

    /// <summary>
    /// Editable row backing the identifier table.
    ///
    /// A separate type from <see cref="IdentifierRule"/> because WPF binds to
    /// properties and the rule is plain fields, and because a half-typed row —
    /// a pattern with no target yet — has to be able to sit in the grid without
    /// being a rule the classifier would try to use.
    /// </summary>
    public class RuleRow
    {
        public string Pattern { get; set; }
        public string Mode { get; set; }
        public string Discipline { get; set; }
        public string Activity { get; set; }
        public string Level { get; set; }
        public bool Enabled { get; set; }
        public string Note { get; set; }

        public static readonly string[] Modes = { "token", "contains", "regex" };

        public RuleRow()
        {
            Pattern = "";
            Mode = Modes[0];
            Discipline = "";
            Activity = "";
            Level = "";
            Enabled = true;
            Note = "";
        }

        public RuleRow(IdentifierRule rule) : this()
        {
            if (rule == null) return;
            Pattern = rule.Pattern ?? "";
            Mode = rule.Match.ToString().ToLowerInvariant();
            Discipline = rule.DisciplineCode ?? "";
            Activity = rule.ActivityCode ?? "";
            Level = rule.LevelTag ?? "";
            Enabled = rule.Enabled;
            Note = rule.Note ?? "";
        }

        public IdentifierRule ToRule()
        {
            RuleMatch match;
            switch ((Mode ?? "").Trim().ToLowerInvariant())
            {
                case "contains": match = RuleMatch.Contains; break;
                case "regex": match = RuleMatch.Regex; break;
                default: match = RuleMatch.Token; break;
            }

            return new IdentifierRule
            {
                Pattern = (Pattern ?? "").Trim(),
                Match = match,
                DisciplineCode = (Discipline ?? "").Trim().ToUpperInvariant(),
                ActivityCode = (Activity ?? "").Trim().ToUpperInvariant(),
                LevelTag = (Level ?? "").Trim().ToUpperInvariant(),
                Enabled = Enabled,
                Note = (Note ?? "").Trim()
            };
        }
    }

    /// <summary>Editable row backing the project's own activity definitions.</summary>
    public class CustomActivityRow
    {
        public string Code { get; set; }
        public string DisplayName { get; set; }
        public string DisciplineCode { get; set; }
        public int Lag { get; set; }
        public int Order { get; set; }
        public string Aliases { get; set; }

        public CustomActivityRow()
        {
            Code = "";
            DisplayName = "";
            DisciplineCode = "ARCS";
            Lag = 6;
            Order = 50;
            Aliases = "";
        }

        public CustomActivityRow(Activity activity) : this()
        {
            if (activity == null) return;
            Code = activity.Code;
            DisplayName = activity.DisplayName;
            DisciplineCode = activity.DisciplineCode;
            Lag = activity.LagFloors;
            Order = activity.CycleOrder;

            // The code is always the first alias; showing it back would invite
            // someone to delete it.
            var extra = new List<string>();
            foreach (string alias in activity.Aliases ?? new string[0])
                if (!string.Equals(alias, activity.Code, StringComparison.OrdinalIgnoreCase)) extra.Add(alias);
            Aliases = string.Join("; ", extra.ToArray());
        }

        public Activity ToActivity()
        {
            var aliases = new List<string> { (Code ?? "").Trim().ToUpperInvariant() };
            foreach (string part in (Aliases ?? "").Split(';', ','))
            {
                string alias = NameClassifier.Squash(part);
                if (alias.Length > 0 && !aliases.Contains(alias)) aliases.Add(alias);
            }

            return new Activity((Code ?? "").Trim().ToUpperInvariant(),
                                (DisplayName ?? "").Trim(),
                                (DisciplineCode ?? "").Trim().ToUpperInvariant(),
                                Lag, Order, aliases.ToArray());
        }

        /// <summary>Null when the row is usable; otherwise why it will be dropped.</summary>
        public string Validate()
        {
            string code = (Code ?? "").Trim();
            if (code.Length == 0) return "no code";

            // The rendered name is fixed-width by design: four letters, or the
            // whole scheme stops sorting.
            if (code.Length != 4 || !code.All(char.IsLetter))
                return "'" + code + "' must be exactly four letters";
            if (string.IsNullOrWhiteSpace(DisciplineCode)) return "no discipline";
            return null;
        }
    }
}
