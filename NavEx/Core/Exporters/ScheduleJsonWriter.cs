using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using NavEx.FourD;

namespace NavEx.Core.Exporters
{
    /// <summary>
    /// Writes the one sidecar a Datasmith export cannot carry: the schedule.
    ///
    /// A .udatasmith holds geometry, materials and the per-node pixmy.* linkage, but
    /// no tasks and no dates — so Unrealistic4D's importer has, until now, had to be
    /// handed a separate TimeLiner CSV to date anything. This writer emits that
    /// schedule as JSON beside the .udatasmith files, in the shape the Earth4D plugin
    /// already reads (UEarth4DSubsystem::ImportNavisworks4D):
    ///
    ///   scheduleStart              — day 0 anchor, "yyyy-MM-dd"
    ///   tasks[]  id, name, plannedStart, plannedEnd
    ///   elements[] taskId, name
    ///
    /// Those five names are load-bearing; the reader looks them up literally. Every
    /// other field is additive and safely ignored by an older reader.
    ///
    /// Elements carry the join key twice on purpose, because the plugin has two
    /// import paths and they key differently:
    ///   • "name" is the selection set, which is also the name of the StaticMesh
    ///     asset the Datasmith import produces (see DatasmithWriter's element naming)
    ///     — that is what the stem-matching path resolves against.
    ///   • "set" is the same string presented as the pixmy.set.name metadata value,
    ///     which is what the Datasmith-folder path matches on.
    /// One file, both paths.
    /// </summary>
    internal static class ScheduleJsonWriter
    {
        /// <summary>Fixed name so a re-export overwrites rather than accumulating.</summary>
        public const string FileName = "navex_schedule.json";

        private const string ContractVersion = "1.0";

        /// <summary>
        /// Writes <see cref="FileName"/> into <paramref name="folder"/> and returns its
        /// path, or null when the export carried no matched tasks — in which case there
        /// is genuinely nothing to say and an empty tasks array would only be refused
        /// on the Unreal side.
        /// </summary>
        public static string Write(ExportOptions options, string sourceDocument,
                                   IEnumerable<ExportResult> results, string folder)
        {
            string json = Build(options, sourceDocument, results);
            if (json == null) return null;

            string path = Path.Combine(folder, FileName);
            File.WriteAllText(path, json, new UTF8Encoding(false));
            return path;
        }

        /// <summary>Split out from <see cref="Write"/> so the shape can be asserted without touching disk.</summary>
        public static string Build(ExportOptions options, string sourceDocument,
                                   IEnumerable<ExportResult> results)
        {
            var invariant = CultureInfo.InvariantCulture;
            Dictionary<string, ScheduleTask> links = options.DatasmithTaskLinks;
            if (links == null || links.Count == 0) return null;

            // Tasks are emitted once each, in the order the ids were first seen, so a
            // task attached to several sets does not turn into several tasks.
            var taskOrder = new List<ScheduleTask>();
            var seen = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var elements = new JArr();
            DateTime? earliest = null;
            DateTime? latest = null;

            foreach (ExportResult result in results)
            {
                if (result == null || result.Failed) continue;

                string setName = result.SetName ?? "";
                ScheduleTask task;
                if (setName.Length == 0 || !links.TryGetValue(setName, out task) || task == null) continue;
                if (!task.IsValid) continue;

                if (!seen.ContainsKey(task.StableKey))
                {
                    seen[task.StableKey] = true;
                    taskOrder.Add(task);

                    if (!earliest.HasValue || task.PlannedStart.Value < earliest.Value) earliest = task.PlannedStart;
                    if (!latest.HasValue || task.PlannedFinish.Value > latest.Value) latest = task.PlannedFinish;
                }

                var element = new JObj()
                    .Set("taskId", task.StableKey)
                    .Set("name", setName)
                    .Set("set", setName);

                if (!string.IsNullOrEmpty(result.FilePath))
                    element.Set("file", Path.GetFileName(result.FilePath));

                FourDName parsed = FourDName.TryParse(setName);
                if (parsed != null)
                {
                    AddIfPresent(element, "sequenceCode", parsed.SequenceCode);
                    AddIfPresent(element, "zone", parsed.Zone);
                    AddIfPresent(element, "level", parsed.LevelTag);
                    AddIfPresent(element, "discipline", parsed.DisciplineCode);
                    AddIfPresent(element, "activity", parsed.ActivityCode);
                }

                elements.Add(element);
            }

            if (taskOrder.Count == 0) return null;

            var tasks = new JArr();
            foreach (ScheduleTask task in taskOrder)
            {
                var entry = new JObj()
                    .Set("id", task.StableKey)
                    .Set("name", task.Name ?? "")
                    .Set("type", task.NormalizedTaskType())
                    .Set("plannedStart", Iso(task.PlannedStart))
                    .Set("plannedEnd", Iso(task.PlannedFinish))
                    .Set("durationDays", task.ComputedDurationDays);

                AddIfPresent(entry, "sourceId", task.TaskId);
                AddIfPresent(entry, "wbs", task.Wbs);
                AddIfPresent(entry, "description", task.Description);
                AddIfPresent(entry, "actualStart", Iso(task.ActualStart));
                AddIfPresent(entry, "actualEnd", Iso(task.ActualFinish));

                tasks.Add(entry);
            }

            var root = new JObj()
                .Set("contractVersion", ContractVersion)
                .Set("generator", PluginInfo.Name + " " + PluginInfo.Version)
                .Set("sourceDocument", sourceDocument ?? "")
                .Set("exportedUtc", DateTime.UtcNow.ToString("o", invariant))
                // The metadata key the .udatasmith files carry the same value under, so
                // the plugin never has to guess which field to link on.
                .Set("joinKey", "pixmy.set.name")
                .Set("scheduleStart", Day(earliest))
                .Set("scheduleEnd", Day(latest))
                .Set("tasks", tasks)
                .Set("elements", elements);

            var sb = new StringBuilder();
            root.Write(sb);
            return sb.ToString();
        }

        private static void AddIfPresent(JObj target, string name, string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            target.Set(name, value);
        }

        /// <summary>ISO 8601 with no offset — same spelling the pixmy.task.* metadata uses.</summary>
        private static string Iso(DateTime? value)
        {
            return value.HasValue
                ? value.Value.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)
                : "";
        }

        private static string Day(DateTime? value)
        {
            return value.HasValue
                ? value.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : "";
        }
    }
}
