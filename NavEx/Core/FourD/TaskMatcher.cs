using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace NavEx.FourD
{
    /// <summary>
    /// A candidate target for a schedule task: a search set, with the 4D identity
    /// derived from its name.
    /// </summary>
    public class MatchTarget
    {
        public string SetName = "";
        public string SetPath = "";       // folder path, for display
        public object Tag;                // the SavedItem, kept opaque for testability
        public FourDName Identity;
        public int ItemCount = -1;        // -1 = not counted yet

        public override string ToString() { return SetName; }
    }

    /// <summary>How a task ended up attached to a set.</summary>
    public enum MatchState { Unmatched, Auto, Manual, Confirmed, Excluded }

    public class TaskMatch
    {
        public ScheduleTask Task;
        public MatchTarget Target;
        public double Score;
        public MatchState State = MatchState.Unmatched;
        public string Reason = "";
        public readonly List<MatchTarget> Alternatives = new List<MatchTarget>();

        public bool IsAttached
        {
            get { return Target != null && State != MatchState.Excluded; }
        }

        /// <summary>Bands the score for the review grid. Deliberately conservative.</summary>
        public string ConfidenceBand
        {
            get
            {
                if (Target == null) return "none";
                if (Score >= 0.75) return "high";
                if (Score >= 0.45) return "medium";
                return "low";
            }
        }
    }

    /// <summary>
    /// Scores schedule tasks against model search sets.
    ///
    /// The scoring is deliberately explainable rather than clever. Every task and
    /// every set is reduced to the same three-part identity — level, discipline,
    /// activity — and agreement on those is worth far more than incidental word
    /// overlap, because "Level 5 structural deck" and "L05_STRC_DECK" share almost
    /// no literal text while describing exactly the same work.
    ///
    /// Word overlap still contributes, as a tiebreaker among sets that agree on
    /// the identity. What the matcher never does is attach on word overlap alone
    /// when the levels disagree: hanging "Level 12 drywall" on the level 2 set is
    /// worse than leaving it unmatched, because an unmatched task is visible in
    /// the review grid and a wrong one is not.
    /// </summary>
    public class TaskMatcher
    {
        private readonly NameClassifier _classifier;

        /// <summary>Below this, a candidate is not offered as an automatic match.</summary>
        public double AutoMatchThreshold = 0.45;

        /// <summary>Words that carry no discriminating information in either domain.</summary>
        private static readonly HashSet<string> Noise = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "THE", "AND", "FOR", "WITH", "OF", "TO", "AT", "ON", "IN", "A", "AN",
            "INSTALL", "INSTALLATION", "WORK", "WORKS", "COMPLETE", "COMPLETION",
            "START", "FINISH", "PHASE", "AREA", "ZONE", "TYP", "TYPICAL", "NEW",
            "SET", "SETS", "MODEL", "TASK", "ACTIVITY"
        };

        public TaskMatcher(NameClassifier classifier)
        {
            _classifier = classifier ?? new NameClassifier(new SequenceProfile());
        }

        public List<TaskMatch> MatchAll(IEnumerable<ScheduleTask> tasks, IList<MatchTarget> targets)
        {
            var results = new List<TaskMatch>();
            if (tasks == null) return results;

            // Pre-compute each target's identity and token set once.
            var targetTokens = new Dictionary<MatchTarget, HashSet<string>>();
            foreach (MatchTarget target in targets ?? new List<MatchTarget>())
            {
                if (target.Identity == null)
                    target.Identity = _classifier.Classify(target.SetName, target.SetPath);
                targetTokens[target] = MeaningfulTokens(target.SetName + " " + target.SetPath);
            }

            foreach (ScheduleTask task in tasks)
            {
                results.Add(Match(task, targets, targetTokens));
            }

            return results;
        }

        public TaskMatch Match(ScheduleTask task, IList<MatchTarget> targets,
                               Dictionary<MatchTarget, HashSet<string>> targetTokens)
        {
            var match = new TaskMatch { Task = task };
            if (targets == null || targets.Count == 0) return match;

            FourDName taskIdentity = _classifier.Classify(task.Name, task.Description);
            HashSet<string> taskWords = MeaningfulTokens(task.SearchText);

            var scored = new List<KeyValuePair<MatchTarget, double>>();
            var reasons = new Dictionary<MatchTarget, string>();

            foreach (MatchTarget target in targets)
            {
                HashSet<string> words;
                if (targetTokens == null || !targetTokens.TryGetValue(target, out words))
                    words = MeaningfulTokens(target.SetName + " " + target.SetPath);

                string reason;
                double score = Score(taskIdentity, taskWords, target, words, out reason);
                if (score <= 0) continue;

                scored.Add(new KeyValuePair<MatchTarget, double>(target, score));
                reasons[target] = reason;
            }

            if (scored.Count == 0)
            {
                match.Reason = "nothing scored above zero";
                return match;
            }

            scored.Sort((a, b) => b.Value.CompareTo(a.Value));

            MatchTarget best = scored[0].Key;
            double bestScore = scored[0].Value;

            match.Score = bestScore;
            foreach (KeyValuePair<MatchTarget, double> entry in scored.Take(8))
                match.Alternatives.Add(entry.Key);

            if (bestScore >= AutoMatchThreshold)
            {
                match.Target = best;
                match.State = MatchState.Auto;
                match.Reason = reasons[best];

                // Flag a near-tie: two sets scoring the same means the task text
                // does not actually distinguish them, and silently taking the
                // first is how a 4D model quietly goes wrong.
                if (scored.Count > 1 && Math.Abs(scored[1].Value - bestScore) < 0.05)
                    match.Reason += " (ambiguous: ties with '" + scored[1].Key.SetName + "')";
            }
            else
            {
                match.Reason = "best candidate '" + best.SetName +
                               "' scored " + bestScore.ToString("0.00", CultureInfo.InvariantCulture) +
                               ", below threshold";
            }

            return match;
        }

        /// <summary>
        /// 0 means "do not offer". The weights are tuned so identity agreement
        /// alone can clear the threshold, and text overlap alone cannot.
        /// </summary>
        private double Score(FourDName taskIdentity, HashSet<string> taskWords,
                             MatchTarget target, HashSet<string> targetWords, out string reason)
        {
            var notes = new List<string>();
            double score = 0;

            FourDName setIdentity = target.Identity;

            bool taskHasLevel = taskIdentity.LevelTag != "L00";
            bool setHasLevel = setIdentity != null && setIdentity.LevelTag != "L00";

            if (taskHasLevel && setHasLevel)
            {
                if (taskIdentity.LevelTag == setIdentity.LevelTag)
                {
                    score += 0.35;
                    notes.Add("level " + taskIdentity.LevelTag);
                }
                else
                {
                    // A level conflict is disqualifying, not merely unhelpful.
                    reason = "level mismatch (" + taskIdentity.LevelTag + " vs " + setIdentity.LevelTag + ")";
                    return 0;
                }
            }

            if (setIdentity != null)
            {
                if (!string.IsNullOrEmpty(taskIdentity.DisciplineCode) &&
                    taskIdentity.DisciplineCode == setIdentity.DisciplineCode)
                {
                    score += 0.25;
                    notes.Add("discipline " + taskIdentity.DisciplineCode);
                }
                else if (!string.IsNullOrEmpty(taskIdentity.DisciplineCode) &&
                         !string.IsNullOrEmpty(setIdentity.DisciplineCode))
                {
                    // Different named disciplines: strong evidence against.
                    score -= 0.25;
                    notes.Add("discipline differs");
                }

                if (!string.IsNullOrEmpty(taskIdentity.ActivityCode) &&
                    taskIdentity.ActivityCode == setIdentity.ActivityCode)
                {
                    score += 0.3;
                    notes.Add("activity " + taskIdentity.ActivityCode);
                }
            }

            double overlap = Overlap(taskWords, targetWords);
            if (overlap > 0)
            {
                score += 0.3 * overlap;
                notes.Add("text " + overlap.ToString("0.00", CultureInfo.InvariantCulture));
            }

            // An exact name equality is decisive whatever else happened.
            if (string.Equals((target.SetName ?? "").Trim(), (taskIdentity.Description ?? "").Trim(),
                              StringComparison.OrdinalIgnoreCase))
            {
                score += 0.4;
                notes.Add("name equals set");
            }

            if (score < 0) score = 0;
            reason = notes.Count == 0 ? "no shared attributes" : string.Join(", ", notes.ToArray());
            return Math.Min(1.0, score);
        }

        /// <summary>Jaccard-style overlap, biased toward the shorter side.</summary>
        private static double Overlap(HashSet<string> a, HashSet<string> b)
        {
            if (a == null || b == null || a.Count == 0 || b.Count == 0) return 0;
            int shared = a.Count(b.Contains);
            if (shared == 0) return 0;
            return (double)shared / Math.Min(a.Count, b.Count);
        }

        private static HashSet<string> MeaningfulTokens(string text)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string token in NameClassifier.Tokenize(text ?? ""))
            {
                if (token.Length < 3) continue;
                if (Noise.Contains(token)) continue;
                // Pure numbers are level or sequence noise; the level comparison
                // already handled the one that matters.
                if (token.All(char.IsDigit)) continue;
                set.Add(token);
            }
            return set;
        }

        /// <summary>
        /// Carries manual decisions across a re-import. Keyed on the task's stable
        /// key, so an updated P6 export refreshes dates while every human
        /// correction survives — which is the whole point of re-importing rather
        /// than rebuilding.
        /// </summary>
        public static void ApplyRemembered(IEnumerable<TaskMatch> matches,
                                           IDictionary<string, string> remembered,
                                           IList<MatchTarget> targets)
        {
            if (matches == null || remembered == null) return;

            var byName = new Dictionary<string, MatchTarget>(StringComparer.OrdinalIgnoreCase);
            foreach (MatchTarget target in targets ?? new List<MatchTarget>())
                byName[target.SetName] = target;

            foreach (TaskMatch match in matches)
            {
                string setName;
                if (!remembered.TryGetValue(match.Task.StableKey, out setName)) continue;

                if (string.IsNullOrEmpty(setName))
                {
                    match.Target = null;
                    match.State = MatchState.Excluded;
                    match.Reason = "excluded previously";
                    continue;
                }

                MatchTarget target;
                if (byName.TryGetValue(setName, out target))
                {
                    match.Target = target;
                    match.State = MatchState.Manual;
                    match.Score = 1.0;
                    match.Reason = "set manually";
                }
                else
                {
                    match.Reason = "remembered set '" + setName + "' is no longer in the model";
                }
            }
        }

        public static Dictionary<string, string> Remember(IEnumerable<TaskMatch> matches)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (matches == null) return map;

            foreach (TaskMatch match in matches)
            {
                if (match.State == MatchState.Manual || match.State == MatchState.Confirmed)
                    map[match.Task.StableKey] = match.Target == null ? "" : match.Target.SetName;
                else if (match.State == MatchState.Excluded)
                    map[match.Task.StableKey] = "";
            }

            return map;
        }
    }
}
