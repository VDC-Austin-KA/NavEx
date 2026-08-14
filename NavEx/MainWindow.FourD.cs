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
        private List<ActivityRow> _activityRows = new List<ActivityRow>();
        private bool _fourDReady;

        private void InitializeFourD()
        {
            Document document = NavApp.ActiveDocument;

            _profile = new SequenceProfile();
            _classifier = new NameClassifier(_profile);
            _state = FourDState.Load(document == null ? "" : document.Title);
            _state.ApplyTo(_classifier, _profile);

            _renamer = new SetRenamer(_classifier);
            _matcher = new TaskMatcher(_classifier);

            RefreshActivityRows();
            _fourDReady = true;
        }

        private void RefreshActivityRows()
        {
            _activityRows = _profile.AllResolved().Select(a => new ActivityRow(a)).ToList();
            ActivityListBox.ItemsSource = null;
            ActivityListBox.ItemsSource = _activityRows;
        }

        // ── Schedule ─────────────────────────────────────────────────────────

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
            if (_schedule == null) { Log.Warning("Import a schedule first."); return; }

            if (ColumnMappingWindow.Prompt(this, _schedule, true))
                AfterScheduleLoaded();
        }

        private void AfterScheduleLoaded()
        {
            ScheduleStatusText.Text = _schedule.Summary();
            OnMatchTasks(null, null);
        }

        private void OnMatchTasks(object sender, RoutedEventArgs e)
        {
            if (_schedule == null) { Log.Warning("Import a schedule first."); return; }

            Document document = NavApp.ActiveDocument;
            if (document == null) return;

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
            if (_matches.Count == 0) { Log.Warning("Import and match a schedule first."); return; }

            Document document = NavApp.ActiveDocument;
            if (document == null) return;

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

            BeginBusy("Writing tasks to TimeLiner…");
            TimelinerSyncResult result;
            try
            {
                result = bridge.Sync(document, _matches, AttachCheck.IsChecked == true, _progress);
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
            Document document = NavApp.ActiveDocument;
            if (document == null) return;

            List<FourDName> names = _renamer.ClassifyLoadedModels(document);

            ModelListBox.Items.Clear();
            foreach (FourDName name in names)
            {
                ModelListBox.Items.Add(string.Format(CultureInfo.InvariantCulture,
                    "{0}   {1}{2}",
                    name.Render(false).PadRight(28),
                    name.Description,
                    name.IsResolved ? "" : "   [" + name.Basis + "]"));
            }

            int unresolved = names.Count(n => !n.IsResolved);
            Log.Info(string.Format(CultureInfo.InvariantCulture,
                "Classified {0:N0} loaded model(s); {1:N0} could not be fully resolved.",
                names.Count, unresolved));
        }

        private void OnProposeRenames(object sender, RoutedEventArgs e)
        {
            Document document = NavApp.ActiveDocument;
            if (document == null) return;

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

        // ── Sequencing table ─────────────────────────────────────────────────

        private void OnRecomputeSequence(object sender, RoutedEventArgs e)
        {
            _profile.LagOverrides.Clear();
            _profile.OrderOverrides.Clear();
            _state.LagOverrides.Clear();
            _state.OrderOverrides.Clear();

            foreach (ActivityRow row in _activityRows)
            {
                Activity original = SequenceModel.FindActivity(row.Code);
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
