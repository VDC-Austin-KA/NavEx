using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace NavEx.FourD
{
    /// <summary>Tuning knobs for <see cref="ScaleToTasks.Distribute"/>. Empty today —
    /// kept as a parameter (rather than adding one later and breaking the
    /// signature) because the work order names it explicitly.</summary>
    public class ScaleOptions
    {
    }

    /// <summary>One suggested set-to-task assignment, with the reasoning the
    /// review grid shows the user before they commit it.</summary>
    public class ScaleAssignment
    {
        public MatchTarget Set;
        public ScheduleTask Task;
        public string Reason = "";
    }

    /// <summary>
    /// Distributes N search sets across M schedule tasks as evenly as possible —
    /// the bulk counterpart to <see cref="TaskMatcher"/>'s one-to-one matching.
    ///
    /// Sets are ordered by build sequence, not alphabetically: a set's name is
    /// re-parsed with <see cref="FourDName.TryParse"/> (the same idempotent
    /// parse the 4D layer already relies on) and sorted on the resulting
    /// sequence code, so "00840_..." lands before "00868_..." regardless of
    /// what a human happened to type first. Sets whose name does not parse as a
    /// 4D name sort after every set that does, in their original order, so an
    /// unclassified set is never silently interleaved into the middle of a
    /// build sequence it was not shown to belong to.
    /// </summary>
    public static class ScaleToTasks
    {
        public static List<ScaleAssignment> Distribute(IList<MatchTarget> sets, IList<ScheduleTask> tasks,
                                                        ScaleOptions options)
        {
            var result = new List<ScaleAssignment>();
            if (sets == null || sets.Count == 0 || tasks == null || tasks.Count == 0) return result;

            List<MatchTarget> ordered = sets
                .Select((set, index) => new { set, index, code = SequenceCodeOf(set) })
                .OrderBy(x => x.code, StringComparer.Ordinal)
                .ThenBy(x => x.index)
                .Select(x => x.set)
                .ToList();

            int setCount = ordered.Count;
            int taskCount = tasks.Count;
            int baseShare = setCount / taskCount;
            int remainder = setCount % taskCount;

            int cursor = 0;
            for (int taskIndex = 0; taskIndex < taskCount; taskIndex++)
            {
                ScheduleTask task = tasks[taskIndex];
                int share = baseShare + (taskIndex < remainder ? 1 : 0);

                for (int slot = 0; slot < share; slot++)
                {
                    MatchTarget set = ordered[cursor];
                    string reason = BuildReason(slot, share, taskIndex, remainder, cursor, setCount);
                    result.Add(new ScaleAssignment { Set = set, Task = task, Reason = reason });
                    cursor++;
                }
            }

            return result;
        }

        private static string BuildReason(int slot, int share, int taskIndex, int remainder, int overallIndex, int setCount)
        {
            string position = string.Format(CultureInfo.InvariantCulture,
                "{0} of {1} in this task's share ({2} of {3} sets)",
                slot + 1, share, overallIndex + 1, setCount);

            if (taskIndex < remainder)
                return position + "; earliest task, carries one extra from the remainder";

            return position + "; even split";
        }

        /// <summary>The sort key: a coded set's own sequence code, or a value that
        /// always sorts after every valid 5-digit code for one that has none.</summary>
        private static string SequenceCodeOf(MatchTarget set)
        {
            FourDName parsed = set == null ? null : FourDName.TryParse(set.SetName);
            return parsed != null ? parsed.SequenceCode : "ZZZZZ";
        }
    }
}
