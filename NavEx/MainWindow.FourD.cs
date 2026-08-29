using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Navisworks.Api;
using NavEx.Controls;
using NavEx.Core;
using NavEx.FourD;
using NavApp = Autodesk.Navisworks.Api.Application;

namespace NavEx
{
    /// <summary>
    /// The 4D tab: schedule import, task-to-model matching, TimeLiner sync, and
    /// the naming/renaming tools that make a folder listing sort into build order.
    ///
    /// Kept in its own partial so the export UI and the 4D UI stay separable.
    /// </summary>
    public partial class MainWindow
    {
        private SequenceProfile _profile;
        private NameClassifier _classifier;
        private SetRenamer _renamer;
        private TaskMatcher _matcher;
        private FourDState _state;

        private ScheduleImportResult _schedule;
        private List<TaskMatch> _matches = new List<TaskMatch>();
        private List<MatchTarget> _targets = new List<MatchTarget>();
        private List<RenameProposal> _proposals = new List<RenameProposal>();
        private List<GroupProposal> _groupProposals = new List<GroupProposal>();
        private List<ActivityRow> _activityRows = new List<ActivityRow>();
        private List<RuleRow> _ruleRows = new List<RuleRow>();
        private List<CustomActivityRow> _customActivityRows = new List<CustomActivityRow>();
        private bool _fourDReady;

        /// <summary>
        /// Builds the 4D layer. Called unconditionally, including when no model is
        /// open: the identifier library, the sequencing table and the naming rules
        /// are all editable without a document, and every 4D handler used to throw
        /// a null reference the moment the window had been opened on an empty
        /// Navisworks.
        /// </summary>
        private void InitializeFourD()
        {
            if (_fourDReady) return;

            Document document = NavApp.ActiveDocument;

            _profile = new SequenceProfile();
            _profile.Identifiers = IdentifierLibrary.Load();
            _classifier = new NameClassifier(_profile);
            _state = FourDState.Load(document == null ? "" : document.Title);
            _state.ApplyTo(_classifier, _profile);

            _renamer = new SetRenamer(_classifier);
            _matcher = new TaskMatcher(_classifier);

            RefreshActivityRows();
            RefreshIdentifierRows();
            _fourDReady = true;

            foreach (string warning in _profile.Identifiers.Warnings) Log.Warning(warning);
        }

        /// <summary>
        /// Guards every 4D handler. Initialisation is cheap and idempotent, so the
        /// cost of being safe here is nothing, and the cost of not being safe is a
        /// null reference in front of the user.
        /// </summary>
        private bool EnsureFourD()
        {
            try
            {
                InitializeFourD();
                return _fourDReady;
            }
            catch (Exception ex)
            {
                Log.Error("Could not start the 4D tools", ex);
                return false;
            }
        }

        private void RefreshActivityRows()
        {
            _activityRows = _profile.AllResolved().Select(a => new ActivityRow(a)).ToList();
            ActivityListBox.ItemsSource = null;
            ActivityListBox.ItemsSource = _activityRows;
        }

        // ── Schedule ─────────────────────────────────────────────────────────

        /// <summary>
        /// Loads the schedule the model already has.
        ///
        /// This is the path that should have existed from the start. A model with a
        /// populated TimeLiner is the normal case, not the exception, and requiring
        /// a file import before the tab would do anything meant re-exporting a
        /// schedule that was already sitting in the document.
        /// </summary>
        private void OnReadTimeliner(object sender, RoutedEventArgs e)
        {
            if (!EnsureFourD()) return;

            Document document = NavApp.ActiveDocument;
            if (document == null) { Log.Warning("Open a model first."); return; }

            var bridge = new TimelinerBridge();
            TimelinerReadResult read;

            BeginBusy("Reading TimeLiner…");
            try
            {
                read = bridge.ReadTasks(document);
            }
            catch (Exception ex)
            {
                Log.Error("Could not read TimeLiner", ex);
                return;
            }
            finally
            {
                EndBusy();
            }

            foreach (string warning in read.Warnings) Log.Warning(warning);

            if (read.Tasks.Count == 0)
            {
                ScheduleStatusText.Text = read.Warnings.Count > 0
                    ? "TimeLiner could not be read: " + string.Join("; ", read.Warnings.ToArray())
                    : "TimeLiner has no tasks in this model yet.";
                Log.Warning(ScheduleStatusText.Text);
                return;
            }

            _schedule = new ScheduleImportResult { FormatName = "TimeLiner", SourcePath = document.Title ?? "" };
            _schedule.Tasks.AddRange(read.Tasks);

            // The mapping dialog exists to reconcile unknown columns; TimeLiner's
            // fields are already the fields, so the mapping is filled in rather
            // than asked for. The rows are still populated to match, so the dialog
            // shows the truth if the user opens it anyway.
            ScheduleField[] fields =
            {
                ScheduleField.TaskId, ScheduleField.Name, ScheduleField.PlannedStart,
                ScheduleField.PlannedFinish, ScheduleField.ActualStart, ScheduleField.ActualFinish,
                ScheduleField.TaskType, ScheduleField.Wbs
            };

            foreach (ScheduleField field in fields)
            {
                _schedule.Columns.Add(new ScheduleColumn
                {
                    Index = _schedule.Columns.Count,
                    Header = ColumnAliases.DisplayName(field),
                    Field = field,
                    AutoDetected = true,
                    DetectionBasis = "read from TimeLiner"
                });
            }

            foreach (ScheduleTask task in read.Tasks)
                _schedule.Rows.Add(new[]
                {
                    task.TaskId, task.Name, FormatDate(task.PlannedStart), FormatDate(task.PlannedFinish),
                    FormatDate(task.ActualStart), FormatDate(task.ActualFinish), task.TaskType, task.Wbs
                });

            // TimeLiner's own attachments are decisions somebody already made;
            // treating them as manual matches stops the matcher from overruling them.
            foreach (KeyValuePair<string, string> entry in read.Attachments)
                _state.MatchOverrides[entry.Key] = entry.Value;

            Log.Info(read.Summary());
            _state.LastSchedulePath = "";
            AfterScheduleLoaded();

            ScheduleStatusText.Text = read.Summary() + "  " + ScheduleStatusText.Text;
        }

        private void OnImportSchedule(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Import a schedule",
                Filter = "All schedules|*.csv;*.txt;*.tsv;*.xer;*.xml|"
                       + "CSV / delimited (*.csv;*.txt;*.tsv)|*.csv;*.txt;*.tsv|"
                       + "Primavera P6 (*.xer;*.xml)|*.xer;*.xml|"
                       + "MS Project XML (*.xml)|*.xml|All files (*.*)|*.*"
            };

            if (!EnsureFourD()) return;
            if (dialog.ShowDialog(this) != true) return;

            try
            {
                BeginBusy("Importing schedule…");
                try
                {
                    _schedule = ScheduleImporter.Import(dialog.FileName);
                }
                finally
                {
                    EndBusy();
                }

                foreach (string warning in _schedule.Warnings) Log.Warning(warning);
                Log.Info("Imported " + Path.GetFileName(dialog.FileName) + " — " + _schedule.Summary());

                // Only interrupt when the file genuinely cannot be used as read.
                if (!ColumnMappingWindow.Prompt(this, _schedule, false))
                {
                    ScheduleStatusText.Text = "Import cancelled at the column mapping step.";
                    _schedule = null;
                    return;
                }

                _state.LastSchedulePath = dialog.FileName;
                AfterScheduleLoaded();
            }
            catch (Exception ex)
            {
                Log.Error("Could not import the schedule", ex);
                MessageBox.Show(this, "Could not import that schedule:\n\n" + ex.Message,
                    "NavEx", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void OnRemapColumns(object sender, RoutedEventArgs e)
        {
            if (_schedule == null) { Log.Warning("Read TimeLiner or import a schedule first."); return; }

            // Re-mapping re-parses the rows through the chosen columns. A TimeLiner
            // read has no rows to re-parse — its fields came from the API, not from
            // text — and running the dialog would replace the live tasks (and the
            // handles that let edits go back to the right ones) with copies.
            if (string.Equals(_schedule.FormatName, "TimeLiner", StringComparison.OrdinalIgnoreCase))
            {
                Log.Info("TimeLiner's fields are already mapped — there is nothing to re-map.");
                return;
            }

            if (ColumnMappingWindow.Prompt(this, _schedule, true))
                AfterScheduleLoaded();
        }

        private static string FormatDate(DateTime? value)
        {
            return value.HasValue ? value.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "";
        }

        private void AfterScheduleLoaded()
        {
            ScheduleStatusText.Text = _schedule.Summary();
            OnMatchTasks(null, null);
        }

        private void OnMatchTasks(object sender, RoutedEventArgs e)
        {
            if (!EnsureFourD()) return;

            Document document = NavApp.ActiveDocument;
            if (document == null) { Log.Warning("Open a model first."); return; }

            // No schedule yet is not a reason to refuse: the model's own TimeLiner
            // is right there, so read it rather than sending the user away.
            if (_schedule == null)
            {
                OnReadTimeliner(sender, e);
                if (_schedule == null)
                {
                    Log.Warning("Nothing to match — read TimeLiner or import a schedule first.");
                    return;
                }
                return;   // reading already ran the match
            }

            BeginBusy("Matching tasks to model sets…");
            try
            {
                _targets = _renamer.CollectTargets(document);

                double threshold;
                if (double.TryParse((MatchThresholdBox.Text ?? "").Trim(), NumberStyles.Float,
                                    CultureInfo.InvariantCulture, out threshold))
                    _matcher.AutoMatchThreshold = Math.Max(0, Math.Min(1, threshold));

                _matches = _matcher.MatchAll(_schedule.Tasks, _targets);

                if (RememberCheck.IsChecked == true)
                    TaskMatcher.ApplyRemembered(_matches, _state.MatchOverrides, _targets);

                Gantt.Tasks = _matches;
                Gantt.ZoomToFit(GanttScroll.ActualWidth);

                int matched = _matches.Count(m => m.IsAttached);
                int low = _matches.Count(m => m.IsAttached && m.ConfidenceBand == "low");

                ScheduleStatusText.Text = string.Format(CultureInfo.InvariantCulture,
                    "{0} — {1:N0} of {2:N0} tasks matched to {3:N0} search sets ({4:N0} low confidence, {5:N0} unmatched).",
                    _schedule.Summary(), matched, _matches.Count, _targets.Count, low, _matches.Count - matched);

                Log.Info(ScheduleStatusText.Text);
            }
            catch (Exception ex)
            {
                Log.Error("Matching failed", ex);
            }
            finally
            {
                EndBusy();
            }
        }

        /// <summary>
        /// Hands the export layer the task-to-set decisions this tab currently holds.
        ///
        /// This is the whole connection between the 4D tab and a Datasmith export: with
        /// it, every exported node carries its pixmy.task.* linkage and the export
        /// writes the schedule JSON sidecar Unrealistic4D reads to date the model.
        /// Without it the geometry still exports, just undated. Called immediately
        /// before the export runs so what ships is the latest state of the tab, not
        /// whatever was matched when it was last opened.
        /// </summary>
        private void SyncDatasmithTaskLinks()
        {
            _options.DatasmithTaskLinks.Clear();

            foreach (TaskMatch match in _matches)
            {
                if (!match.IsAttached || match.Task == null || !match.Task.IsValid) continue;

                string setName = match.Target.SetName;
                if (string.IsNullOrEmpty(setName)) continue;

                // A set attached to several tasks keeps the earliest — the task that
                // actually builds it, rather than a later one that touches it again.
                ScheduleTask existing;
                if (_options.DatasmithTaskLinks.TryGetValue(setName, out existing) &&
                    existing.PlannedStart <= match.Task.PlannedStart) continue;
                _options.DatasmithTaskLinks[setName] = match.Task;
            }
        }

        private void OnGanttZoomChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_fourDReady || Gantt == null) return;
            Gantt.Zoom = TagOf(ZoomCombo, GanttZoom.Week);
        }

        private void OnGanttFit(object sender, RoutedEventArgs e)
        {
            if (Gantt == null) return;
            Gantt.ZoomToFit(GanttScroll.ActualWidth);
        }

        private void OnGanttScrolled(object sender, ScrollChangedEventArgs e)
        {
            // Feeds the offset back so the date header can stay pinned.
            if (Gantt != null) Gantt.VerticalOffset = e.VerticalOffset;
        }

        private void OnGanttTaskClicked(int index)
        {
            if (index < 0 || index >= _matches.Count) return;
            ShowTaskDetail(_matches[index]);
        }

        private void ShowTaskDetail(TaskMatch match)
        {
            if (match == null || match.Task == null) return;

            ScheduleTask task = match.Task;
            DetailName.Text = task.Name;

            DetailDates.Text = string.Format(CultureInfo.CurrentCulture, "{0:d} → {1:d}   ({2:0.#} days)",
                task.PlannedStart, task.PlannedFinish, task.ComputedDurationDays)
                + (task.ActualStart.HasValue
                    ? string.Format(CultureInfo.CurrentCulture, "\nActual {0:d} → {1}",
                        task.ActualStart, task.ActualFinish.HasValue
                            ? task.ActualFinish.Value.ToString("d", CultureInfo.CurrentCulture) : "in progress")
                    : "");

            DetailDescription.Text = string.IsNullOrWhiteSpace(task.Description) ? "" : task.Description;

            FillEditBoxes(task);

            DetailMatch.Text = match.Target == null ? "(not attached)" : match.Target.SetName;
            DetailReason.Text = string.Format(CultureInfo.InvariantCulture, "{0} · score {1:0.00} · {2}",
                match.ConfidenceBand, match.Score, match.Reason);

            AlternativesCombo.SelectionChanged -= OnAlternativeChosen;
            AlternativesCombo.Items.Clear();
            AlternativesCombo.Items.Add(new ComboBoxItem { Content = "(not attached)", Tag = null });

            foreach (MatchTarget alternative in match.Alternatives)
                AlternativesCombo.Items.Add(new ComboBoxItem { Content = alternative.SetName, Tag = alternative });

            // Any set can be chosen, not only the scored candidates.
            foreach (MatchTarget target in _targets)
                if (!match.Alternatives.Contains(target))
                    AlternativesCombo.Items.Add(new ComboBoxItem { Content = target.SetName, Tag = target });

            AlternativesCombo.SelectedIndex = 0;
            for (int i = 1; i < AlternativesCombo.Items.Count; i++)
            {
                var item = (ComboBoxItem)AlternativesCombo.Items[i];
                if (ReferenceEquals(item.Tag, match.Target)) { AlternativesCombo.SelectedIndex = i; break; }
            }

            AlternativesCombo.SelectionChanged += OnAlternativeChosen;
        }

        private TaskMatch SelectedMatch()
        {
            int index = Gantt == null ? -1 : Gantt.SelectedIndex;
            if (index < 0 || index >= _matches.Count) return null;
            return _matches[index];
        }

        // ── Editing a task in place ──────────────────────────────────────────

        private void FillEditBoxes(ScheduleTask task)
        {
            EditNameBox.Text = task.Name ?? "";
            EditStartBox.Text = task.PlannedStart.HasValue
                ? task.PlannedStart.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "";
            EditFinishBox.Text = task.PlannedFinish.HasValue
                ? task.PlannedFinish.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "";
            EditTypeCombo.Text = task.NormalizedTaskType();
            EditStatusText.Text = "";
        }

        /// <summary>
        /// Applies the edited fields to the selected task. Nothing reaches the
        /// model here — the change lands in the plugin's copy and only becomes real
        /// on the next write to TimeLiner, which is the same preview-then-commit
        /// shape the renamer uses.
        /// </summary>
        private void OnApplyTaskEdit(object sender, RoutedEventArgs e)
        {
            TaskMatch match = SelectedMatch();
            if (match == null || match.Task == null) { EditStatusText.Text = "Select a task first."; return; }

            string name = (EditNameBox.Text ?? "").Trim();
            if (name.Length == 0) { EditStatusText.Text = "A task needs a name."; return; }

            DateTime start, finish;
            if (!TryReadDate(EditStartBox.Text, out start)) { EditStatusText.Text = "Start is not a date."; return; }
            if (!TryReadDate(EditFinishBox.Text, out finish)) { EditStatusText.Text = "Finish is not a date."; return; }

            if (finish < start)
            {
                EditStatusText.Text = "Finish is before start — the bar would render backwards.";
                return;
            }

            ScheduleTask task = match.Task;
            task.Name = name;
            task.PlannedStart = start;
            task.PlannedFinish = finish;
            task.DurationDays = null;               // recomputed from the new dates
            task.TaskType = (EditTypeCombo.Text ?? "").Trim();

            EditStatusText.Text = "Edited — write to TimeLiner to commit.";
            Gantt.Tasks = _matches;                 // re-lays out the bar
            Gantt.InvalidateVisual();
            ShowTaskDetail(match);
        }

        private void OnRevertTaskEdit(object sender, RoutedEventArgs e)
        {
            TaskMatch match = SelectedMatch();
            if (match == null || match.Task == null) return;
            FillEditBoxes(match.Task);
        }

        private static bool TryReadDate(string text, out DateTime value)
        {
            value = DateTime.MinValue;
            text = (text ?? "").Trim();
            if (text.Length == 0) return false;

            return DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.None, out value)
                || DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
        }

        private void OnAlternativeChosen(object sender, SelectionChangedEventArgs e)
        {
            TaskMatch match = SelectedMatch();
            if (match == null) return;

            var item = AlternativesCombo.SelectedItem as ComboBoxItem;
            var target = item == null ? null : item.Tag as MatchTarget;

            match.Target = target;
            match.State = target == null ? MatchState.Excluded : MatchState.Manual;
            match.Score = target == null ? 0 : 1.0;
            match.Reason = target == null ? "excluded by user" : "set manually";

            RememberDecision(match);
            Gantt.InvalidateVisual();
            ShowTaskDetail(match);
        }

        private void OnConfirmMatch(object sender, RoutedEventArgs e)
        {
            TaskMatch match = SelectedMatch();
            if (match == null || match.Target == null) return;

            match.State = MatchState.Confirmed;
            match.Reason = "confirmed";
            RememberDecision(match);
            Gantt.InvalidateVisual();
            ShowTaskDetail(match);
        }

        private void OnExcludeMatch(object sender, RoutedEventArgs e)
        {
            TaskMatch match = SelectedMatch();
            if (match == null) return;

            match.Target = null;
            match.State = MatchState.Excluded;
            match.Reason = "excluded by user";
            RememberDecision(match);
            Gantt.InvalidateVisual();
            ShowTaskDetail(match);
        }

        private void RememberDecision(TaskMatch match)
        {
            if (RememberCheck.IsChecked != true) return;
            _state.MatchOverrides[match.Task.StableKey] =
                match.Target == null ? "" : match.Target.SetName;
        }

        // ── TimeLiner ────────────────────────────────────────────────────────

        private void OnSyncTimeliner(object sender, RoutedEventArgs e)
        {
            if (!EnsureFourD()) return;
            if (_matches.Count == 0) { Log.Warning("Read TimeLiner or import a schedule first."); return; }

            Document document = NavApp.ActiveDocument;
            if (document == null) return;

            bool replace = string.Equals(TagText(WriteModeCombo), "Replace", StringComparison.OrdinalIgnoreCase);

            // Replace deletes work that is not NavEx's to delete, so it asks first
            // and says exactly how many tasks it is about to remove.
            if (replace)
            {
                int writable = _matches.Count(m => m.Task != null && m.Task.IsValid);

                MessageBoxResult confirm = MessageBox.Show(this, string.Format(CultureInfo.InvariantCulture,
                    "Replace deletes every task currently in this model's TimeLiner, then writes the "
                    + "{0:N0} task(s) listed here that have dates.\n\n"
                    + "• Anything in TimeLiner that is not in this list — tasks and attachments added outside "
                    + "NavEx included — is lost.\n"
                    + "• The result is a flat list: WBS grouping in the current TimeLiner is not recreated"
                    + "{1}.\n\n"
                    + "Update mode does neither, and is usually what you want. Continue with Replace?",
                    writable,
                    _matches.Count == writable
                        ? ""
                        : string.Format(CultureInfo.InvariantCulture,
                            ", and the {0:N0} row(s) here without dates are dropped",
                            _matches.Count - writable)),
                    "NavEx — Replace TimeLiner", MessageBoxButton.OKCancel, MessageBoxImage.Warning);

                if (confirm != MessageBoxResult.OK) return;
            }

            var bridge = new TimelinerBridge();
            if (!bridge.TryBind(document))
            {
                Log.Warning("TimeLiner API unavailable: " + bridge.BindingError);

                MessageBoxResult answer = MessageBox.Show(this,
                    "NavEx could not reach the TimeLiner API in this Navisworks build:\n\n"
                    + bridge.BindingError
                    + "\n\nWrite a TimeLiner-importable CSV instead?",
                    "NavEx", MessageBoxButton.YesNo, MessageBoxImage.Warning);

                if (answer == MessageBoxResult.Yes) OnExportTimelinerCsv(sender, e);
                return;
            }

            BeginBusy(replace ? "Replacing TimeLiner tasks…" : "Writing tasks to TimeLiner…");
            TimelinerSyncResult result;
            try
            {
                result = bridge.Sync(document, _matches, AttachCheck.IsChecked == true, replace, _progress);
            }
            catch (OperationCanceledException)
            {
                Log.Warning("TimeLiner sync cancelled.");
                return;
            }
            finally
            {
                EndBusy();
            }

            foreach (string error in result.Errors.Take(20)) Log.Error(error);
            Log.Success(result.Summary());
            ScheduleStatusText.Text = result.Summary();

            MessageBox.Show(this, result.Summary(), "NavEx — TimeLiner",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void OnExportTimelinerCsv(object sender, RoutedEventArgs e)
        {
            if (_matches.Count == 0) { Log.Warning("Import and match a schedule first."); return; }

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Export TimeLiner CSV",
                Filter = "CSV (*.csv)|*.csv",
                DefaultExt = ".csv",
                FileName = "navex-timeliner.csv"
            };

            if (dialog.ShowDialog(this) != true) return;

            try
            {
                TimelinerBridge.WriteCsv(_matches, dialog.FileName);
                Log.Success("Wrote " + Path.GetFileName(dialog.FileName)
                            + " — import it in TimeLiner via Data Sources.");
            }
            catch (Exception ex)
            {
                Log.Error("Could not write the CSV", ex);
            }
        }

        // ── Naming ───────────────────────────────────────────────────────────

        private void OnClassifyModels(object sender, RoutedEventArgs e)
        {
            if (!EnsureFourD()) return;

            ModelListBox.Items.Clear();

            Document document = NavApp.ActiveDocument;
            if (document == null)
            {
                ModelStatusText.Text = "No model is open. Open one in Navisworks, then press this again.";
                return;
            }

            try
            {
                List<ModelClassification> models = _renamer.ClassifyLoadedModels(document);

                foreach (ModelClassification model in models)
                    ModelListBox.Items.Add(model.Line);

                if (models.Count == 0)
                {
                    ModelStatusText.Text = "This document has no model files in it.";
                    Log.Info(ModelStatusText.Text);
                    return;
                }

                int unresolved = models.Count(m => m.Identity == null || !m.Identity.IsResolved);
                ModelStatusText.Text = string.Format(CultureInfo.InvariantCulture,
                    "{0:N0} model file(s); {1:N0} could not be fully resolved — add an identifier for those.",
                    models.Count, unresolved);
                Log.Info(ModelStatusText.Text);
            }
            catch (Exception ex)
            {
                // This panel used to have no handler at all, so anything the model
                // list threw surfaced as a Navisworks-level crash.
                ModelStatusText.Text = "Could not classify the loaded models: " + ex.Message;
                Log.Error("Could not classify the loaded models", ex);
            }
        }

        private void OnProposeRenames(object sender, RoutedEventArgs e)
        {
            if (!EnsureFourD()) return;

            Document document = NavApp.ActiveDocument;
            if (document == null) { Log.Warning("Open a model first."); return; }

            BeginBusy("Classifying search sets…");
            try
            {
                _targets = _renamer.CollectTargets(document);
                _proposals = _renamer.Propose(_targets, IncludeDescriptionCheck.IsChecked == true);

                ProposalListBox.ItemsSource = null;
                ProposalListBox.ItemsSource = _proposals;

                int changes = _proposals.Count(p => p.IsChange && p.Apply);
                int blocked = _proposals.Count(p => !p.Identity.IsResolved);

                RenameStatusText.Text = string.Format(CultureInfo.InvariantCulture,
                    "{0:N0} set(s): {1:N0} to rename, {2:N0} already correct, {3:N0} unresolved.",
                    _proposals.Count, changes,
                    _proposals.Count(p => !p.IsChange), blocked);

                Log.Info(RenameStatusText.Text);
            }
            catch (Exception ex)
            {
                Log.Error("Could not build rename proposals", ex);
            }
            finally
            {
                EndBusy();
            }
        }

        private void OnApplyRenames(object sender, RoutedEventArgs e)
        {
            if (!EnsureFourD()) return;
            if (_proposals.Count == 0) { Log.Warning("Press Propose first."); return; }

            Document document = NavApp.ActiveDocument;
            if (document == null) return;

            int pending = _proposals.Count(p => p.Apply && p.IsChange);
            if (pending == 0) { Log.Info("Nothing ticked to rename."); return; }

            MessageBoxResult answer = MessageBox.Show(this,
                string.Format(CultureInfo.InvariantCulture,
                    "Rename {0:N0} search set(s)?\n\nClash tests, viewpoints and reports that reference these "
                    + "sets by name will follow the new names.", pending),
                "NavEx", MessageBoxButton.OKCancel, MessageBoxImage.Question);

            if (answer != MessageBoxResult.OK) return;

            BeginBusy("Renaming search sets…");
            try
            {
                int renamed = _renamer.Apply(document, _proposals, _progress);
                RenameStatusText.Text = string.Format(CultureInfo.InvariantCulture,
                    "Renamed {0:N0} set(s).", renamed);
                OnProposeRenames(sender, e);
                RefreshTree();
            }
            catch (OperationCanceledException)
            {
                Log.Warning("Rename cancelled.");
            }
            catch (Exception ex)
            {
                Log.Error("Rename failed", ex);
            }
            finally
            {
                EndBusy();
            }
        }

        // ── Grouping ─────────────────────────────────────────────────────────

        private void OnProposeGrouping(object sender, RoutedEventArgs e)
        {
            if (!EnsureFourD()) return;

            Document document = NavApp.ActiveDocument;
            if (document == null) { Log.Warning("Open a model first."); return; }

            BeginBusy("Working out where each set belongs…");
            try
            {
                _targets = _renamer.CollectTargets(document);
                _groupProposals = _renamer.ProposeGrouping(_targets, TagOf(GroupByCombo, GroupBy.Discipline));

                GroupListBox.ItemsSource = null;
                GroupListBox.ItemsSource = _groupProposals;

                int moves = _groupProposals.Count(p => p.IsChange);
                int unsorted = _groupProposals.Count(p => p.TargetFolder == SetRenamer.UnsortedFolder);

                GroupStatusText.Text = string.Format(CultureInfo.InvariantCulture,
                    "{0:N0} set(s): {1:N0} to move, {2:N0} already filed, {3:N0} with nothing to file them by.",
                    _groupProposals.Count, moves, _groupProposals.Count - moves, unsorted);
                Log.Info(GroupStatusText.Text);
            }
            catch (Exception ex)
            {
                Log.Error("Could not build the grouping proposal", ex);
            }
            finally
            {
                EndBusy();
            }
        }

        private void OnApplyGrouping(object sender, RoutedEventArgs e)
        {
            if (!EnsureFourD()) return;
            if (_groupProposals.Count == 0) { Log.Warning("Press Propose first."); return; }

            Document document = NavApp.ActiveDocument;
            if (document == null) return;

            int pending = _groupProposals.Count(p => p.Apply && p.IsChange);
            if (pending == 0) { Log.Info("Nothing ticked to move."); return; }

            MessageBoxResult answer = MessageBox.Show(this,
                string.Format(CultureInfo.InvariantCulture,
                    "Move {0:N0} search set(s) into folders?\n\nThe sets keep their names, so clash tests and "
                    + "viewpoints that reference them are unaffected.", pending),
                "NavEx", MessageBoxButton.OKCancel, MessageBoxImage.Question);

            if (answer != MessageBoxResult.OK) return;

            BeginBusy("Filing search sets…");
            try
            {
                int moved = _renamer.ApplyGrouping(document, _groupProposals, _progress);
                GroupStatusText.Text = string.Format(CultureInfo.InvariantCulture, "Moved {0:N0} set(s).", moved);
                OnProposeGrouping(sender, e);
                RefreshTree();
            }
            catch (OperationCanceledException)
            {
                Log.Warning("Grouping cancelled.");
            }
            catch (Exception ex)
            {
                Log.Error("Grouping failed", ex);
            }
            finally
            {
                EndBusy();
            }
        }

        // ── Identifiers ──────────────────────────────────────────────────────

        private void RefreshIdentifierRows()
        {
            _ruleRows = _profile.Identifiers.Rules.Select(r => new RuleRow(r)).ToList();
            _customActivityRows = _profile.Identifiers.Activities.Select(a => new CustomActivityRow(a)).ToList();

            RuleListBox.ItemsSource = null;
            RuleListBox.ItemsSource = _ruleRows;
            CustomActivityListBox.ItemsSource = null;
            CustomActivityListBox.ItemsSource = _customActivityRows;
        }

        private void OnAddRule(object sender, RoutedEventArgs e)
        {
            if (!EnsureFourD()) return;
            _ruleRows.Add(new RuleRow());
            RuleListBox.ItemsSource = null;
            RuleListBox.ItemsSource = _ruleRows;
            RuleListBox.SelectedIndex = _ruleRows.Count - 1;
        }

        private void OnRemoveRule(object sender, RoutedEventArgs e)
        {
            var row = RuleListBox.SelectedItem as RuleRow;
            if (row == null) { IdentifierStatusText.Text = "Select a rule to remove."; return; }

            _ruleRows.Remove(row);
            RuleListBox.ItemsSource = null;
            RuleListBox.ItemsSource = _ruleRows;
        }

        private void OnAddCustomActivity(object sender, RoutedEventArgs e)
        {
            if (!EnsureFourD()) return;
            _customActivityRows.Add(new CustomActivityRow());
            CustomActivityListBox.ItemsSource = null;
            CustomActivityListBox.ItemsSource = _customActivityRows;
            CustomActivityListBox.SelectedIndex = _customActivityRows.Count - 1;
        }

        private void OnRemoveCustomActivity(object sender, RoutedEventArgs e)
        {
            var row = CustomActivityListBox.SelectedItem as CustomActivityRow;
            if (row == null) { IdentifierStatusText.Text = "Select an activity to remove."; return; }

            _customActivityRows.Remove(row);
            CustomActivityListBox.ItemsSource = null;
            CustomActivityListBox.ItemsSource = _customActivityRows;
        }

        /// <summary>
        /// Reads the two grids back into the library and returns what it could not
        /// use.
        ///
        /// Rows that cannot be used are reported and skipped rather than silently
        /// dropped — a rule that quietly does nothing is worse than no rule, because
        /// the user goes on believing the name is covered.
        /// </summary>
        private List<string> CommitIdentifierRows()
        {
            var problems = new List<string>();
            IdentifierLibrary library = _profile.Identifiers;

            library.Rules.Clear();
            foreach (RuleRow row in _ruleRows)
            {
                if (string.IsNullOrWhiteSpace(row.Pattern)) continue;

                IdentifierRule rule = row.ToRule();
                string problem = rule.Validate();
                if (problem != null) { problems.Add("'" + rule.Pattern + "': " + problem); continue; }

                library.Rules.Add(rule);
            }

            library.Activities.Clear();
            foreach (CustomActivityRow row in _customActivityRows)
            {
                if (string.IsNullOrWhiteSpace(row.Code) && string.IsNullOrWhiteSpace(row.DisplayName)) continue;

                string problem = row.Validate();
                if (problem != null) { problems.Add("activity " + problem); continue; }

                library.Activities.Add(row.ToActivity());
            }

            return problems;
        }

        /// <summary>
        /// Commits the grids, saves the library, and re-runs anything already on
        /// screen so the effect of a rule is visible immediately rather than on the
        /// next model open.
        /// </summary>
        private void OnApplyIdentifiers(object sender, RoutedEventArgs e)
        {
            if (!EnsureFourD()) return;

            List<string> problems = CommitIdentifierRows();
            IdentifierLibrary library = _profile.Identifiers;
            bool saved = library.Save();

            IdentifierStatusText.Text = string.Format(CultureInfo.InvariantCulture,
                "{0:N0} rule(s), {1:N0} custom activity(ies){2}{3}",
                library.Rules.Count, library.Activities.Count,
                saved ? " — saved to " + IdentifierLibrary.DefaultPath : " — could not be saved",
                problems.Count == 0 ? "" : "\nSkipped: " + string.Join("; ", problems.ToArray()));

            foreach (string problem in problems) Log.Warning("Identifier skipped — " + problem);
            Log.Info(string.Format(CultureInfo.InvariantCulture,
                "Identifier library applied: {0:N0} rule(s), {1:N0} custom activity(ies).",
                library.Rules.Count, library.Activities.Count));

            RefreshActivityRows();
            RefreshTestName();

            if (_proposals.Count > 0) OnProposeRenames(sender, e);
            if (_groupProposals.Count > 0) OnProposeGrouping(sender, e);
            if (ModelListBox.Items.Count > 0) OnClassifyModels(sender, e);
        }

        private void OnImportIdentifiers(object sender, RoutedEventArgs e)
        {
            if (!EnsureFourD()) return;

            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Import identifiers",
                Filter = "NavEx identifiers (*.txt)|*.txt|All files (*.*)|*.*"
            };
            if (dialog.ShowDialog(this) != true) return;

            IdentifierLibrary imported = IdentifierLibrary.Load(dialog.FileName);

            // Merged, not replaced: importing a colleague's file should add their
            // vocabulary to yours, not throw yours away.
            foreach (IdentifierRule rule in imported.Rules) _profile.Identifiers.Rules.Add(rule);
            foreach (Activity activity in imported.Activities) _profile.Identifiers.Activities.Add(activity);
            foreach (Discipline discipline in imported.Disciplines) _profile.Identifiers.Disciplines.Add(discipline);

            RefreshIdentifierRows();
            RefreshActivityRows();

            IdentifierStatusText.Text = string.Format(CultureInfo.InvariantCulture,
                "Imported {0:N0} rule(s) and {1:N0} activity(ies) from {2}. Press Apply to keep them.",
                imported.Rules.Count, imported.Activities.Count, Path.GetFileName(dialog.FileName));

            foreach (string warning in imported.Warnings) Log.Warning(warning);
        }

        private void OnExportIdentifiers(object sender, RoutedEventArgs e)
        {
            if (!EnsureFourD()) return;

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Export identifiers",
                Filter = "NavEx identifiers (*.txt)|*.txt",
                DefaultExt = ".txt",
                FileName = "navex-identifiers.txt"
            };
            if (dialog.ShowDialog(this) != true) return;

            // Export what is on screen, not what was last applied.
            List<string> problems = CommitIdentifierRows();
            foreach (string problem in problems) Log.Warning("Not exported — " + problem);

            if (_profile.Identifiers.Save(dialog.FileName))
            {
                IdentifierStatusText.Text = "Exported to " + dialog.FileName;
                Log.Success("Wrote " + Path.GetFileName(dialog.FileName) + ".");
            }
        }

        private void OnLoadSampleIdentifiers(object sender, RoutedEventArgs e)
        {
            if (!EnsureFourD()) return;

            var have = new HashSet<string>(_ruleRows.Select(r => (r.Pattern ?? "").Trim()),
                                           StringComparer.OrdinalIgnoreCase);

            int added = 0;
            foreach (IdentifierRule rule in IdentifierLibrary.Sample().Rules)
            {
                if (!have.Add(rule.Pattern)) continue;
                _ruleRows.Add(new RuleRow(rule));
                added++;
            }

            RuleListBox.ItemsSource = null;
            RuleListBox.ItemsSource = _ruleRows;
            IdentifierStatusText.Text = added == 0
                ? "The example rules are already in the table."
                : "Added " + added.ToString("N0", CultureInfo.InvariantCulture)
                  + " example rule(s). Edit them, then press Apply.";
        }

        private void OnTestNameChanged(object sender, TextChangedEventArgs e)
        {
            if (!_fourDReady) return;
            RefreshTestName();
        }

        /// <summary>
        /// The feedback loop that makes the rule table usable: type a real set name
        /// and see the code it would get, and which rule or alias produced it.
        /// </summary>
        private void RefreshTestName()
        {
            if (TestNameBox == null || TestResultText == null) return;

            string name = (TestNameBox.Text ?? "").Trim();
            if (name.Length == 0)
            {
                TestResultText.Text = "—";
                TestBasisText.Text = "";
                return;
            }

            try
            {
                FourDName identity = _classifier.Classify(name);
                TestResultText.Text = identity.Render(true);
                TestBasisText.Text = string.Format(CultureInfo.InvariantCulture,
                    "{0} · confidence {1:0.00}{2}",
                    identity.Basis, identity.Confidence,
                    identity.IsResolved ? "" : " · not renameable until it has both a discipline and an activity");
            }
            catch (Exception ex)
            {
                TestResultText.Text = "—";
                TestBasisText.Text = ex.Message;
            }
        }

        // ── Sequencing table ─────────────────────────────────────────────────

        private void OnRecomputeSequence(object sender, RoutedEventArgs e)
        {
            if (!EnsureFourD()) return;

            _profile.LagOverrides.Clear();
            _profile.OrderOverrides.Clear();
            _state.LagOverrides.Clear();
            _state.OrderOverrides.Clear();

            foreach (ActivityRow row in _activityRows)
            {
                // The project's own activities are editable here too, so the
                // definition has to come from the profile rather than the built-in
                // table — otherwise a custom row silently ignores its own edits.
                Activity original = _profile.FindDefinition(row.Code);
                if (original == null) continue;

                if (row.Lag != original.LagFloors)
                {
                    _profile.LagOverrides[row.Code] = row.Lag;
                    _state.LagOverrides[row.Code] = row.Lag;
                }
                if (row.Order != original.CycleOrder)
                {
                    _profile.OrderOverrides[row.Code] = row.Order;
                    _state.OrderOverrides[row.Code] = row.Order;
                }
            }

            Log.Info(string.Format(CultureInfo.InvariantCulture,
                "Sequencing updated: {0:N0} lag override(s), {1:N0} order override(s).",
                _profile.LagOverrides.Count, _profile.OrderOverrides.Count));

            // Any code already on screen is now stale.
            if (_proposals.Count > 0) OnProposeRenames(sender, e);
            if (ModelListBox.Items.Count > 0) OnClassifyModels(sender, e);
        }

        private void OnResetSequence(object sender, RoutedEventArgs e)
        {
            if (!EnsureFourD()) return;

            _profile.LagOverrides.Clear();
            _profile.OrderOverrides.Clear();
            _state.LagOverrides.Clear();
            _state.OrderOverrides.Clear();
            RefreshActivityRows();
            Log.Info("Sequencing table reset to defaults.");
        }

        private void SaveFourDState()
        {
            if (_state == null) return;

            // The library is global rather than per-document, so it is saved even
            // when the document state below is not worth writing.
            if (_profile != null && _profile.Identifiers != null && !_profile.Identifiers.IsEmpty)
                _profile.Identifiers.Save();

            if (RememberCheck != null && RememberCheck.IsChecked == true && _matches.Count > 0)
            {
                foreach (KeyValuePair<string, string> entry in TaskMatcher.Remember(_matches))
                    _state.MatchOverrides[entry.Key] = entry.Value;
            }

            foreach (KeyValuePair<string, string> entry in _classifier.DisciplineOverrides)
                _state.DisciplineOverrides[entry.Key] = entry.Value;
            foreach (KeyValuePair<string, string> entry in _classifier.ActivityOverrides)
                _state.ActivityOverrides[entry.Key] = entry.Value;

            Document document = NavApp.ActiveDocument;
            _state.Save(document == null ? "" : document.Title);
        }
    }
}
