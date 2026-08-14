using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using NavEx.FourD;

namespace NavEx
{
    /// <summary>
    /// Tests for the 4D layer. Sequencing, name classification, schedule parsing
    /// and task matching never touch Navisworks, so they run anywhere — which
    /// matters because these are the parts whose failure modes are silent. A
    /// mis-sequenced code still produces a perfectly valid file name; it just
    /// sorts the folder into the wrong build order, and nobody notices until the
    /// model is already in someone else's software.
    /// </summary>
    internal static class FourDTests
    {
        private static int _failures;
        private static int _checks;

        public static int Run()
        {
            _failures = 0;
            _checks = 0;

            Console.WriteLine();
            Console.WriteLine("4D tests");
            Console.WriteLine("--------");

            HighRiseSortOrder();
            SequenceIsStableAcrossLevels();
            Classification();
            LevelDetection();
            RoundTrip();
            CsvImport();
            DelimiterAndDates();
            XerImport();
            MissingRequiredColumns();
            Matching();
            RememberedDecisions();
            ScaleToTasksDistribution();

            Console.WriteLine();
            Console.WriteLine(_failures == 0
                ? string.Format(CultureInfo.InvariantCulture, "All {0} 4D checks passed.", _checks)
                : string.Format(CultureInfo.InvariantCulture, "{0} of {1} 4D checks FAILED.", _failures, _checks));

            return _failures;
        }

        // ── The requirement: a sorted folder reads as the build sequence ─────

        /// <summary>
        /// The scenario from the brief: a 15-storey building with structure
        /// leading, interior framing 3 floors behind, and curtain wall 5 floors
        /// behind. Sorting the generated names must interleave the three trades
        /// into the staggered order they are actually built in.
        /// </summary>
        private static void HighRiseSortOrder()
        {
            var profile = new SequenceProfile();
            Activity deck = profile.Resolve("DECK");
            Activity framing = profile.Resolve("FRMG");
            Activity curtainWall = profile.Resolve("CWAL");

            Check("structure leads (lag 0)", deck.LagFloors == 0);
            Check("framing trails 3 floors", framing.LagFloors == 3);
            Check("curtain wall trails 5 floors", curtainWall.LagFloors == 5);

            var names = new List<string>();
            for (int level = 1; level <= 15; level++)
            {
                names.Add(Render(level, deck));
                names.Add(Render(level, framing));
                names.Add(Render(level, curtainWall));
            }

            List<string> sorted = names.OrderBy(n => n, StringComparer.Ordinal).ToList();

            // Concurrency check: when the structure tops out floor 8, framing
            // should be on 5 and curtain wall on 3, and those three entries should
            // be adjacent in the sorted list, in that top-down order.
            int deckIndex = sorted.IndexOf(Render(8, deck));
            int framingIndex = sorted.IndexOf(Render(5, framing));
            int curtainIndex = sorted.IndexOf(Render(3, curtainWall));

            Check("L08 deck, L05 framing and L03 curtain wall are concurrent",
                  deckIndex >= 0 && framingIndex == deckIndex + 1 && curtainIndex == deckIndex + 2,
                  string.Format(CultureInfo.InvariantCulture, "indices {0}/{1}/{2}",
                                deckIndex, framingIndex, curtainIndex));

            // Per trade, floors must still ascend.
            List<int> deckOrder = sorted
                .Select((n, i) => new { n, i })
                .Where(x => x.n.Contains("_STRC_DECK"))
                .Select(x => LevelOf(x.n))
                .ToList();
            Check("structure floors ascend in sort order",
                  deckOrder.SequenceEqual(deckOrder.OrderBy(v => v)));

            // Structure on any floor must precede that same floor's framing.
            bool ok = true;
            for (int level = 1; level <= 15; level++)
                if (sorted.IndexOf(Render(level, deck)) > sorted.IndexOf(Render(level, framing))) ok = false;
            Check("each floor's structure precedes its framing", ok);

            // And the first thing in the whole list is structure, not skin.
            Check("the sequence opens with structure", sorted[0].Contains("_STRC_"), sorted[0]);
        }

        private static void SequenceIsStableAcrossLevels()
        {
            var profile = new SequenceProfile();
            Activity deck = profile.Resolve("DECK");

            // Basements and site work must not produce negative or clipped codes.
            string site = SequenceModel.SequenceCode(SequenceModel.SiteLevelIndex, profile.Resolve("EXCV"));
            string basement = SequenceModel.SequenceCode(-2, deck);
            string ground = SequenceModel.SequenceCode(1, deck);

            Check("site code is 5 digits", site.Length == 5, site);
            Check("basement sorts before ground",
                  string.CompareOrdinal(basement, ground) < 0, basement + " vs " + ground);
            Check("site sorts before basement",
                  string.CompareOrdinal(site, basement) < 0, site + " vs " + basement);

            // A lag override must move the trade, which is the whole point of the
            // editable table.
            profile.LagOverrides["CWAL"] = 9;
            Activity slowSkin = profile.Resolve("CWAL");
            Check("lag override is applied", slowSkin.LagFloors == 9);
            Check("override moves the code later",
                  string.CompareOrdinal(SequenceModel.SequenceCode(3, slowSkin),
                                        SequenceModel.SequenceCode(3, profile.Resolve("DECK"))) > 0);
        }

        private static string Render(int level, Activity activity)
        {
            return SequenceModel.SequenceCode(level, activity)
                 + "_" + FourDName.LevelTagFor(level)
                 + "_" + activity.DisciplineCode
                 + "_" + activity.Code;
        }

        private static int LevelOf(string rendered)
        {
            string[] parts = rendered.Split('_');
            return FourDName.LevelIndexFromTag(parts[1]);
        }

        // ── Classification ───────────────────────────────────────────────────

        private static void Classification()
        {
            var classifier = new NameClassifier(new SequenceProfile());

            FourDName structural = classifier.Classify("L01_STRC");
            Check("L01_STRC -> STRC", structural.DisciplineCode == "STRC", structural.DisciplineCode);
            Check("L01_STRC -> level 1", structural.LevelTag == "L01", structural.LevelTag);

            FourDName arch = classifier.Classify("L05_ARCS");
            Check("L05_ARCS -> ARCS", arch.DisciplineCode == "ARCS", arch.DisciplineCode);

            FourDName mech = classifier.Classify("L12_MECH");
            Check("L12_MECH -> MECH", mech.DisciplineCode == "MECH", mech.DisciplineCode);

            FourDName fire = classifier.Classify("L03_FIRE");
            Check("L03_FIRE -> FIRE", fire.DisciplineCode == "FIRE", fire.DisciplineCode);

            FourDName plumbing = classifier.Classify("L07_PLBG");
            Check("L07_PLBG -> PLBG", plumbing.DisciplineCode == "PLBG", plumbing.DisciplineCode);

            // Longest alias wins: curtain wall must not resolve as a generic wall.
            FourDName curtain = classifier.Classify("Level 4 - Curtain Wall - South");
            Check("'Curtain Wall' -> CWAL", curtain.ActivityCode == "CWAL", curtain.ActivityCode);
            Check("'Curtain Wall' implies ARCS", curtain.DisciplineCode == "ARCS", curtain.DisciplineCode);
            Check("'Level 4' -> L04", curtain.LevelTag == "L04", curtain.LevelTag);

            // Discipline disambiguates shared vocabulary.
            FourDName plumbRough = classifier.Classify("L06 Plumbing Rough-In");
            Check("plumbing rough-in stays with PLBG",
                  plumbRough.DisciplineCode == "PLBG", plumbRough.DisciplineCode);

            FourDName framing = classifier.Classify("L05_ARCS_Interior Metal Stud Framing");
            Check("metal stud framing -> FRMG", framing.ActivityCode == "FRMG", framing.ActivityCode);

            FourDName unknown = classifier.Classify("Miscellaneous stuff");
            Check("an unmatched name resolves to nothing", !unknown.IsResolved);
            Check("an unmatched name has zero-ish confidence", unknown.Confidence < 0.5);

            // Learned overrides.
            classifier.DisciplineOverrides["SS"] = "STRC";
            FourDName learned = classifier.Classify("L02_SS_Deck");
            Check("a learned token is honoured", learned.DisciplineCode == "STRC", learned.DisciplineCode);
        }

        private static void LevelDetection()
        {
            var cases = new Dictionary<string, string>
            {
                { "L01_ARCS", "L01" },
                { "LVL2 Structural", "L02" },
                { "Level 03 Mechanical", "L03" },
                { "3RD FLOOR PLUMBING", "L03" },
                { "FLOOR 12 drywall", "L12" },
                { "B2 parking electrical", "L-02" },
                { "Roof membrane", "ROOF" },
                { "Sitework grading", "SITE" },
            };

            foreach (KeyValuePair<string, string> test in cases)
            {
                int level;
                bool found = NameClassifier.TryMatchLevel(test.Key, NameClassifier.Tokenize(test.Key), out level);
                string tag = FourDName.LevelTagFor(level);
                Check("level of '" + test.Key + "' is " + test.Value,
                      found && tag == test.Value, tag);
            }
        }

        private static void RoundTrip()
        {
            var classifier = new NameClassifier(new SequenceProfile());
            FourDName original = classifier.Classify("L05_ARCS_Interior Framing");
            string rendered = original.Render(true);

            FourDName reparsed = FourDName.TryParse(rendered);
            Check("a generated name parses back", reparsed != null, rendered);
            if (reparsed == null) return;

            Check("round trip keeps the sequence", reparsed.SequenceCode == original.SequenceCode);
            Check("round trip keeps the level", reparsed.LevelTag == original.LevelTag);
            Check("round trip keeps the discipline", reparsed.DisciplineCode == original.DisciplineCode);
            Check("round trip keeps the activity", reparsed.ActivityCode == original.ActivityCode);

            // Re-classifying an already-coded name must be idempotent, or repeated
            // runs would stack prefixes.
            FourDName again = classifier.Classify(rendered);
            Check("re-classifying a coded name is idempotent",
                  again.Render(true) == rendered, again.Render(true));
        }

        // ── Schedule import ──────────────────────────────────────────────────

        private static void CsvImport()
        {
            string path = Path.Combine(Path.GetTempPath(), "navex-test-schedule.csv");
            File.WriteAllText(path, string.Join("\n", new[]
            {
                "Activity ID,Activity Name,Start,Finish,Original Duration,Activity Type,Notes",
                "A1000,L01 Structural Deck Pour,2026-03-02,2026-03-06,5,Construct,Level 1 slab",
                "A1010,L02 Structural Deck Pour,2026-03-09,2026-03-13,5,Construct,Level 2 slab",
                "A1020,\"L01 Interior Framing, typical\",2026-03-23,2026-03-27,5,Construct,Metal stud",
                "A1030,L01 Curtain Wall,2026-04-06,2026-04-17,10,Construct,South elevation"
            }), Encoding.UTF8);

            ScheduleImportResult result = ScheduleImporter.Import(path);

            Check("CSV detected", result.FormatName == "CSV", result.FormatName);
            Check("CSV rows read", result.Rows.Count == 4, result.Rows.Count.ToString(CultureInfo.InvariantCulture));
            Check("CSV needs no manual mapping", !result.NeedsMapping, result.Summary());
            Check("all CSV tasks valid", result.ValidTaskCount == 4,
                  result.ValidTaskCount.ToString(CultureInfo.InvariantCulture));

            ScheduleTask first = result.Tasks[0];
            Check("task id mapped", first.TaskId == "A1000", first.TaskId);
            Check("task name mapped", first.Name == "L01 Structural Deck Pour", first.Name);
            Check("start parsed", first.PlannedStart == new DateTime(2026, 3, 2),
                  first.PlannedStart.ToString());
            Check("finish parsed", first.PlannedFinish == new DateTime(2026, 3, 6),
                  first.PlannedFinish.ToString());
            Check("duration mapped", first.DurationDays.HasValue && Math.Abs(first.DurationDays.Value - 5) < 0.01);
            Check("description mapped", first.Description == "Level 1 slab", first.Description);
            Check("task type normalised", first.NormalizedTaskType() == "Construct");

            // A quoted field containing the delimiter must survive.
            Check("quoted comma preserved",
                  result.Tasks[2].Name == "L01 Interior Framing, typical", result.Tasks[2].Name);

            File.Delete(path);
        }

        private static void DelimiterAndDates()
        {
            string path = Path.Combine(Path.GetTempPath(), "navex-test-schedule.tsv");
            File.WriteAllText(path, string.Join("\n", new[]
            {
                "Task Name\tPlanned Start\tPlanned Finish",
                "L03 Drywall\t15-Mar-26\t22-Mar-26",
                "L03 Ceiling Grid\t03/23/2026\t03/27/2026"
            }), Encoding.UTF8);

            ScheduleImportResult result = ScheduleImporter.Import(path);
            Check("tab delimiter sniffed", result.FormatName == "Tab-separated", result.FormatName);
            Check("both TSV tasks valid", result.ValidTaskCount == 2,
                  result.ValidTaskCount.ToString(CultureInfo.InvariantCulture));
            Check("dd-MMM-yy parsed", result.Tasks[0].PlannedStart == new DateTime(2026, 3, 15),
                  result.Tasks[0].PlannedStart.ToString());
            Check("MM/dd/yyyy parsed", result.Tasks[1].PlannedStart == new DateTime(2026, 3, 23),
                  result.Tasks[1].PlannedStart.ToString());

            File.Delete(path);
        }

        private static void XerImport()
        {
            string path = Path.Combine(Path.GetTempPath(), "navex-test-schedule.xer");
            File.WriteAllText(path, string.Join("\n", new[]
            {
                "ERMHDR\t19.12\t2026-03-01\tProject",
                "%T\tPROJWBS",
                "%F\twbs_id\twbs_name",
                "%R\t1\tTower",
                "%T\tTASK",
                "%F\ttask_id\ttask_code\ttask_name\ttarget_start_date\ttarget_end_date\ttask_type",
                "%R\t101\tA1000\tL01 Structural Deck\t2026-03-02 08:00\t2026-03-06 17:00\tTT_Task",
                "%R\t102\tA1010\tL02 Structural Deck\t2026-03-09 08:00\t2026-03-13 17:00\tTT_Task",
                "%E"
            }), Encoding.UTF8);

            ScheduleImportResult result = ScheduleImporter.Import(path);

            Check("XER detected", result.FormatName.Contains("XER"), result.FormatName);
            Check("only the TASK table is read", result.Rows.Count == 2,
                  result.Rows.Count.ToString(CultureInfo.InvariantCulture));
            Check("XER tasks valid", result.ValidTaskCount == 2,
                  result.ValidTaskCount.ToString(CultureInfo.InvariantCulture));
            Check("target_start_date mapped",
                  result.Tasks[0].PlannedStart == new DateTime(2026, 3, 2, 8, 0, 0),
                  result.Tasks[0].PlannedStart.ToString());
            Check("task_name mapped", result.Tasks[0].Name == "L01 Structural Deck", result.Tasks[0].Name);

            File.Delete(path);
        }

        private static void MissingRequiredColumns()
        {
            string path = Path.Combine(Path.GetTempPath(), "navex-test-nodate.csv");
            File.WriteAllText(path, string.Join("\n", new[]
            {
                "Ref,Scope",
                "1,L01 Structural Deck",
                "2,L02 Structural Deck"
            }), Encoding.UTF8);

            ScheduleImportResult result = ScheduleImporter.Import(path);

            Check("a schedule with no dates is flagged for mapping", result.NeedsMapping, result.Summary());
            List<ScheduleField> missing = result.MissingRequiredFields().ToList();
            Check("planned start reported missing", missing.Contains(ScheduleField.PlannedStart));
            Check("planned finish reported missing", missing.Contains(ScheduleField.PlannedFinish));

            File.Delete(path);
        }

        // ── Matching ─────────────────────────────────────────────────────────

        private static void Matching()
        {
            var classifier = new NameClassifier(new SequenceProfile());
            var matcher = new TaskMatcher(classifier);

            var targets = new List<MatchTarget>
            {
                Target("00840_L05_STRC_DECK"),
                Target("00240_L02_STRC_DECK"),
                Target("00868_L05_ARCS_FRMG"),
                Target("00872_L05_ARCS_CWAL"),
                Target("00280_L02_ARCS_DRYW"),
            };

            var tasks = new List<ScheduleTask>
            {
                Task("Level 5 Structural Deck Pour", "Slab on metal deck"),
                Task("L02 Drywall", "Board and tape"),
                Task("Level 5 Curtain Wall", "Glazing, south elevation"),
                Task("Procure elevator cabs", "Long lead item"),
            };

            List<TaskMatch> matches = matcher.MatchAll(tasks, targets);

            Check("deck task matched to the L05 deck set",
                  matches[0].Target != null && matches[0].Target.SetName == "00840_L05_STRC_DECK",
                  Describe(matches[0]));

            Check("drywall task matched to the L02 drywall set",
                  matches[1].Target != null && matches[1].Target.SetName == "00280_L02_ARCS_DRYW",
                  Describe(matches[1]));

            Check("curtain wall task matched to the L05 curtain wall set",
                  matches[2].Target != null && matches[2].Target.SetName == "00872_L05_ARCS_CWAL",
                  Describe(matches[2]));

            // A procurement activity has no geometry; leaving it unmatched is the
            // correct answer, and far better than attaching it to something.
            Check("a non-model task stays unmatched", matches[3].Target == null, Describe(matches[3]));

            // A level conflict must veto, even with strong word overlap.
            var wrongLevel = new List<ScheduleTask> { Task("Level 12 Structural Deck Pour", "Slab on metal deck") };
            List<TaskMatch> vetoed = matcher.MatchAll(wrongLevel, targets);
            Check("a level mismatch is never matched", vetoed[0].Target == null, Describe(vetoed[0]));
        }

        private static void RememberedDecisions()
        {
            var classifier = new NameClassifier(new SequenceProfile());
            var matcher = new TaskMatcher(classifier);

            var targets = new List<MatchTarget> { Target("00840_L05_STRC_DECK"), Target("00868_L05_ARCS_FRMG") };
            var tasks = new List<ScheduleTask> { Task("Level 5 Structural Deck Pour", "") };

            List<TaskMatch> matches = matcher.MatchAll(tasks, targets);
            matches[0].Target = targets[1];
            matches[0].State = MatchState.Manual;

            Dictionary<string, string> remembered = TaskMatcher.Remember(matches);
            Check("a manual decision is remembered", remembered.Count == 1);

            // Re-import: fresh matches, then replay the remembered decision.
            List<TaskMatch> reimported = matcher.MatchAll(tasks, targets);
            Check("a fresh match reverts to the automatic answer",
                  reimported[0].Target == targets[0], Describe(reimported[0]));

            TaskMatcher.ApplyRemembered(reimported, remembered, targets);
            Check("the remembered decision survives a re-import",
                  reimported[0].Target == targets[1] && reimported[0].State == MatchState.Manual,
                  Describe(reimported[0]));
        }

        // ── Scale to tasks ───────────────────────────────────────────────────

        private static void ScaleToTasksDistribution()
        {
            // Even split: 6 sets, 3 tasks -> 2 each, no remainder.
            var evenSets = new List<MatchTarget>
            {
                Target("00280_L02_ARCS_DRYW"),
                Target("00840_L05_STRC_DECK"),
                Target("00240_L02_STRC_DECK"),
                Target("00872_L05_ARCS_CWAL"),
                Target("00868_L05_ARCS_FRMG"),
                Target("00100_L01_STRC_DECK"),
            };
            var threeTasks = new List<ScheduleTask> { Task("Task A", ""), Task("Task B", ""), Task("Task C", "") };

            List<ScaleAssignment> even = ScaleToTasks.Distribute(evenSets, threeTasks, new ScaleOptions());
            Check("even split assigns every set", even.Count == evenSets.Count, even.Count.ToString(CultureInfo.InvariantCulture));

            var evenCounts = threeTasks.ToDictionary(t => t, t => even.Count(a => a.Task == t));
            Check("even split gives each task exactly 2", evenCounts.Values.All(c => c == 2),
                  string.Join(",", evenCounts.Values.Select(c => c.ToString(CultureInfo.InvariantCulture))));

            // Ordering follows sequence code: task A (first task) should receive
            // the two lowest-coded sets, in ascending order.
            List<MatchTarget> taskASets = even.Where(a => a.Task == threeTasks[0]).Select(a => a.Set).ToList();
            Check("ordering follows sequence code, not input order",
                  taskASets.Count == 2 &&
                  taskASets[0].SetName == "00100_L01_STRC_DECK" &&
                  taskASets[1].SetName == "00240_L02_STRC_DECK",
                  string.Join(" | ", taskASets.Select(s => s.SetName)));

            Check("every assignment carries a reason", even.All(a => !string.IsNullOrEmpty(a.Reason)));

            // Uneven remainder: 7 sets, 3 tasks -> 3,2,2, remainder to the
            // earliest task(s).
            var sevenSets = new List<MatchTarget>(evenSets) { Target("00900_L06_ARCS_CWAL") };
            List<ScaleAssignment> uneven = ScaleToTasks.Distribute(sevenSets, threeTasks, new ScaleOptions());
            var unevenCounts = threeTasks.Select(t => uneven.Count(a => a.Task == t)).ToList();
            Check("uneven remainder is 3,2,2",
                  unevenCounts.Count == 3 && unevenCounts[0] == 3 && unevenCounts[1] == 2 && unevenCounts[2] == 2,
                  string.Join(",", unevenCounts.Select(c => c.ToString(CultureInfo.InvariantCulture))));
            Check("no task is left empty when sets >= tasks", unevenCounts.All(c => c > 0));

            // Fewer sets than tasks: 2 sets, 3 tasks -> two tasks get one set,
            // one task is legitimately left empty.
            var twoSets = new List<MatchTarget> { Target("00840_L05_STRC_DECK"), Target("00100_L01_STRC_DECK") };
            List<ScaleAssignment> scarce = ScaleToTasks.Distribute(twoSets, threeTasks, new ScaleOptions());
            var scarceCounts = threeTasks.Select(t => scarce.Count(a => a.Task == t)).ToList();
            Check("fewer sets than tasks assigns every set exactly once",
                  scarce.Count == 2, scarce.Count.ToString(CultureInfo.InvariantCulture));
            Check("fewer sets than tasks leaves exactly one task empty",
                  scarceCounts.Count(c => c == 0) == 1,
                  string.Join(",", scarceCounts.Select(c => c.ToString(CultureInfo.InvariantCulture))));

            // Idempotent re-run: distributing the same inputs twice yields the
            // same set-to-task pairing.
            List<ScaleAssignment> rerun = ScaleToTasks.Distribute(evenSets, threeTasks, new ScaleOptions());
            bool sameEveryTime = even.Count == rerun.Count &&
                Enumerable.Range(0, even.Count).All(i => even[i].Set == rerun[i].Set && even[i].Task == rerun[i].Task);
            Check("re-running with the same inputs is idempotent", sameEveryTime);
        }

        private static MatchTarget Target(string name)
        {
            return new MatchTarget { SetName = name, Tag = new object() };
        }

        private static ScheduleTask Task(string name, string description)
        {
            return new ScheduleTask
            {
                Name = name,
                Description = description,
                PlannedStart = new DateTime(2026, 3, 2),
                PlannedFinish = new DateTime(2026, 3, 6)
            };
        }

        private static string Describe(TaskMatch match)
        {
            return (match.Target == null ? "(none)" : match.Target.SetName)
                 + " score " + match.Score.ToString("0.00", CultureInfo.InvariantCulture)
                 + " — " + match.Reason;
        }

        // ── Harness ──────────────────────────────────────────────────────────

        private static void Check(string description, bool condition) { Check(description, condition, null); }

        private static void Check(string description, bool condition, string detail)
        {
            _checks++;
            if (condition)
            {
                Console.WriteLine("  ok   " + description);
            }
            else
            {
                _failures++;
                Console.WriteLine("  FAIL " + description + (string.IsNullOrEmpty(detail) ? "" : "  [got: " + detail + "]"));
            }
        }
    }
}
