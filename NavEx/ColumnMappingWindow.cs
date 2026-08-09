using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NavEx.FourD;

namespace NavEx
{
    /// <summary>
    /// Lets the user finish a column mapping the importer could not complete.
    ///
    /// Built in code rather than XAML because the content is entirely driven by
    /// the imported file — one row per source column, however many that is.
    ///
    /// The dialog shows what auto-detection decided and why, alongside real sample
    /// values from the file, because "is this column the planned finish or the
    /// baseline finish" is a question only someone looking at the data can answer.
    /// </summary>
    public class ColumnMappingWindow : Window
    {
        private readonly ScheduleImportResult _result;
        private readonly List<ComboBox> _selectors = new List<ComboBox>();
        private readonly TextBlock _status;

        public ColumnMappingWindow(ScheduleImportResult result)
        {
            _result = result;

            Title = "NavEx — map schedule columns";
            Width = 860;
            Height = 620;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = new SolidColorBrush(Color.FromRgb(0xF4, 0xF5, 0xF7));
            FontFamily = new FontFamily("Segoe UI");
            FontSize = 12;

            var root = new DockPanel { Margin = new Thickness(14) };

            root.Children.Add(BuildHeader());
            DockPanel.SetDock(root.Children[root.Children.Count - 1], Dock.Top);

            _status = new TextBlock
            {
                Margin = new Thickness(0, 10, 0, 0),
                TextWrapping = TextWrapping.Wrap
            };

            var footer = BuildFooter();
            DockPanel.SetDock(footer, Dock.Bottom);
            root.Children.Add(footer);

            root.Children.Add(BuildGrid());

            Content = root;
            UpdateStatus();
        }

        private UIElement BuildHeader()
        {
            var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };

            panel.Children.Add(new TextBlock
            {
                Text = _result.Summary(),
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap
            });

            panel.Children.Add(new TextBlock
            {
                Text = "Task Name, Planned Start and Planned Finish are required. "
                     + "Everything else is optional — map what you have.",
                Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0)
            });

            foreach (string warning in _result.Warnings)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = "• " + warning,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xB4, 0x53, 0x09)),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 3, 0, 0)
                });
            }

            return panel;
        }

        private UIElement BuildGrid()
        {
            var grid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            AddHeaderCell(grid, 0, 0, "Source column");
            AddHeaderCell(grid, 0, 1, "Maps to");
            AddHeaderCell(grid, 0, 2, "Sample values / detection");

            int row = 1;
            foreach (ScheduleColumn column in _result.Columns)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                var name = new TextBlock
                {
                    Text = column.Header,
                    Margin = new Thickness(2, 6, 8, 6),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetRow(name, row);
                Grid.SetColumn(name, 0);
                grid.Children.Add(name);

                var selector = new ComboBox
                {
                    Margin = new Thickness(0, 3, 8, 3),
                    Tag = column
                };
                foreach (ScheduleField field in ColumnAliases.AllFields())
                    selector.Items.Add(new ComboBoxItem
                    {
                        Content = ColumnAliases.DisplayName(field),
                        Tag = field
                    });
                selector.SelectedIndex = (int)column.Field;
                selector.SelectionChanged += (s, e) => UpdateStatus();

                Grid.SetRow(selector, row);
                Grid.SetColumn(selector, 1);
                grid.Children.Add(selector);
                _selectors.Add(selector);

                string samples = string.Join(" · ", column.Samples.Take(3).ToArray());
                if (samples.Length > 90) samples = samples.Substring(0, 90) + "…";
                if (column.AutoDetected && !string.IsNullOrEmpty(column.DetectionBasis))
                    samples = (samples.Length > 0 ? samples + "    " : "") + "[" + column.DetectionBasis + "]";

                var detail = new TextBlock
                {
                    Text = samples,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)),
                    Margin = new Thickness(2, 6, 2, 6),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetRow(detail, row);
                Grid.SetColumn(detail, 2);
                grid.Children.Add(detail);

                row++;
            }

            return new ScrollViewer
            {
                Content = grid,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xDD, 0xE1, 0xE6)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8)
            };
        }

        private static void AddHeaderCell(Grid grid, int row, int column, string text)
        {
            if (grid.RowDefinitions.Count == 0)
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var block = new TextBlock
            {
                Text = text,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(2, 0, 8, 6)
            };
            Grid.SetRow(block, row);
            Grid.SetColumn(block, column);
            grid.Children.Add(block);
        }

        private UIElement BuildFooter()
        {
            var panel = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            var cancel = new Button { Content = "Cancel", MinWidth = 84, Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(6, 0, 0, 0) };
            cancel.Click += (s, e) => { DialogResult = false; Close(); };

            var ok = new Button
            {
                Content = "Use this mapping",
                MinWidth = 130,
                Padding = new Thickness(14, 5, 14, 5),
                Margin = new Thickness(6, 0, 0, 0),
                IsDefault = true
            };
            ok.Click += OnAccept;

            buttons.Children.Add(cancel);
            buttons.Children.Add(ok);

            DockPanel.SetDock(buttons, Dock.Right);
            panel.Children.Add(buttons);
            panel.Children.Add(_status);

            return panel;
        }

        private void Commit()
        {
            // A field can only be claimed once; the last selector wins and the
            // earlier one reverts, which is checked in UpdateStatus.
            foreach (ComboBox selector in _selectors)
            {
                var column = (ScheduleColumn)selector.Tag;
                var item = selector.SelectedItem as ComboBoxItem;
                column.Field = item == null ? ScheduleField.Ignore : (ScheduleField)item.Tag;
                column.AutoDetected = false;
            }
        }

        private void UpdateStatus()
        {
            var chosen = new Dictionary<ScheduleField, int>();
            foreach (ComboBox selector in _selectors)
            {
                var item = selector.SelectedItem as ComboBoxItem;
                if (item == null) continue;
                var field = (ScheduleField)item.Tag;
                if (field == ScheduleField.Ignore) continue;

                int count;
                chosen.TryGetValue(field, out count);
                chosen[field] = count + 1;
            }

            var problems = new List<string>();

            foreach (ScheduleField field in ColumnAliases.AllFields())
                if (ColumnAliases.IsRequired(field) && !chosen.ContainsKey(field))
                    problems.Add("missing " + ColumnAliases.DisplayName(field));

            foreach (KeyValuePair<ScheduleField, int> entry in chosen)
                if (entry.Value > 1)
                    problems.Add(ColumnAliases.DisplayName(entry.Key) + " is mapped " + entry.Value + " times");

            if (problems.Count == 0)
            {
                _status.Text = "Ready to import.";
                _status.Foreground = new SolidColorBrush(Color.FromRgb(0x04, 0x7A, 0x55));
            }
            else
            {
                _status.Text = string.Join("; ", problems.ToArray());
                _status.Foreground = new SolidColorBrush(Color.FromRgb(0xB4, 0x53, 0x09));
            }
        }

        private void OnAccept(object sender, RoutedEventArgs e)
        {
            Commit();

            List<ScheduleField> missing = _result.MissingRequiredFields().ToList();
            if (missing.Count > 0)
            {
                MessageBox.Show(this,
                    "Still missing: " + string.Join(", ", missing.Select(ColumnAliases.DisplayName).ToArray()),
                    "NavEx", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            ScheduleImporter.Rebuild(_result);

            if (_result.ValidTaskCount == 0)
            {
                MessageBox.Show(this,
                    "That mapping produced no usable tasks — check the date columns.",
                    "NavEx", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            DialogResult = true;
            Close();
        }

        /// <summary>Shows the dialog when needed, or when the user asks to review.</summary>
        public static bool Prompt(Window owner, ScheduleImportResult result, bool force)
        {
            if (!force && !result.NeedsMapping) return true;

            var window = new ColumnMappingWindow(result);
            try { if (owner != null && owner.IsVisible) window.Owner = owner; }
            catch (InvalidOperationException) { /* an unshown owner is not fatal */ }

            return window.ShowDialog() == true;
        }
    }
}
