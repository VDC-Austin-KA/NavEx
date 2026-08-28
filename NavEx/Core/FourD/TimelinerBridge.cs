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
        public int Removed;
        public bool UsedFallback;
        public string FallbackPath = "";
        public readonly List<string> Errors = new List<string>();

        public string Summary()
        {
            if (UsedFallback)
                return string.Format(CultureInfo.InvariantCulture,
                    "Wrote {0:N0} task(s) to {1} for TimeLiner import.", Created, Path.GetFileName(FallbackPath));

            return string.Format(CultureInfo.InvariantCulture,
                "TimeLiner: {0:N0} created, {1:N0} updated, {2:N0} attached, {3:N0} skipped{4}.",
                Created, Updated, Attached, Skipped,
                Removed > 0
                    ? string.Format(CultureInfo.InvariantCulture, ", {0:N0} replaced", Removed)
                    : "");
        }
    }

    /// <summary>What reading the document's existing TimeLiner produced.</summary>
    public class TimelinerReadResult
    {
        public readonly List<ScheduleTask> Tasks = new List<ScheduleTask>();

        /// <summary>Task stable key -> the search set TimeLiner already has attached.</summary>
        public readonly Dictionary<string, string> Attachments =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public readonly List<string> Warnings = new List<string>();

        /// <summary>Tasks read but unusable — a summary row with no dates, typically.</summary>
        public int Incomplete;

        public string Summary()
        {
            return string.Format(CultureInfo.InvariantCulture,
                "TimeLiner: {0:N0} task(s) read, {1:N0} with dates, {2:N0} already attached to a set.",
                Tasks.Count, Tasks.Count - Incomplete, Attachments.Count);
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

        // ── Reading what is already there ────────────────────────────────────

        /// <summary>
        /// Reads the document's existing TimeLiner into schedule tasks.
        ///
        /// This is the way in for the common case, which the file importers never
        /// covered: the schedule is already in the model. Somebody linked P6 to
        /// TimeLiner months ago, or built the tasks by hand, and wanting to work on
        /// them should not require going back to the scheduler for a fresh export
        /// that will not round-trip anyway.
        ///
        /// Each task keeps a handle on the TimeLiner object it came from, so edits
        /// made here can be written back onto the same task rather than appearing
        /// beside it.
        /// </summary>
        public TimelinerReadResult ReadTasks(Document document)
        {
            var result = new TimelinerReadResult();

            if (!TryBind(document))
            {
                result.Warnings.Add(BindingError ?? "TimeLiner is unavailable.");
                return result;
            }

            object root = GetProperty(_timeliner, "TasksRoot");
            if (root == null)
            {
                result.Warnings.Add("TimeLiner has no task root.");
                return result;
            }

            try
            {
                ReadInto(document, root, "", result, 0);
            }
            catch (Exception ex)
            {
                result.Warnings.Add("Could not read the whole task tree: " + ex.Message);
            }

            return result;
        }

        /// <summary>
        /// Walks the task tree. Summary rows — the WBS parents, which carry no
        /// dates of their own — become the WBS path of the tasks beneath them
        /// rather than tasks in their own right.
        /// </summary>
        private void ReadInto(Document document, object parent, string wbs,
                              TimelinerReadResult result, int depth)
        {
            if (depth > 32) return;   // a malformed tree must not recurse forever

            var children = GetProperty(parent, "Children") as System.Collections.IEnumerable;
            if (children == null) return;

            foreach (object child in children)
            {
                string name = (GetProperty(child, "DisplayName") as string ?? "").Trim();

                var task = new ScheduleTask
                {
                    Name = name,
                    Wbs = wbs,
                    SourceHandle = child,
                    PlannedStart = AsDate(GetProperty(child, "PlannedStartDate")),
                    PlannedFinish = AsDate(GetProperty(child, "PlannedEndDate")),
                    ActualStart = AsDate(GetProperty(child, "ActualStartDate")),
                    ActualFinish = AsDate(GetProperty(child, "ActualEndDate")),
                    TaskType = GetProperty(child, "SimulationTaskTypeName") as string ?? "",
                    TaskId = FirstString(child, "SynchronizationId", "SynchronizationID", "DisplayId", "DisplayID")
                };

                if (!string.IsNullOrEmpty(name))
                {
                    if (!task.IsValid) result.Incomplete++;
                    result.Tasks.Add(task);

                    string attached = AttachedSetName(document, child);
                    if (!string.IsNullOrEmpty(attached)) result.Attachments[task.StableKey] = attached;
                }

                ReadInto(document, child,
                    string.IsNullOrEmpty(wbs) ? name : wbs + " / " + name,
                    result, depth + 1);
            }
        }

        /// <summary>
        /// The name of the search set TimeLiner already has attached, when the
        /// attachment is a saved set rather than an ad-hoc selection. An ad-hoc
        /// selection has no name to carry across and is simply not reported.
        /// </summary>
        private string AttachedSetName(Document document, object task)
        {
            try
            {
                object selection = GetProperty(task, "Selection");
                if (selection == null) return null;

                var sources = GetProperty(selection, "SelectionSources") as System.Collections.IEnumerable;
                if (sources == null) return null;

                foreach (object source in sources)
                {
                    var typed = source as SelectionSource;
                    if (typed == null) continue;

                    SavedItem resolved = document.SelectionSets.ResolveSelectionSource(typed);
                    if (resolved != null && !string.IsNullOrEmpty(resolved.DisplayName))
                        return resolved.DisplayName;
                }
            }
            catch (Exception ex)
            {
                Log.Debug("Could not read a TimeLiner attachment: " + ex.Message);
            }

            return null;
        }

        private static DateTime? AsDate(object value)
        {
            if (value == null) return null;
            if (value is DateTime)
            {
                var date = (DateTime)value;
                // TimeLiner uses a sentinel rather than null for "not set".
                return date == DateTime.MinValue || date.Year < 1900 ? (DateTime?)null : date;
            }
            return null;
        }

        private static string FirstString(object instance, params string[] names)
        {
            foreach (string name in names)
            {
                object value = GetProperty(instance, name);
                if (value == null) continue;

                string text = value as string ?? Convert.ToString(value, CultureInfo.InvariantCulture);
                if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
            }
            return "";
        }

        /// <summary>
        /// Empties the task tree. Used by the replace-rather-than-merge path, where
        /// the plugin's list is meant to become the schedule instead of being
        /// folded into it.
        /// </summary>
        public int ClearTasks(Document document, out string error)
        {
            error = null;

            if (!TryBind(document)) { error = BindingError ?? "TimeLiner is unavailable."; return 0; }

            object root = GetProperty(_timeliner, "TasksRoot");
            if (root == null) { error = "TimeLiner has no task root."; return 0; }

            try
            {
                var children = GetProperty(root, "Children") as System.Collections.ICollection;
                int count = children == null ? 0 : children.Count;
                if (count == 0) return 0;

                // Remove from the end: every shape of this API reindexes on remove.
                foreach (string name in new[] { "RemoveTask", "RemoveTaskAt", "Remove", "RemoveAt" })
                {
                    MethodInfo remove = _timeliner.GetType().GetMethod(name, new[] { root.GetType(), typeof(int) });
                    if (remove == null) continue;

                    for (int i = count - 1; i >= 0; i--)
                        remove.Invoke(_timeliner, new object[] { root, i });
                    return count;
                }

                object list = GetProperty(root, "Children");
                MethodInfo clear = list == null ? null : list.GetType().GetMethod("Clear", Type.EmptyTypes);
                if (clear != null) { clear.Invoke(list, null); return count; }

                error = "No supported way to remove tasks in this TimeLiner version.";
                return 0;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return 0;
            }
        }

        /// <summary>
        /// Pushes matched tasks into TimeLiner. Existing tasks are updated in place
        /// rather than duplicated — by the task object they were read from where
        /// there is one, otherwise by synchronisation ID, otherwise by name — so a
        /// refreshed schedule re-import does not multiply the task tree, and a task
        /// renamed inside NavEx moves rather than forks.
        ///
        /// <paramref name="replaceExisting"/> empties the tree first, for when the
        /// plugin's list is meant to become the schedule outright.
        /// </summary>
        public TimelinerSyncResult Sync(Document document, IList<TaskMatch> matches,
                                        bool attachSelections, ProgressContext progress)
        {
            return Sync(document, matches, attachSelections, false, progress);
        }

        public TimelinerSyncResult Sync(Document document, IList<TaskMatch> matches,
                                        bool attachSelections, bool replaceExisting,
                                        ProgressContext progress)
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

            if (replaceExisting)
            {
                string error;
                result.Removed = ClearTasks(document, out error);
                if (error != null) result.Errors.Add(error);

                // The handles now point at tasks that no longer exist.
                foreach (TaskMatch match in matches)
                    if (match != null && match.Task != null) match.Task.SourceHandle = null;
            }

            Dictionary<string, object> existing = replaceExisting
                ? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                : IndexExistingTasks(root);

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
                    // The handle first: it is the only key that survives the user
                    // renaming the task inside NavEx.
                    object existingTask = _taskType.IsInstanceOfType(match.Task.SourceHandle)
                        ? match.Task.SourceHandle
                        : null;

                    if (existingTask == null && !string.IsNullOrWhiteSpace(match.Task.TaskId))
                        existing.TryGetValue("id:" + match.Task.TaskId.Trim(), out existingTask);

                    if (existingTask == null)
                        existing.TryGetValue("name:" + match.Task.Name.Trim(), out existingTask);

                    bool update = existingTask != null;
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
                        // The added task becomes the handle for the next sync, so a
                        // second Apply updates rather than duplicates.
                        match.Task.SourceHandle = AddTask(root, task);
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

        /// <summary>
        /// Every existing task, keyed both by synchronisation ID and by name, and
        /// found at any depth — a task nested under a WBS parent is still the task
        /// a re-sync should update rather than a second copy to create.
        /// </summary>
        private Dictionary<string, object> IndexExistingTasks(object root)
        {
            var map = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            try { IndexInto(root, map, 0); }
            catch (Exception) { }
            return map;
        }

        private void IndexInto(object parent, Dictionary<string, object> map, int depth)
        {
            if (depth > 32) return;

            var children = GetProperty(parent, "Children") as System.Collections.IEnumerable;
            if (children == null) return;

            foreach (object child in children)
            {
                string id = FirstString(child, "SynchronizationId", "SynchronizationID");
                if (!string.IsNullOrEmpty(id) && !map.ContainsKey("id:" + id)) map["id:" + id] = child;

                var name = GetProperty(child, "DisplayName") as string;
                if (!string.IsNullOrEmpty(name))
                {
                    string key = "name:" + name.Trim();
                    if (!map.ContainsKey(key)) map[key] = child;
                }

                IndexInto(child, map, depth + 1);
            }
        }

        /// <summary>
        /// Adds a task and returns the object that actually ended up in the tree,
        /// which is not necessarily the one handed in — the preferred API copies.
        /// </summary>
        private object AddTask(object root, object task)
        {
            // Releases differ on whether the task tree is mutated through the
            // timeliner or the root, and on whether the call copies or takes
            // ownership. Try the known shapes in order of preference.
            MethodInfo addCopy = _timeliner.GetType().GetMethod("AddTaskCopy",
                new[] { root.GetType(), _taskType });
            if (addCopy != null) return Added(root, addCopy.Invoke(_timeliner, new[] { root, task }), task);

            MethodInfo add = _timeliner.GetType().GetMethod("AddTask", new[] { root.GetType(), _taskType });
            if (add != null) return Added(root, add.Invoke(_timeliner, new[] { root, task }), task);

            var children = GetProperty(root, "Children");
            if (children != null)
            {
                MethodInfo childAdd = children.GetType().GetMethod("Add", new[] { _taskType });
                if (childAdd != null) { childAdd.Invoke(children, new[] { task }); return task; }
            }

            throw new InvalidOperationException("No supported way to add a task to this TimeLiner version.");
        }

        /// <summary>The returned task if the call gave one back, else the tree's new last child.</summary>
        private object Added(object root, object returned, object fallback)
        {
            if (_taskType.IsInstanceOfType(returned)) return returned;

            object last = null;
            var children = GetProperty(root, "Children") as System.Collections.IEnumerable;
            if (children != null) foreach (object child in children) last = child;

            return last ?? fallback;
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
