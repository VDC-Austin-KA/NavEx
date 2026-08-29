using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Autodesk.Navisworks.Api;
using NavEx.Core;
using NavEx.Core.Exporters;
using NavApp = Autodesk.Navisworks.Api.Application;

namespace NavEx
{
    public partial class MainWindow : Window
    {
        private readonly ExportOptions _options;
        private ObservableCollection<SetNode> _allNodes = new ObservableCollection<SetNode>();
        private ProgressContext _progress;
        private bool _initialized;
        private bool _isExporting;
        private DateTime _lastPump = DateTime.MinValue;
        private string _lastOutputFolder = "";

        public MainWindow()
        {
            InitializeComponent();

            _options = SettingsStore.Load();
            Log.Sink = AppendLog;

            Loaded += OnWindowLoaded;
            Closing += OnWindowClosing;
        }

        private void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            Document document = NavApp.ActiveDocument;

            if (document == null || document.Models.Count == 0)
            {
                DocumentText.Text = "No model loaded";
                StatusText.Text = "Open a model in Navisworks, then reopen NavEx.";
                ExportButton.IsEnabled = false;
                EstimateButton.IsEnabled = false;
                return;
            }

            DocumentText.Text = document.Title + "   ·   " + document.Models.Count + " model file(s)   ·   units: " + document.Units;

            if (string.IsNullOrWhiteSpace(_options.OutputFolder))
            {
                _options.OutputFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "NavEx Exports");
            }

            ApplyOptionsToUi();
            RefreshPresets();
            RefreshTree();

            InitializeFourD();
            Gantt.TaskClicked += OnGanttTaskClicked;

            _initialized = true;
            Log.Info("NavEx " + PluginInfo.Version + " ready.");
        }

        private void OnWindowClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_isExporting)
            {
                MessageBoxResult answer = MessageBox.Show(
                    "An export is still running. Cancel it and close?",
                    "NavEx", MessageBoxButton.YesNo, MessageBoxImage.Question);

                if (answer != MessageBoxResult.Yes) { e.Cancel = true; return; }
                if (_progress != null) _progress.CancelRequested = true;
            }

            if (_initialized)
            {
                ReadOptionsFromUi();
                SettingsStore.Save(_options);
                SaveFourDState();
            }

            Log.Sink = null;
        }

        // ── Tree ─────────────────────────────────────────────────────────────

        private void RefreshTree()
        {
            Document document = NavApp.ActiveDocument;
            if (document == null) return;

            var checkedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (SetNode root in _allNodes)
                foreach (SetNode node in root.DescendantsAndSelf)
                    if (node.IsChecked) checkedNames.Add(node.Name);

            _allNodes = SelectionSetTree.Build(document);

            // Restore ticks across a refresh so a rebuilt tree doesn't discard a
            // carefully assembled batch.
            if (checkedNames.Count > 0)
            {
                foreach (SetNode root in _allNodes)
                    foreach (SetNode node in root.DescendantsAndSelf)
                        if (checkedNames.Contains(node.Name)) node.SetCheckedQuiet(true);
            }

            foreach (SetNode root in _allNodes)
                foreach (SetNode node in root.DescendantsAndSelf)
                    node.PropertyChanged += OnNodeChanged;

            ApplyFilter();
            UpdateSummary();
        }

        private void OnNodeChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == "IsChecked") UpdateSummary();
        }

        private void ApplyFilter()
        {
            string filter = FilterBox == null ? "" : (FilterBox.Text ?? "").Trim();

            if (filter.Length == 0)
            {
                SetTree.ItemsSource = _allNodes;
                return;
            }

            var matches = new ObservableCollection<SetNode>();
            foreach (SetNode root in _allNodes)
            {
                SetNode pruned = Prune(root, filter);
                if (pruned != null) matches.Add(pruned);
            }
            SetTree.ItemsSource = matches;
        }

        /// <summary>
        /// Filtered copies share the underlying SavedItem and check state with the
        /// originals, so ticking a filtered row ticks the real node.
        /// </summary>
        private static SetNode Prune(SetNode node, string filter)
        {
            bool selfMatches = (node.Name ?? "").IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;

            var keptChildren = new List<SetNode>();
            foreach (SetNode child in node.Children)
            {
                SetNode pruned = Prune(child, filter);
                if (pruned != null) keptChildren.Add(pruned);
            }

            if (!selfMatches && keptChildren.Count == 0) return null;
            if (selfMatches && keptChildren.Count == node.Children.Count) return node;

            var copy = new SetNode
            {
                Name = node.Name,
                IsFolder = node.IsFolder,
                IsCurrentSelection = node.IsCurrentSelection,
                Item = node.Item,
                Detail = node.Detail,
                IsExpanded = true
            };
            copy.SetCheckedQuiet(node.IsChecked);
            copy.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == "IsChecked") node.IsChecked = copy.IsChecked;
            };

            foreach (SetNode child in keptChildren) copy.Children.Add(child);
            return copy;
        }

        private void UpdateSummary()
        {
            int sets = 0;
            foreach (SetNode root in _allNodes)
                foreach (SetNode node in root.DescendantsAndSelf)
                    if (node.IsChecked && !node.IsFolder) sets++;

            SummaryText.Text = sets == 0
                ? "Nothing selected yet."
                : sets + (sets == 1 ? " set selected." : " sets selected.");

            ExportButton.IsEnabled = sets > 0 && !_isExporting;
            EstimateText.Text = "";
        }

        // ── Options plumbing ─────────────────────────────────────────────────

        private void ApplyOptionsToUi()
        {
            OutputFolderBox.Text = _options.OutputFolder;
            SelectByTag(FormatCombo, _options.Format.ToString());
            SelectByTag(BatchCombo, _options.Batch.ToString());
            TemplateBox.Text = _options.FileNameTemplate;
            OverwriteCheck.IsChecked = _options.OverwriteExisting;

            SelectByTag(UnitsCombo, _options.TargetUnits.ToString());
            SelectByTag(OriginCombo, _options.Origin.ToString());
            OriginXBox.Text = Fmt(_options.CustomOrigin.X);
            OriginYBox.Text = Fmt(_options.CustomOrigin.Y);
            OriginZBox.Text = Fmt(_options.CustomOrigin.Z);
            CustomOriginPanel.Visibility = _options.Origin == OriginMode.Custom ? Visibility.Visible : Visibility.Collapsed;
            YUpCheck.IsChecked = _options.ConvertToYUp;

            SelectByTag(GroupingCombo, _options.Grouping.ToString());
            WeldCheck.IsChecked = _options.WeldVertices;
            WeldToleranceBox.Text = Fmt(_options.WeldTolerance);
            NormalsCheck.IsChecked = _options.IncludeNormals;
            VertexColorsCheck.IsChecked = _options.IncludeVertexColors;
            LinesCheck.IsChecked = _options.IncludeLines;
            SkipHiddenCheck.IsChecked = _options.SkipHidden;
            MinEdgeBox.Text = Fmt(_options.MinTriangleEdge);

            MaterialsCheck.IsChecked = _options.EmitMaterials;
            TransparencyCheck.IsChecked = _options.PreserveTransparency;
            DoubleSidedCheck.IsChecked = _options.DoubleSided;
            ComMaterialsCheck.IsChecked = _options.UseComMaterials;
            RoughnessBox.Text = Fmt(_options.DefaultRoughness);
            MetallicBox.Text = Fmt(_options.DefaultMetallic);

            ExtrasCheck.IsChecked = _options.EmbedItemExtras;
            PropertiesCheck.IsChecked = _options.ExportPropertiesSidecar;
            PropertyFilterBox.Text = string.Join(";", _options.PropertyCategoryFilter.ToArray());
        }

        private void ReadOptionsFromUi()
        {
            _options.OutputFolder = OutputFolderBox.Text.Trim();
            _options.Format = TagOf(FormatCombo, _options.Format);
            _options.Batch = TagOf(BatchCombo, _options.Batch);
            _options.FileNameTemplate = string.IsNullOrWhiteSpace(TemplateBox.Text) ? "{set}" : TemplateBox.Text.Trim();
            _options.OverwriteExisting = OverwriteCheck.IsChecked == true;

            _options.TargetUnits = TagOf(UnitsCombo, _options.TargetUnits);
            _options.Origin = TagOf(OriginCombo, _options.Origin);
            _options.CustomOrigin = new Vec3(
                ParseDouble(OriginXBox.Text, 0),
                ParseDouble(OriginYBox.Text, 0),
                ParseDouble(OriginZBox.Text, 0));
            _options.ConvertToYUp = YUpCheck.IsChecked == true;

            _options.Grouping = TagOf(GroupingCombo, _options.Grouping);
            _options.WeldVertices = WeldCheck.IsChecked == true;
            _options.WeldTolerance = Math.Max(0.0, ParseDouble(WeldToleranceBox.Text, 0.0001));
            _options.IncludeNormals = NormalsCheck.IsChecked == true;
            _options.IncludeVertexColors = VertexColorsCheck.IsChecked == true;
            _options.IncludeLines = LinesCheck.IsChecked == true;
            _options.SkipHidden = SkipHiddenCheck.IsChecked == true;
            _options.MinTriangleEdge = Math.Max(0.0, ParseDouble(MinEdgeBox.Text, 0));

            _options.EmitMaterials = MaterialsCheck.IsChecked == true;
            _options.PreserveTransparency = TransparencyCheck.IsChecked == true;
            _options.DoubleSided = DoubleSidedCheck.IsChecked == true;
            _options.UseComMaterials = ComMaterialsCheck.IsChecked == true;
            _options.DefaultRoughness = Clamp01(ParseDouble(RoughnessBox.Text, 0.85));
            _options.DefaultMetallic = Clamp01(ParseDouble(MetallicBox.Text, 0.0));

            _options.EmbedItemExtras = ExtrasCheck.IsChecked == true;
            _options.ExportPropertiesSidecar = PropertiesCheck.IsChecked == true;
            _options.PropertyCategoryFilter.Clear();
            foreach (string category in (PropertyFilterBox.Text ?? "").Split(';'))
                if (!string.IsNullOrWhiteSpace(category)) _options.PropertyCategoryFilter.Add(category.Trim());
        }

        // ── Commands ─────────────────────────────────────────────────────────

        private void OnExport(object sender, RoutedEventArgs e)
        {
            if (_isExporting) return;

            Document document = NavApp.ActiveDocument;
            if (document == null) return;

            ReadOptionsFromUi();
            // Always the state of the 4D tab as it stands right now, not as it stood
            // when the tab was last opened.
            SyncDatasmithTaskLinks();

            List<ExportPart> parts = SelectionSetTree.ResolveCheckedParts(document, _allNodes);
            if (parts.Count == 0)
            {
                MessageBox.Show("None of the ticked sets resolved to any items.", "NavEx",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _lastOutputFolder = _options.OutputFolder;
            BeginBusy("Exporting…");
            Log.Info("──────────────────────────────────────────");
            Log.Info("Export started: " + parts.Count + " set(s) → " + _options.OutputFolder);

            ExportSummary summary;
            try
            {
                summary = new ExportRunner(_options).Run(document, parts, _progress);
            }
            finally
            {
                EndBusy();
            }

            ReportSummary(summary);
        }

        private void ReportSummary(ExportSummary summary)
        {
            var sb = new StringBuilder();
            var invariant = CultureInfo.InvariantCulture;

            if (summary.Cancelled) sb.AppendLine("Export cancelled.");

            sb.AppendLine(string.Format(invariant,
                "{0} file(s) written, {1} failed, {2:N0} triangles, {3:0.#} s",
                summary.SucceededCount, summary.FailedCount, summary.TotalTriangles,
                summary.Elapsed.TotalSeconds));

            if (summary.SchedulePath != null)
                sb.AppendLine("  ✓ " + Path.GetFileName(summary.SchedulePath) + "  (4D schedule)");
            else if (summary.Note != null)
                sb.AppendLine(summary.Note);

            foreach (ExportResult result in summary.Results)
            {
                sb.AppendLine(result.Failed
                    ? "  ✗ " + result.SetName + " — " + result.FailureReason
                    : "  ✓ " + Path.GetFileName(result.FilePath) + "  " + result.SizeDisplay);
            }

            StatusText.Text = string.Format(invariant, "{0} file(s) written in {1:0.#} s.",
                summary.SucceededCount, summary.Elapsed.TotalSeconds);
            Log.Info(sb.ToString().TrimEnd());

            if (summary.SucceededCount > 0 && !summary.Cancelled)
            {
                MessageBoxResult answer = MessageBox.Show(
                    sb.ToString() + Environment.NewLine + "Open the output folder?",
                    "NavEx — export complete", MessageBoxButton.YesNo, MessageBoxImage.Information);

                if (answer == MessageBoxResult.Yes) OpenOutputFolder();
            }
            else if (summary.SucceededCount == 0)
            {
                MessageBox.Show(sb.ToString(), "NavEx", MessageBoxButton.OK, MessageBoxImage.Warning);
                Tabs.SelectedIndex = 2;
            }
        }

        private void OnEstimate(object sender, RoutedEventArgs e)
        {
            Document document = NavApp.ActiveDocument;
            if (document == null || _isExporting) return;

            ReadOptionsFromUi();
            List<ExportPart> parts = SelectionSetTree.ResolveCheckedParts(document, _allNodes);

            if (parts.Count == 0)
            {
                EstimateText.Text = "Nothing ticked.";
                return;
            }

            BeginBusy("Estimating…");
            try
            {
                EstimateText.Text = ExportRunner.Estimate(parts, _progress);
                Log.Info("Estimate: " + EstimateText.Text);
            }
            finally
            {
                EndBusy();
            }
        }

        private void OnCancel(object sender, RoutedEventArgs e)
        {
            if (_progress != null)
            {
                _progress.CancelRequested = true;
                StatusText.Text = "Cancelling…";
            }
        }

        private void OnClose(object sender, RoutedEventArgs e) { Close(); }

        private void OnRefreshTree(object sender, RoutedEventArgs e) { RefreshTree(); }

        private void OnCheckAll(object sender, RoutedEventArgs e) { SetAllChecked(true); }

        private void OnUncheckAll(object sender, RoutedEventArgs e) { SetAllChecked(false); }

        private void SetAllChecked(bool value)
        {
            foreach (SetNode root in _allNodes)
                foreach (SetNode node in root.DescendantsAndSelf)
                    node.SetCheckedQuiet(value);
            UpdateSummary();
        }

        private void OnExpandAll(object sender, RoutedEventArgs e) { SetExpanded(true); }

        private void OnCollapseAll(object sender, RoutedEventArgs e) { SetExpanded(false); }

        private void SetExpanded(bool value)
        {
            foreach (SetNode root in _allNodes)
                foreach (SetNode node in root.DescendantsAndSelf)
                    node.IsExpanded = value;
            ApplyFilter();
        }

        private void OnFilterChanged(object sender, TextChangedEventArgs e)
        {
            if (!_initialized) return;
            ApplyFilter();
        }

        private void OnFormatChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_initialized) return;

            ExportFormat format = TagOf(FormatCombo, ExportFormat.Glb);
            if (format == ExportFormat.Obj)
                Log.Info("OBJ carries colour and opacity but not PBR roughness, vertex colours or node metadata.");
        }

        private void OnOriginChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CustomOriginPanel == null) return;
            CustomOriginPanel.Visibility = TagOf(OriginCombo, OriginMode.SelectionCenter) == OriginMode.Custom
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void OnBrowseFolder(object sender, RoutedEventArgs e)
        {
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = "Choose where NavEx writes the exported files";
                dialog.SelectedPath = Directory.Exists(OutputFolderBox.Text)
                    ? OutputFolderBox.Text
                    : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                    OutputFolderBox.Text = dialog.SelectedPath;
            }
        }

        private void OnOpenOutputFolder(object sender, RoutedEventArgs e) { OpenOutputFolder(); }

        private void OpenOutputFolder()
        {
            string folder = string.IsNullOrWhiteSpace(_lastOutputFolder) ? OutputFolderBox.Text : _lastOutputFolder;
            try
            {
                if (Directory.Exists(folder)) Process.Start("explorer.exe", "\"" + folder + "\"");
            }
            catch (Exception ex)
            {
                Log.Debug("Could not open the output folder: " + ex.Message);
            }
        }

        // ── Presets ──────────────────────────────────────────────────────────

        private void RefreshPresets()
        {
            PresetCombo.Items.Clear();
            foreach (string preset in SettingsStore.ListPresets())
                PresetCombo.Items.Add(preset);
            if (PresetCombo.Items.Count > 0) PresetCombo.SelectedIndex = 0;
        }

        private void OnLoadPreset(object sender, RoutedEventArgs e)
        {
            var name = PresetCombo.SelectedItem as string;
            if (string.IsNullOrEmpty(name)) return;

            ExportOptions loaded = SettingsStore.Load(SettingsStore.PresetPath(name));
            CopyInto(loaded, _options);
            ApplyOptionsToUi();
            Log.Info("Loaded preset '" + name + "'.");
        }

        private void OnSavePreset(object sender, RoutedEventArgs e)
        {
            ReadOptionsFromUi();

            // A SaveFileDialog rooted at the presets folder gives naming, overwrite
            // confirmation and validation without a bespoke prompt window.
            Directory.CreateDirectory(SettingsStore.PresetFolder);
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                InitialDirectory = SettingsStore.PresetFolder,
                Filter = "NavEx preset (*.txt)|*.txt",
                DefaultExt = ".txt",
                Title = "Save NavEx preset",
                FileName = "preset"
            };

            if (dialog.ShowDialog(this) != true) return;

            SettingsStore.Save(_options, dialog.FileName);
            RefreshPresets();
            Log.Info("Saved preset '" + Path.GetFileNameWithoutExtension(dialog.FileName) + "'.");
        }

        private void OnResetDefaults(object sender, RoutedEventArgs e)
        {
            var defaults = new ExportOptions { OutputFolder = _options.OutputFolder };
            CopyInto(defaults, _options);
            ApplyOptionsToUi();
            Log.Info("Options reset to defaults.");
        }

        private static void CopyInto(ExportOptions source, ExportOptions target)
        {
            target.OutputFolder = source.OutputFolder;
            target.Format = source.Format;
            target.Batch = source.Batch;
            target.FileNameTemplate = source.FileNameTemplate;
            target.OverwriteExisting = source.OverwriteExisting;
            target.TargetUnits = source.TargetUnits;
            target.Grouping = source.Grouping;
            target.Origin = source.Origin;
            target.CustomOrigin = source.CustomOrigin;
            target.ConvertToYUp = source.ConvertToYUp;
            target.IncludeNormals = source.IncludeNormals;
            target.IncludeVertexColors = source.IncludeVertexColors;
            target.WeldVertices = source.WeldVertices;
            target.WeldTolerance = source.WeldTolerance;
            target.MinTriangleEdge = source.MinTriangleEdge;
            target.MaxVerticesPerPrimitive = source.MaxVerticesPerPrimitive;
            target.SkipHidden = source.SkipHidden;
            target.IncludeLines = source.IncludeLines;
            target.EmitMaterials = source.EmitMaterials;
            target.PreserveTransparency = source.PreserveTransparency;
            target.DoubleSided = source.DoubleSided;
            target.DefaultRoughness = source.DefaultRoughness;
            target.DefaultMetallic = source.DefaultMetallic;
            target.UseComMaterials = source.UseComMaterials;
            target.EmbedItemExtras = source.EmbedItemExtras;
            target.ExportPropertiesSidecar = source.ExportPropertiesSidecar;
            target.ComBatchSize = source.ComBatchSize;
            target.PropertyCategoryFilter.Clear();
            target.PropertyCategoryFilter.AddRange(source.PropertyCategoryFilter);
        }

        // ── Log ──────────────────────────────────────────────────────────────

        private void AppendLog(string message, LogLevel level)
        {
            if (level == LogLevel.Debug && VerboseCheck.IsChecked != true) return;

            string prefix;
            switch (level)
            {
                case LogLevel.Success: prefix = "[ok]   "; break;
                case LogLevel.Warning: prefix = "[warn] "; break;
                case LogLevel.Error: prefix = "[err]  "; break;
                case LogLevel.Debug: prefix = "[dbg]  "; break;
                default: prefix = "       "; break;
            }

            LogBox.AppendText(DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " " + prefix + message + Environment.NewLine);
            LogBox.ScrollToEnd();
        }

        private void OnCopyLog(object sender, RoutedEventArgs e)
        {
            try { Clipboard.SetText(LogBox.Text); }
            catch (Exception) { }
        }

        private void OnClearLog(object sender, RoutedEventArgs e) { LogBox.Clear(); }

        // ── Busy state and the UI pump ───────────────────────────────────────

        private void BeginBusy(string status)
        {
            _isExporting = true;
            _progress = new ProgressContext { Report = OnProgress, Pump = PumpUi };

            ExportButton.IsEnabled = false;
            EstimateButton.IsEnabled = false;
            CancelButton.IsEnabled = true;
            Progress.Visibility = Visibility.Visible;
            Progress.IsIndeterminate = true;
            StatusText.Text = status;
            Cursor = System.Windows.Input.Cursors.Wait;
            PumpUi(true);
        }

        private void EndBusy()
        {
            _isExporting = false;
            _progress = null;

            CancelButton.IsEnabled = false;
            EstimateButton.IsEnabled = true;
            Progress.Visibility = Visibility.Collapsed;
            Cursor = System.Windows.Input.Cursors.Arrow;
            UpdateSummary();
        }

        private void OnProgress(string message, double fraction)
        {
            StatusText.Text = message;
            if (fraction < 0)
            {
                Progress.IsIndeterminate = true;
            }
            else
            {
                Progress.IsIndeterminate = false;
                Progress.Value = fraction;
            }
        }

        private void PumpUi() { PumpUi(false); }

        /// <summary>
        /// The Navisworks API and its COM bridge are bound to the main thread, so the
        /// export loop cannot move to a worker. Draining the dispatcher queue between
        /// batches is what keeps the window painting and the Cancel button clickable.
        /// It is throttled — pumping on every item would cost more than the export.
        /// </summary>
        private void PumpUi(bool force)
        {
            DateTime now = DateTime.UtcNow;
            if (!force && (now - _lastPump).TotalMilliseconds < 60) return;
            _lastPump = now;

            var frame = new DispatcherFrame();
            this.Dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(delegate { frame.Continue = false; }));
            System.Windows.Threading.Dispatcher.PushFrame(frame);
        }

        // ── Small helpers ────────────────────────────────────────────────────

        private static void SelectByTag(ComboBox combo, string tag)
        {
            foreach (object entry in combo.Items)
            {
                var item = entry as ComboBoxItem;
                if (item != null && string.Equals(Convert.ToString(item.Tag), tag, StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedItem = item;
                    return;
                }
            }
            if (combo.Items.Count > 0) combo.SelectedIndex = 0;
        }

        private static T TagOf<T>(ComboBox combo, T fallback) where T : struct
        {
            var item = combo.SelectedItem as ComboBoxItem;
            if (item == null || item.Tag == null) return fallback;

            try { return (T)Enum.Parse(typeof(T), Convert.ToString(item.Tag), true); }
            catch (Exception) { return fallback; }
        }

        /// <summary>The selected item's Tag as text, for combos whose values are not an enum.</summary>
        private static string TagText(ComboBox combo)
        {
            var item = combo == null ? null : combo.SelectedItem as ComboBoxItem;
            return item == null || item.Tag == null ? "" : Convert.ToString(item.Tag);
        }

        private static double ParseDouble(string text, double fallback)
        {
            double value;
            if (double.TryParse((text ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                return value;
            if (double.TryParse((text ?? "").Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out value))
                return value;
            return fallback;
        }

        private static string Fmt(double value)
        {
            return value.ToString("0.######", CultureInfo.InvariantCulture);
        }

        private static double Clamp01(double value)
        {
            if (double.IsNaN(value)) return 0;
            return value < 0 ? 0 : (value > 1 ? 1 : value);
        }
    }
}
