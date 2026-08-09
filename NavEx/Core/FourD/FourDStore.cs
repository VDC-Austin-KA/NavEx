using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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
}
