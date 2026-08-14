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

        /// <summary>
        /// Classifies the loaded model files themselves. This is what answers the
        /// "which of my L01_ARCS / L01_STRC files goes first" question without any
        /// search sets existing yet.
        /// </summary>
        public List<FourDName> ClassifyLoadedModels(Document document)
        {
            var names = new List<FourDName>();
            if (document == null) return names;

            for (int i = 0; i < document.Models.Count; i++)
            {
                Model model = document.Models[i];
                string file = model.SourceFileName;
                if (string.IsNullOrEmpty(file)) file = model.FileName;
                if (string.IsNullOrEmpty(file)) continue;

                names.Add(_classifier.Classify(System.IO.Path.GetFileNameWithoutExtension(file)));
            }

            names.Sort((a, b) => string.CompareOrdinal(a.Render(false), b.Render(false)));
            return names;
        }
    }
}
