using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Autodesk.Navisworks.Api;
using NavEx.Core;

namespace NavEx.FourD
{
    public class TimelinerSyncResult
    {
        public int Created;
        public int Updated;
        public int Attached;
        public int Skipped;
        public bool UsedFallback;
        public string FallbackPath = "";
        public readonly List<string> Errors = new List<string>();

        public string Summary()
        {
            if (UsedFallback)
                return string.Format(CultureInfo.InvariantCulture,
                    "Wrote {0:N0} task(s) to {1} for TimeLiner import.", Created, Path.GetFileName(FallbackPath));

            return string.Format(CultureInfo.InvariantCulture,
                "TimeLiner: {0:N0} created, {1:N0} updated, {2:N0} attached, {3:N0} skipped.",
                Created, Updated, Attached, Skipped);
        }
    }

    /// <summary>
    /// Writes tasks into the Navisworks TimeLiner.
    ///
    /// The TimeLiner managed API lives in <c>Autodesk.Navisworks.Timeliner.dll</c>,
    /// which ships inside the Navisworks install but is not redistributable and is
    /// not published on NuGet. Referencing it directly would mean NavEx could only
    /// be compiled on a machine with Navisworks installed, and would tie one build
    /// to one release's assembly version.
    ///
    /// So this binds late. The assembly is already loaded in-process whenever
    /// TimeLiner has been touched, and is loadable by name from the install
    /// directory otherwise; every member is looked up defensively and any gap is
    /// reported rather than thrown. If the bridge cannot bind at all, the caller
    /// falls back to <see cref="WriteCsv"/>, which produces a file TimeLiner's own
    /// CSV import reads — slower for the user, but never a dead end.
    /// </summary>
    public class TimelinerBridge
    {
        private const string AssemblyName = "Autodesk.Navisworks.Timeliner";

        private Assembly _assembly;
        private object _timeliner;
        private Type _taskType;

        public string BindingError { get; private set; }

        public bool IsAvailable { get { return _timeliner != null && _taskType != null; } }

        /// <summary>Attempts to bind. Safe to call repeatedly; never throws.</summary>
        public bool TryBind(Document document)
        {
            if (IsAvailable) return true;
            BindingError = null;

            try
            {
                _assembly = FindAssembly();
                if (_assembly == null)
                {
                    BindingError = "Could not load " + AssemblyName + ".dll.";
                    return false;
                }

                _taskType = _assembly.GetTypes().FirstOrDefault(t => t.Name == "TimelinerTask");
                if (_taskType == null)
                {
                    BindingError = "TimelinerTask type not found in " + AssemblyName + ".";
                    return false;
                }

                _timeliner = GetTimeliner(document);
                if (_timeliner == null)
                {
                    BindingError = "Could not obtain the document's TimeLiner.";
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                BindingError = ex.Message;
                _timeliner = null;
                return false;
            }
        }

        private static Assembly FindAssembly()
        {
            Assembly loaded = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => string.Equals(a.GetName().Name, AssemblyName, StringComparison.OrdinalIgnoreCase));
            if (loaded != null) return loaded;

            try { return Assembly.Load(AssemblyName); }
            catch (Exception) { }

            // Last resort: next to the Navisworks executable that is hosting us.
            try
            {
                string directory = Path.GetDirectoryName(
                    Assembly.GetAssembly(typeof(Document)).Location);
                if (!string.IsNullOrEmpty(directory))
                {
                    string candidate = Path.Combine(directory, AssemblyName + ".dll");
                    if (File.Exists(candidate)) return Assembly.LoadFrom(candidate);
                }
            }
            catch (Exception) { }

            return null;
        }

        /// <summary>
        /// The documented routes are the <c>TimelinerDocumentExtensions.GetTimeliner</c>
        /// extension method and casting <c>Document.Timeliner</c> to
        /// <c>DocumentTimeliner</c>. Both are tried, since which one exists has
        /// varied across releases.
        /// </summary>
        private object GetTimeliner(Document document)
        {
            Type extensions = _assembly.GetTypes()
                .FirstOrDefault(t => t.Name == "TimelinerDocumentExtensions");
            if (extensions != null)
            {
                MethodInfo getter = extensions.GetMethod("GetTimeliner",
                    BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Document) }, null);
                if (getter != null)
                {
                    object result = getter.Invoke(null, new object[] { document });
                    if (result != null) return result;
                }
            }

            return document.Timeliner;
        }

        /// <summary>
        /// Pushes matched tasks into TimeLiner. Existing tasks with the same
        /// display name are updated in place rather than duplicated, so a refreshed
        /// schedule re-import does not multiply the task tree.
        /// </summary>
        public TimelinerSyncResult Sync(Document document, IList<TaskMatch> matches,
                                        bool attachSelections, ProgressContext progress)
        {
            var result = new TimelinerSyncResult();

            if (!TryBind(document))
            {
                result.Errors.Add(BindingError ?? "TimeLiner is unavailable.");
                return result;
            }

            object root = GetProperty(_timeliner, "TasksRoot");
            if (root == null)
            {
                result.Errors.Add("TimeLiner has no task root.");
                return result;
            }

            Dictionary<string, object> existing = IndexExistingTasks(root);

            int index = 0;
            foreach (TaskMatch match in matches)
            {
                if (progress != null)
                {
                    progress.ThrowIfCancelled();
                    progress.Update("TimeLiner: " + match.Task.Name,
                        matches.Count == 0 ? -1 : (double)index / matches.Count);
                    progress.Tick();
                }
                index++;

                if (!match.Task.IsValid) { result.Skipped++; continue; }
                if (match.State == MatchState.Excluded) { result.Skipped++; continue; }

                try
                {
                    object existingTask;
                    bool update = existing.TryGetValue(match.Task.Name.Trim(), out existingTask);

                    object task = update ? existingTask : Activator.CreateInstance(_taskType);

                    SetProperty(task, "DisplayName", match.Task.Name);
                    SetProperty(task, "PlannedStartDate", match.Task.PlannedStart.Value);
                    SetProperty(task, "PlannedEndDate", match.Task.PlannedFinish.Value);

                    if (match.Task.ActualStart.HasValue)
                        SetProperty(task, "ActualStartDate", match.Task.ActualStart.Value);
                    if (match.Task.ActualFinish.HasValue)
                        SetProperty(task, "ActualEndDate", match.Task.ActualFinish.Value);

                    SetProperty(task, "SimulationTaskTypeName", match.Task.NormalizedTaskType());

                    if (!string.IsNullOrWhiteSpace(match.Task.TaskId))
                        SetProperty(task, "SynchronizationId", match.Task.TaskId);

                    if (attachSelections && match.IsAttached && match.Target != null)
                        if (AttachSelection(document, task, match.Target)) result.Attached++;

                    if (update) result.Updated++;
                    else
                    {
                        AddTask(root, task);
                        result.Created++;
                    }
                }
                catch (Exception ex)
                {
                    result.Errors.Add(match.Task.Name + ": " + ex.Message);
                    result.Skipped++;
                }
            }

            return result;
        }

        private Dictionary<string, object> IndexExistingTasks(object root)
        {
            var map = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var children = GetProperty(root, "Children") as System.Collections.IEnumerable;
                if (children == null) return map;

                foreach (object child in children)
                {
                    var name = GetProperty(child, "DisplayName") as string;
                    if (!string.IsNullOrEmpty(name) && !map.ContainsKey(name)) map[name] = child;
                }
            }
            catch (Exception) { }
            return map;
        }

        private void AddTask(object root, object task)
        {
            // Releases differ on whether the task tree is mutated through the
            // timeliner or the root, and on whether the call copies or takes
            // ownership. Try the known shapes in order of preference.
            MethodInfo addCopy = _timeliner.GetType().GetMethod("AddTaskCopy",
                new[] { root.GetType(), _taskType });
            if (addCopy != null) { addCopy.Invoke(_timeliner, new[] { root, task }); return; }

            MethodInfo add = _timeliner.GetType().GetMethod("AddTask", new[] { root.GetType(), _taskType });
            if (add != null) { add.Invoke(_timeliner, new[] { root, task }); return; }

            var children = GetProperty(root, "Children");
            if (children != null)
            {
                MethodInfo childAdd = children.GetType().GetMethod("Add", new[] { _taskType });
                if (childAdd != null) { childAdd.Invoke(children, new[] { task }); return; }
            }

            throw new InvalidOperationException("No supported way to add a task to this TimeLiner version.");
        }

        /// <summary>
        /// TimeLiner attaches a selection, not a set, so the search set has to be
        /// converted: SelectionSet -> SelectionSource -> SelectionSourceCollection,
        /// which is then assigned to the task's Selection.
        /// </summary>
        private bool AttachSelection(Document document, object task, MatchTarget target)
        {
            try
            {
                var savedItem = target.Tag as SavedItem;
                if (savedItem == null) return false;

                SelectionSource source = document.SelectionSets.CreateSelectionSource(savedItem);
                if (source == null) return false;

                object selection = GetProperty(task, "Selection");
                if (selection == null) return false;

                object sources = GetProperty(selection, "SelectionSources");
                if (sources == null) return false;

                MethodInfo clear = sources.GetType().GetMethod("Clear", Type.EmptyTypes);
                if (clear != null) clear.Invoke(sources, null);

                MethodInfo add = sources.GetType().GetMethod("Add", new[] { typeof(SelectionSource) });
                if (add == null) return false;

                add.Invoke(sources, new object[] { source });
                return true;
            }
            catch (Exception ex)
            {
                Log.Debug("Could not attach '" + target.SetName + "': " + ex.Message);
                return false;
            }
        }

        private static object GetProperty(object instance, string name)
        {
            if (instance == null) return null;
            PropertyInfo property = instance.GetType().GetProperty(name,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
            return property == null ? null : property.GetValue(instance, null);
        }

        private static void SetProperty(object instance, string name, object value)
        {
            if (instance == null) return;
            PropertyInfo property = instance.GetType().GetProperty(name,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
            if (property == null || !property.CanWrite) return;
            property.SetValue(instance, value, null);
        }

        // ── CSV fallback ─────────────────────────────────────────────────────

        /// <summary>
        /// Writes the column set TimeLiner's own CSV data-source import expects.
        /// Used when the managed bridge cannot bind, and useful on its own as an
        /// auditable record of what the sync would have done.
        /// </summary>
        public static string WriteCsv(IList<TaskMatch> matches, string path)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Task Name,Task Type,Planned Start,Planned End,Actual Start,Actual End,Synchronization ID,Attached Set,Match Confidence");

            foreach (TaskMatch match in matches)
            {
                ScheduleTask task = match.Task;
                if (!task.IsValid) continue;

                sb.AppendLine(string.Join(",", new[]
                {
                    Quote(task.Name),
                    Quote(task.NormalizedTaskType()),
                    Quote(Format(task.PlannedStart)),
                    Quote(Format(task.PlannedFinish)),
                    Quote(Format(task.ActualStart)),
                    Quote(Format(task.ActualFinish)),
                    Quote(task.TaskId),
                    Quote(match.IsAttached && match.Target != null ? match.Target.SetName : ""),
                    Quote(match.ConfidenceBand)
                }));
            }

            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            return path;
        }

        private static string Format(DateTime? value)
        {
            return value.HasValue ? value.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "";
        }

        private static string Quote(string value)
        {
            if (value == null) value = "";
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
    }
}
