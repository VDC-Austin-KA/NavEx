using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace NavEx.FourD
{
    /// <summary>The fields Nav4DEx can use from a schedule.</summary>
    public enum ScheduleField
    {
        Ignore = 0,
        TaskId,
        Name,
        Description,
        PlannedStart,
        PlannedFinish,
        ActualStart,
        ActualFinish,
        Duration,
        TaskType,
        Wbs
    }

    /// <summary>
    /// One imported activity. Only <see cref="Name"/>, <see cref="PlannedStart"/>
    /// and <see cref="PlannedFinish"/> are required; everything else improves
    /// matching or the TimeLiner result but is optional by design, because most
    /// exports in the wild carry a different subset.
    /// </summary>
    public class ScheduleTask
    {
        public string TaskId = "";
        public string Name = "";
        public string Description = "";
        public DateTime? PlannedStart;
        public DateTime? PlannedFinish;
        public DateTime? ActualStart;
        public DateTime? ActualFinish;
        public double? DurationDays;
        public string TaskType = "";
        public string Wbs = "";
        public int SourceRow;

        /// <summary>Every column as imported, so nothing is lost on a re-export.</summary>
        public readonly Dictionary<string, string> Raw = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public bool IsValid
        {
            get { return !string.IsNullOrWhiteSpace(Name) && PlannedStart.HasValue && PlannedFinish.HasValue; }
        }

        /// <summary>
        /// The identity used to re-associate a task across re-imports. Prefers the
        /// scheduler's own ID; falls back to the name so a schedule without IDs
        /// still updates in place instead of duplicating.
        /// </summary>
        public string StableKey
        {
            get
            {
                return string.IsNullOrWhiteSpace(TaskId)
                    ? "name:" + (Name ?? "").Trim().ToUpperInvariant()
                    : "id:" + TaskId.Trim().ToUpperInvariant();
            }
        }

        public double ComputedDurationDays
        {
            get
            {
                if (DurationDays.HasValue) return DurationDays.Value;
                if (PlannedStart.HasValue && PlannedFinish.HasValue)
                    return Math.Max(0, (PlannedFinish.Value - PlannedStart.Value).TotalDays);
                return 0;
            }
        }

        /// <summary>Name and description together — what the matcher reads.</summary>
        public string SearchText
        {
            get
            {
                if (string.IsNullOrWhiteSpace(Description)) return Name ?? "";
                return (Name ?? "") + " " + Description;
            }
        }

        /// <summary>
        /// Normalises whatever the schedule calls a task type onto the three
        /// TimeLiner understands out of the box.
        /// </summary>
        public string NormalizedTaskType()
        {
            string raw = (TaskType ?? "").Trim();
            if (raw.Length == 0) return "Construct";

            string upper = raw.ToUpperInvariant();
            if (upper.Contains("DEMO") || upper.Contains("REMOV") || upper.Contains("STRIP")) return "Demolish";
            if (upper.Contains("TEMP") || upper.Contains("SHOR") || upper.Contains("HOIST") ||
                upper.Contains("CRANE") || upper.Contains("SCAFF")) return "Temporary";
            if (upper.Contains("CONSTRUCT") || upper.Contains("INSTALL") || upper.Contains("BUILD")) return "Construct";
            return raw;
        }
    }

    /// <summary>One source column and what it was mapped to.</summary>
    public class ScheduleColumn
    {
        public int Index;
        public string Header = "";
        public ScheduleField Field = ScheduleField.Ignore;
        public bool AutoDetected;
        public string DetectionBasis = "";
        public readonly List<string> Samples = new List<string>();

        public override string ToString()
        {
            return Header + " -> " + Field;
        }
    }

    /// <summary>
    /// Header-name aliases per field, plus the value-shape fallbacks used when the
    /// headers are unhelpful (P6 exports in particular are full of internal names
    /// like <c>target_start_date</c>, and some tools export no header row at all).
    /// </summary>
    public static class ColumnAliases
    {
        private static readonly Dictionary<ScheduleField, string[]> Aliases =
            new Dictionary<ScheduleField, string[]>
            {
                { ScheduleField.TaskId, new[]
                    { "taskid", "task id", "id", "activityid", "activity id", "act id", "task_code",
                      "activity_id", "uniqueid", "unique id", "uid", "syncid", "sync id",
                      "synchronizationid", "synchronization id", "guid", "code" } },

                { ScheduleField.Name, new[]
                    { "taskname", "task name", "name", "activityname", "activity name", "task",
                      "activity", "description of work", "task_name", "activity_name", "title" } },

                { ScheduleField.Description, new[]
                    { "description", "notes", "comment", "comments", "detail", "details", "scope",
                      "task description", "activity description", "longdescription", "remarks" } },

                { ScheduleField.PlannedStart, new[]
                    { "start", "planned start", "plannedstart", "early start", "earlystart",
                      "scheduled start", "baseline start", "target start", "target_start_date",
                      "start date", "startdate", "early_start_date", "plan start", "bl start" } },

                { ScheduleField.PlannedFinish, new[]
                    { "finish", "end", "planned finish", "plannedfinish", "planned end", "early finish",
                      "earlyfinish", "scheduled finish", "baseline finish", "target finish",
                      "target_end_date", "finish date", "finishdate", "end date", "enddate",
                      "early_end_date", "plan finish", "bl finish", "completion" } },

                { ScheduleField.ActualStart, new[]
                    { "actual start", "actualstart", "act start", "act_start_date", "actual start date",
                      "as", "actualstartdate" } },

                { ScheduleField.ActualFinish, new[]
                    { "actual finish", "actualfinish", "act finish", "act_end_date", "actual end",
                      "actual finish date", "af", "actualfinishdate" } },

                { ScheduleField.Duration, new[]
                    { "duration", "dur", "original duration", "origduration", "target_drtn_hr_cnt",
                      "planned duration", "days", "duration (days)", "od" } },

                { ScheduleField.TaskType, new[]
                    { "task type", "tasktype", "type", "activity type", "task_type", "activitytype",
                      "simulation task type", "4d type" } },

                { ScheduleField.Wbs, new[]
                    { "wbs", "wbs code", "wbscode", "wbs name", "outline", "outlinenumber",
                      "outline number", "phase", "wbs_id", "parent" } },
            };

        /// <summary>Exact-ish header match; punctuation and case are ignored.</summary>
        public static ScheduleField FromHeader(string header)
        {
            if (string.IsNullOrWhiteSpace(header)) return ScheduleField.Ignore;
            string normalized = Normalize(header);

            foreach (KeyValuePair<ScheduleField, string[]> entry in Aliases)
                foreach (string alias in entry.Value)
                    if (normalized == Normalize(alias)) return entry.Key;

            // Fall back to a contains match, longest alias first so "actual start"
            // is not swallowed by "start".
            ScheduleField best = ScheduleField.Ignore;
            int bestLength = 0;
            foreach (KeyValuePair<ScheduleField, string[]> entry in Aliases)
            {
                foreach (string alias in entry.Value)
                {
                    string normalizedAlias = Normalize(alias);
                    if (normalizedAlias.Length < 4 || normalizedAlias.Length <= bestLength) continue;
                    if (normalized.Contains(normalizedAlias))
                    {
                        best = entry.Key;
                        bestLength = normalizedAlias.Length;
                    }
                }
            }
            return best;
        }

        public static string Normalize(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            var chars = value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray();
            return new string(chars);
        }

        public static string DisplayName(ScheduleField field)
        {
            switch (field)
            {
                case ScheduleField.TaskId: return "Task / Sync ID";
                case ScheduleField.Name: return "Task Name (required)";
                case ScheduleField.Description: return "Description";
                case ScheduleField.PlannedStart: return "Planned Start (required)";
                case ScheduleField.PlannedFinish: return "Planned Finish (required)";
                case ScheduleField.ActualStart: return "Actual Start";
                case ScheduleField.ActualFinish: return "Actual Finish";
                case ScheduleField.Duration: return "Duration";
                case ScheduleField.TaskType: return "Task Type";
                case ScheduleField.Wbs: return "WBS / Phase";
                default: return "(ignore)";
            }
        }

        public static bool IsRequired(ScheduleField field)
        {
            return field == ScheduleField.Name
                || field == ScheduleField.PlannedStart
                || field == ScheduleField.PlannedFinish;
        }

        public static IEnumerable<ScheduleField> AllFields()
        {
            return Enum.GetValues(typeof(ScheduleField)).Cast<ScheduleField>();
        }
    }

    /// <summary>What an import produced, including what it could not work out.</summary>
    public class ScheduleImportResult
    {
        public string SourcePath = "";
        public string FormatName = "";
        public readonly List<ScheduleColumn> Columns = new List<ScheduleColumn>();
        public readonly List<ScheduleTask> Tasks = new List<ScheduleTask>();
        public readonly List<string> Warnings = new List<string>();

        /// <summary>Rows kept verbatim so the mapping dialog can re-parse after edits.</summary>
        public readonly List<string[]> Rows = new List<string[]>();

        public bool HasField(ScheduleField field)
        {
            return Columns.Any(c => c.Field == field);
        }

        public IEnumerable<ScheduleField> MissingRequiredFields()
        {
            foreach (ScheduleField field in ColumnAliases.AllFields())
                if (ColumnAliases.IsRequired(field) && !HasField(field))
                    yield return field;
        }

        /// <summary>True when the user must resolve columns before the import is usable.</summary>
        public bool NeedsMapping { get { return MissingRequiredFields().Any(); } }

        public int ValidTaskCount { get { return Tasks.Count(t => t.IsValid); } }

        public string Summary()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(string.Format(CultureInfo.InvariantCulture,
                "{0}: {1:N0} rows, {2:N0} usable tasks", FormatName, Rows.Count, ValidTaskCount));

            List<ScheduleField> missing = MissingRequiredFields().ToList();
            if (missing.Count > 0)
                sb.Append(" — missing " + string.Join(", ", missing.Select(ColumnAliases.DisplayName).ToArray()));

            return sb.ToString();
        }
    }
}
