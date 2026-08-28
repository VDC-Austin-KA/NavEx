using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Navisworks.Api;
using NavEx.Core;

namespace NavEx.FourD
{
    /// <summary>
    /// One proposed rename, previewed before anything is committed. Exposed as
    /// properties rather than fields because the review list binds to them, and
    /// WPF bindings only see properties.
    /// </summary>
    public class RenameProposal
    {
        public SavedItem Item { get; set; }
        public string CurrentName { get; set; }
        public string ProposedName { get; set; }
        public FourDName Identity { get; set; }
        public bool Apply { get; set; }
        public string Note { get; set; }

        public RenameProposal()
        {
            CurrentName = "";
            ProposedName = "";
            Note = "";
            Apply = true;
        }

        public bool IsChange
        {
            get { return !string.Equals(CurrentName, ProposedName, StringComparison.Ordinal); }
        }
    }

    /// <summary>How <see cref="SetRenamer.ProposeGrouping"/> should file the sets.</summary>
    public enum GroupBy { Discipline, Level, Activity, LevelThenDiscipline }

    /// <summary>One proposed move of a set into a folder, previewed before committing.</summary>
    public class GroupProposal
    {
        public SavedItem Item { get; set; }
        public string SetName { get; set; }
        public string CurrentFolder { get; set; }
        public string TargetFolder { get; set; }
        public bool Apply { get; set; }
        public string Note { get; set; }

        public GroupProposal()
        {
            SetName = "";
            CurrentFolder = "";
            TargetFolder = "";
            Note = "";
        }

        public bool IsChange
        {
            get { return !string.Equals(CurrentFolder, TargetFolder, StringComparison.OrdinalIgnoreCase); }
        }
    }

    /// <summary>
    /// Builds the model's search sets into 4D match targets, and batch-renames them
    /// onto the 4D scheme.
    ///
    /// Renaming is preview-first and never automatic: a search set name is the
    /// handle a whole coordination workflow hangs off — clash tests reference sets
    /// by name, and so do other people's saved viewpoints and reports — so the user
    /// sees every proposed change and can uncheck any of them before a single set
    /// is touched.
    /// </summary>
    public class SetRenamer
    {
        private readonly NameClassifier _classifier;

        public SetRenamer(NameClassifier classifier)
        {
            _classifier = classifier ?? new NameClassifier(new SequenceProfile());
        }

        /// <summary>Walks the saved-items tree and returns every selection set as a match target.</summary>
        public List<MatchTarget> CollectTargets(Document document)
        {
            var targets = new List<MatchTarget>();
            if (document == null) return targets;

            try
            {
                GroupItem root = document.SelectionSets.RootItem;
                if (root != null) Walk(root, "", targets);
            }
            catch (Exception ex)
            {
                Log.Error("Could not read selection sets", ex);
            }

            foreach (MatchTarget target in targets)
                target.Identity = _classifier.Classify(target.SetName, target.SetPath);

            return targets;
        }

        private void Walk(GroupItem group, string path, List<MatchTarget> targets)
        {
            foreach (SavedItem child in group.Children)
            {
                var set = child as SelectionSet;
                string childPath = string.IsNullOrEmpty(path)
                    ? (child.DisplayName ?? "")
                    : path + " / " + (child.DisplayName ?? "");

                if (set != null)
                {
                    targets.Add(new MatchTarget
                    {
                        SetName = child.DisplayName ?? "",
                        SetPath = path,
                        Tag = child
                    });
                }

                var childGroup = child as GroupItem;
                if (childGroup != null) Walk(childGroup, childPath, targets);
            }
        }

        /// <summary>
        /// Proposes 4D names for the given sets. Sets that already carry a valid
        /// code are left alone, so running this repeatedly is a no-op rather than a
        /// creeping accumulation of prefixes.
        /// </summary>
        public List<RenameProposal> Propose(IEnumerable<MatchTarget> targets, bool includeDescription)
        {
            var proposals = new List<RenameProposal>();
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (MatchTarget target in targets)
            {
                var item = target.Tag as SavedItem;
                if (item == null) continue;

                FourDName identity = target.Identity ?? _classifier.Classify(target.SetName, target.SetPath);

                var proposal = new RenameProposal
                {
                    Item = item,
                    CurrentName = target.SetName,
                    Identity = identity
                };

                if (!identity.IsResolved)
                {
                    // Without both a discipline and an activity the sequence number
                    // would be a guess, and a wrong number sorts the folder wrong.
                    proposal.ProposedName = target.SetName;
                    proposal.Apply = false;
                    proposal.Note = "unresolved (" + identity.Basis + ") — set discipline and activity to enable";
                    proposals.Add(proposal);
                    continue;
                }

                string candidate = identity.Render(includeDescription);

                // Two sets can legitimately reduce to the same code (east and west
                // wings of the same pour). Suffix rather than collide.
                string unique = candidate;
                int suffix = 1;
                while (!used.Add(unique))
                    unique = candidate + "-" + (++suffix).ToString(CultureInfo.InvariantCulture);

                proposal.ProposedName = unique;
                proposal.Note = identity.Basis;
                if (!proposal.IsChange) { proposal.Apply = false; proposal.Note = "already correct"; }

                proposals.Add(proposal);
            }

            return proposals;
        }

        /// <summary>
        /// Commits the ticked proposals inside one transaction, so a failure part
        /// way through does not leave the set tree half-renamed.
        /// </summary>
        public int Apply(Document document, IEnumerable<RenameProposal> proposals, ProgressContext progress)
        {
            List<RenameProposal> pending = proposals
                .Where(p => p.Apply && p.IsChange && p.Item != null)
                .ToList();

            if (pending.Count == 0) return 0;

            int renamed = 0;
            using (Transaction transaction = document.BeginTransaction("NavEx: 4D set rename"))
            {
                foreach (RenameProposal proposal in pending)
                {
                    if (progress != null)
                    {
                        progress.ThrowIfCancelled();
                        progress.Update("Renaming " + proposal.CurrentName,
                            (double)renamed / pending.Count);
                        progress.Tick();
                    }

                    try
                    {
                        document.SelectionSets.EditDisplayName(proposal.Item, proposal.ProposedName);
                        renamed++;
                    }
                    catch (Exception ex)
                    {
                        Log.Error("Could not rename '" + proposal.CurrentName + "'", ex);
                    }
                }

                transaction.Commit();
            }

            Log.Success(string.Format(CultureInfo.InvariantCulture, "Renamed {0:N0} search set(s).", renamed));
            return renamed;
        }

        // ── Regrouping ───────────────────────────────────────────────────────

        /// <summary>
        /// Proposes a folder for each set. Sets whose identity has no answer for
        /// the chosen key go to <see cref="UnsortedFolder"/> rather than to a
        /// folder named after nothing.
        /// </summary>
        public List<GroupProposal> ProposeGrouping(IEnumerable<MatchTarget> targets, GroupBy by)
        {
            var proposals = new List<GroupProposal>();

            foreach (MatchTarget target in targets)
            {
                var item = target.Tag as SavedItem;
                if (item == null) continue;

                FourDName identity = target.Identity ?? _classifier.Classify(target.SetName, target.SetPath);

                var proposal = new GroupProposal
                {
                    Item = item,
                    SetName = target.SetName,
                    CurrentFolder = string.IsNullOrEmpty(target.SetPath) ? RootFolder : target.SetPath,
                    TargetFolder = FolderFor(identity, by)
                };

                proposal.Note = proposal.IsChange
                    ? (identity.IsResolved ? identity.Basis : "unresolved — filed under " + UnsortedFolder)
                    : "already there";
                proposal.Apply = proposal.IsChange;

                proposals.Add(proposal);
            }

            return proposals;
        }

        public const string UnsortedFolder = "ZZ Unsorted";

        /// <summary>Shown for a set that is not in a folder yet.</summary>
        public const string RootFolder = "(top level)";

        private string FolderFor(FourDName identity, GroupBy by)
        {
            if (identity == null) return UnsortedFolder;

            string discipline = identity.DisciplineCode;
            string activity = identity.ActivityCode;
            string level = identity.LevelTag;

            switch (by)
            {
                case GroupBy.Level:
                    return string.IsNullOrEmpty(level) || level == "L00" ? UnsortedFolder : level;

                case GroupBy.Activity:
                    return string.IsNullOrEmpty(activity) ? UnsortedFolder : Label(activity, ActivityName(activity));

                case GroupBy.LevelThenDiscipline:
                    if (string.IsNullOrEmpty(level) || level == "L00" || string.IsNullOrEmpty(discipline))
                        return UnsortedFolder;
                    return level + " " + discipline;

                default:
                    return string.IsNullOrEmpty(discipline) ? UnsortedFolder : Label(discipline, DisciplineName(discipline));
            }
        }

        private static string Label(string code, string displayName)
        {
            return string.IsNullOrEmpty(displayName) ? code : code + " " + displayName;
        }

        private string ActivityName(string code)
        {
            Activity activity = _classifier.Profile.FindDefinition(code);
            return activity == null ? "" : activity.DisplayName;
        }

        private string DisciplineName(string code)
        {
            Discipline discipline = _classifier.Profile.FindDisciplineDefinition(code);
            return discipline == null ? "" : discipline.DisplayName;
        }

        /// <summary>
        /// Moves the ticked sets into their folders, creating any that are missing.
        ///
        /// One transaction for the same reason the rename is one transaction: a
        /// half-reorganised set tree is worse than an unreorganised one, and the
        /// user has no undo for "some of my sets moved".
        /// </summary>
        public int ApplyGrouping(Document document, IEnumerable<GroupProposal> proposals, ProgressContext progress)
        {
            List<GroupProposal> pending = proposals
                .Where(p => p.Apply && p.IsChange && p.Item != null)
                .ToList();

            if (pending.Count == 0 || document == null) return 0;

            int moved = 0;
            using (Transaction transaction = document.BeginTransaction("NavEx: 4D set regroup"))
            {
                GroupItem root = document.SelectionSets.RootItem;

                foreach (GroupProposal proposal in pending)
                {
                    if (progress != null)
                    {
                        progress.ThrowIfCancelled();
                        progress.Update("Filing " + proposal.SetName, (double)moved / pending.Count);
                        progress.Tick();
                    }

                    try
                    {
                        GroupItem folder = FindOrCreateFolder(document, root, proposal.TargetFolder);
                        if (folder == null) continue;

                        var parent = proposal.Item.Parent as GroupItem;
                        if (parent == null) parent = root;
                        if (ReferenceEquals(parent, folder)) continue;

                        int index = parent.Children.IndexOf(proposal.Item);
                        if (index < 0)
                        {
                            // The tree changed under us — most likely the set was
                            // renamed or deleted since Propose ran.
                            Log.Warning("Skipped '" + proposal.SetName + "': it is no longer where it was.");
                            continue;
                        }

                        document.SelectionSets.Move(parent, index, folder, folder.Children.Count);
                        moved++;
                    }
                    catch (Exception ex)
                    {
                        Log.Error("Could not move '" + proposal.SetName + "'", ex);
                    }
                }

                transaction.Commit();
            }

            Log.Success(string.Format(CultureInfo.InvariantCulture, "Filed {0:N0} search set(s) into folders.", moved));
            return moved;
        }

        private static GroupItem FindOrCreateFolder(Document document, GroupItem root, string name)
        {
            if (root == null || string.IsNullOrEmpty(name)) return null;

            foreach (SavedItem child in root.Children)
            {
                var existing = child as GroupItem;
                if (existing != null && string.Equals(child.DisplayName, name, StringComparison.OrdinalIgnoreCase))
                    return existing;
            }

            // AddCopy copies, so the instance that ends up in the tree is not the
            // one handed in — it has to be looked up again afterwards.
            document.SelectionSets.AddCopy(root, new FolderItem { DisplayName = name });

            foreach (SavedItem child in root.Children)
            {
                var created = child as GroupItem;
                if (created != null && string.Equals(child.DisplayName, name, StringComparison.OrdinalIgnoreCase))
                    return created;
            }

            return null;
        }

        /// <summary>
        /// Classifies the loaded model files themselves. This is what answers the
        /// "which of my L01_ARCS / L01_STRC files goes first" question without any
        /// search sets existing yet.
        ///
        /// Every step is wrapped, because the inputs are not as tame as they look.
        /// A model appended from a cloud host carries a source "file name" that is
        /// a URL, and <c>Path.GetFileNameWithoutExtension</c> throws on the
        /// characters in it; a model with no source at all returns empty. Either
        /// used to take the whole list down and leave the panel blank, so a model
        /// that cannot be named now falls back to its root item's display name and
        /// is still listed.
        /// </summary>
        public List<ModelClassification> ClassifyLoadedModels(Document document)
        {
            var results = new List<ModelClassification>();
            if (document == null) return results;

            int count;
            try { count = document.Models.Count; }
            catch (Exception ex) { Log.Error("Could not read the model list", ex); return results; }

            for (int i = 0; i < count; i++)
            {
                var entry = new ModelClassification { Index = i };

                try
                {
                    Model model = document.Models[i];

                    string file = SafeString(delegate { return model.SourceFileName; });
                    if (string.IsNullOrEmpty(file)) file = SafeString(delegate { return model.FileName; });

                    entry.SourcePath = file;
                    entry.SourceName = ShortName(file);

                    if (string.IsNullOrEmpty(entry.SourceName))
                    {
                        // No usable file name: the root item's display name is what
                        // Navisworks itself shows in the selection tree.
                        entry.SourceName = SafeString(delegate
                        {
                            ModelItem root = model.RootItem;
                            return root == null ? "" : root.DisplayName;
                        });
                        entry.Note = "no source file name";
                    }

                    if (string.IsNullOrEmpty(entry.SourceName))
                    {
                        entry.SourceName = "Model " + (i + 1).ToString(CultureInfo.InvariantCulture);
                        entry.Note = "unnamed model";
                    }

                    entry.Identity = _classifier.Classify(entry.SourceName, entry.SourcePath);
                }
                catch (Exception ex)
                {
                    Log.Debug("Could not classify model " + i + ": " + ex.Message);
                    entry.SourceName = "Model " + (i + 1).ToString(CultureInfo.InvariantCulture);
                    entry.Note = ex.Message;
                }

                if (entry.Identity == null)
                {
                    entry.Identity = new FourDName
                    {
                        Description = FourDName.Sanitize(entry.SourceName),
                        Basis = string.IsNullOrEmpty(entry.Note) ? "not classified" : entry.Note
                    };
                }

                results.Add(entry);
            }

            results.Sort(delegate (ModelClassification a, ModelClassification b)
            {
                int order = string.CompareOrdinal(a.Identity.Render(false), b.Identity.Render(false));
                return order != 0 ? order : a.Index.CompareTo(b.Index);
            });

            return results;
        }

        /// <summary>
        /// The file's own name, without throwing on inputs that are not really
        /// paths. Cloud-hosted sources arrive as URLs and network sources as UNC
        /// paths with characters <c>Path</c> rejects outright.
        /// </summary>
        private static string ShortName(string file)
        {
            if (string.IsNullOrEmpty(file)) return "";

            try { return System.IO.Path.GetFileNameWithoutExtension(file); }
            catch (ArgumentException) { }

            int cut = file.LastIndexOfAny(new[] { '\\', '/' });
            string tail = cut >= 0 && cut < file.Length - 1 ? file.Substring(cut + 1) : file;

            int query = tail.IndexOfAny(new[] { '?', '#' });
            if (query > 0) tail = tail.Substring(0, query);

            int dot = tail.LastIndexOf('.');
            return dot > 0 ? tail.Substring(0, dot) : tail;
        }

        private static string SafeString(Func<string> read)
        {
            try { return read() ?? ""; }
            catch (Exception) { return ""; }
        }
    }

    /// <summary>One loaded model file and the 4D identity read off its name.</summary>
    public class ModelClassification
    {
        public int Index;
        public string SourceName = "";
        public string SourcePath = "";
        public string Note = "";
        public FourDName Identity;

        /// <summary>The fixed-width line the naming panel lists.</summary>
        public string Line
        {
            get
            {
                string code = Identity == null ? "" : Identity.Render(false);
                string why = Identity != null && Identity.IsResolved
                    ? ""
                    : "   [" + (string.IsNullOrEmpty(Note) ? (Identity == null ? "" : Identity.Basis) : Note) + "]";

                return code.PadRight(28) + SourceName + why;
            }
        }
    }
}
