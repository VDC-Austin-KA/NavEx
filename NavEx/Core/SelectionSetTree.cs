using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using Autodesk.Navisworks.Api;

namespace NavEx.Core
{
    /// <summary>
    /// One row in the export tree: a search set, a folder of them, or the synthetic
    /// "Current Selection" entry that always sits at the top.
    /// </summary>
    public class SetNode : INotifyPropertyChanged
    {
        private bool _isChecked;
        private bool _isExpanded;
        private bool _suppressCascade;
        private string _detail = "";

        public string Name { get; set; }
        public bool IsFolder { get; set; }
        public bool IsCurrentSelection { get; set; }
        public SavedItem Item { get; set; }
        public SetNode Parent { get; set; }
        public ObservableCollection<SetNode> Children { get; private set; }

        public SetNode()
        {
            Children = new ObservableCollection<SetNode>();
            _isExpanded = true;
        }

        /// <summary>Two-way bound to TreeViewItem.IsExpanded, so Expand/Collapse All reaches the UI.</summary>
        public bool IsExpanded
        {
            get { return _isExpanded; }
            set
            {
                if (_isExpanded == value) return;
                _isExpanded = value;
                OnPropertyChanged("IsExpanded");
            }
        }

        /// <summary>Right-hand column text: item counts, or why a set can't be exported.</summary>
        public string Detail
        {
            get { return _detail; }
            set { _detail = value; OnPropertyChanged("Detail"); }
        }

        public bool IsChecked
        {
            get { return _isChecked; }
            set
            {
                if (_isChecked == value) return;
                _isChecked = value;
                OnPropertyChanged("IsChecked");

                if (_suppressCascade) return;

                // Ticking a folder ticks everything under it — that is the whole
                // point of folders here — but a child's own change must not bounce
                // back down the tree.
                foreach (SetNode child in Children)
                    child.IsChecked = value;
            }
        }

        public void SetCheckedQuiet(bool value)
        {
            _suppressCascade = true;
            IsChecked = value;
            _suppressCascade = false;
        }

        public IEnumerable<SetNode> DescendantsAndSelf
        {
            get
            {
                yield return this;
                foreach (SetNode child in Children)
                    foreach (SetNode descendant in child.DescendantsAndSelf)
                        yield return descendant;
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged(string name)
        {
            PropertyChangedEventHandler handler = PropertyChanged;
            if (handler != null) handler(this, new PropertyChangedEventArgs(name));
        }
    }

    /// <summary>
    /// Builds the export tree from the document's Selection Sets, and turns checked
    /// rows back into resolved <see cref="ExportPart"/>s.
    /// </summary>
    public static class SelectionSetTree
    {
        public const string CurrentSelectionLabel = "◆ Current Selection";

        public static ObservableCollection<SetNode> Build(Document document)
        {
            var roots = new ObservableCollection<SetNode>();

            var currentSelection = new SetNode
            {
                Name = CurrentSelectionLabel,
                IsCurrentSelection = true
            };

            try
            {
                int count = document.CurrentSelection.SelectedItems.Count;
                currentSelection.Detail = count == 0 ? "nothing selected" : count.ToString("N0") + " selected";
                currentSelection.IsChecked = count > 0;
            }
            catch (Exception)
            {
                currentSelection.Detail = "";
            }

            roots.Add(currentSelection);

            try
            {
                GroupItem root = document.SelectionSets.RootItem;
                if (root != null)
                    foreach (SavedItem child in root.Children)
                        AddNode(roots, child, null);
            }
            catch (Exception ex)
            {
                Log.Error("Could not read the document's selection sets", ex);
            }

            return roots;
        }

        private static void AddNode(ObservableCollection<SetNode> target, SavedItem item, SetNode parent)
        {
            var selectionSet = item as SelectionSet;
            var group = item as GroupItem;

            // A SelectionSet can itself be a group in the saved-items tree, so test
            // for the set first — otherwise nested sets are shown as empty folders.
            if (selectionSet != null)
            {
                var node = new SetNode
                {
                    Name = item.DisplayName ?? "(unnamed set)",
                    Item = item,
                    Parent = parent,
                    Detail = selectionSet.HasSearch ? "search set" : "selection set"
                };
                target.Add(node);

                if (group != null)
                    foreach (SavedItem child in group.Children)
                        AddNode(node.Children, child, node);
                return;
            }

            if (group != null)
            {
                var folder = new SetNode
                {
                    Name = item.DisplayName ?? "(unnamed folder)",
                    IsFolder = true,
                    Item = item,
                    Parent = parent
                };
                target.Add(folder);

                foreach (SavedItem child in group.Children)
                    AddNode(folder.Children, child, folder);

                folder.Detail = folder.Children.Count == 1 ? "1 item" : folder.Children.Count + " items";
            }
        }

        /// <summary>
        /// Resolves the checked rows into concrete item collections. Folders
        /// contribute nothing themselves — only the sets inside them do — so a
        /// checked folder yields one part per contained set, keeping per-set output
        /// files intact.
        /// </summary>
        public static List<ExportPart> ResolveCheckedParts(Document document, IEnumerable<SetNode> roots)
        {
            var parts = new List<ExportPart>();
            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (SetNode root in roots)
            {
                foreach (SetNode node in root.DescendantsAndSelf)
                {
                    if (!node.IsChecked || node.IsFolder) continue;

                    ModelItemCollection items = Resolve(document, node);
                    if (items == null || items.Count == 0)
                    {
                        Log.Warning("'" + node.Name + "' resolves to no items — skipped.");
                        continue;
                    }

                    string name = node.IsCurrentSelection ? "CurrentSelection" : node.Name;
                    string unique = name;
                    int suffix = 1;
                    while (!seenNames.Add(unique))
                        unique = name + "_" + (++suffix);

                    parts.Add(new ExportPart(unique, items));
                }
            }

            return parts;
        }

        private static ModelItemCollection Resolve(Document document, SetNode node)
        {
            try
            {
                if (node.IsCurrentSelection)
                {
                    var copy = new ModelItemCollection();
                    copy.CopyFrom(document.CurrentSelection.SelectedItems);
                    return copy;
                }

                var selectionSet = node.Item as SelectionSet;
                if (selectionSet == null) return null;

                // GetSelectedItems re-runs the search against the current document,
                // so a search set always exports what it matches right now rather
                // than whatever was selected when it was saved.
                return selectionSet.GetSelectedItems(document);
            }
            catch (Exception ex)
            {
                Log.Error("Could not resolve '" + node.Name + "'", ex);
                return null;
            }
        }
    }
}
