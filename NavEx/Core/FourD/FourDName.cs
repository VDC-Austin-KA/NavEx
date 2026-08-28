using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace NavEx.FourD
{
    /// <summary>
    /// A parsed or generated 4D identity: where a package sits in the build, on
    /// which level, for which discipline, doing what.
    ///
    /// The rendered form is deliberately fixed-width and left-weighted:
    ///
    ///     SSSSS_ZONE_LEVEL_DISC_ACTV_Free text
    ///     00840_A_L05_ARCS_FRMG_Interior framing
    ///
    /// Sorting that string ASCII-ascending yields construction order, because the
    /// leading five digits already encode "structure floor at the time this
    /// happens" followed by "position within that floor's cycle". Everything after
    /// the sequence block is for humans and never affects ordering — which is why
    /// the zone, level and discipline blocks are fixed width too.
    /// </summary>
    public class FourDName
    {
        public string SequenceCode = "00000";
        public string Zone = "";              // optional; "A", "TOWER", "" for none
        public int LevelIndex;                // 1 = L01, 0 = ground/site, negative = below grade
        public string LevelTag = "L00";
        public string DisciplineCode = "";
        public string ActivityCode = "";
        public string Description = "";

        /// <summary>How the classifier arrived at this — shown in the review grid.</summary>
        public string Basis = "";
        public double Confidence;

        public bool IsResolved
        {
            get { return !string.IsNullOrEmpty(DisciplineCode) && !string.IsNullOrEmpty(ActivityCode); }
        }

        public string Render(bool includeDescription)
        {
            var sb = new StringBuilder();
            sb.Append(SequenceCode);
            if (!string.IsNullOrEmpty(Zone)) sb.Append('_').Append(Zone);
            sb.Append('_').Append(LevelTag);
            if (!string.IsNullOrEmpty(DisciplineCode)) sb.Append('_').Append(DisciplineCode);
            if (!string.IsNullOrEmpty(ActivityCode)) sb.Append('_').Append(ActivityCode);
            if (includeDescription && !string.IsNullOrEmpty(Description))
                sb.Append('_').Append(Sanitize(Description));
            return sb.ToString();
        }

        public override string ToString() { return Render(true); }

        /// <summary>Strips anything that would break a file name or an OBJ/MTL token.</summary>
        public static string Sanitize(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            var sb = new StringBuilder(value.Length);
            foreach (char c in value)
            {
                if (char.IsLetterOrDigit(c)) sb.Append(c);
                else if (c == '-' || c == '.' || c == '&') sb.Append(c);
                else if (char.IsWhiteSpace(c) || c == '_') sb.Append(' ');
            }

            // Collapse runs of spaces, then hyphenate so the result stays a single token.
            string collapsed = Regex.Replace(sb.ToString().Trim(), @"\s+", "-");
            return collapsed;
        }

        /// <summary>
        /// Reads back a name this class produced. Used so a second run is
        /// idempotent — already-coded sets keep their identity instead of being
        /// re-derived from a name that now starts with digits.
        /// </summary>
        public static FourDName TryParse(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;

            Match match = Regex.Match(value.Trim(),
                @"^(?<seq>\d{5})_(?:(?<zone>[A-Za-z0-9]+)_)??(?<level>L-?\d{2}|SITE|ROOF|BLDG|ALL)(?:_(?<disc>[A-Z]{4}))?(?:_(?<act>[A-Z]{4}))?(?:_(?<desc>.*))?$",
                RegexOptions.None);
            if (!match.Success) return null;

            var name = new FourDName
            {
                SequenceCode = match.Groups["seq"].Value,
                Zone = match.Groups["zone"].Success ? match.Groups["zone"].Value : "",
                LevelTag = match.Groups["level"].Value.ToUpperInvariant(),
                DisciplineCode = match.Groups["disc"].Success ? match.Groups["disc"].Value : "",
                ActivityCode = match.Groups["act"].Success ? match.Groups["act"].Value : "",
                Description = match.Groups["desc"].Success ? match.Groups["desc"].Value : "",
                Basis = "already coded",
                Confidence = 1.0
            };
            name.LevelIndex = LevelIndexFromTag(name.LevelTag);
            return name;
        }

        public static string LevelTagFor(int levelIndex)
        {
            if (levelIndex == SequenceModel.SiteLevelIndex) return "SITE";
            if (levelIndex == 99) return "ROOF";
            if (levelIndex < 0) return "L-" + Math.Abs(levelIndex).ToString("00", CultureInfo.InvariantCulture);
            return "L" + levelIndex.ToString("00", CultureInfo.InvariantCulture);
        }

        public static int LevelIndexFromTag(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return 0;
            tag = tag.ToUpperInvariant();
            if (tag == "SITE") return SequenceModel.SiteLevelIndex;
            if (tag == "ROOF") return 99;
            if (tag == "BLDG" || tag == "ALL") return 0;

            Match match = Regex.Match(tag, @"^L(-?\d+)$");
            int index;
            if (match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.Integer,
                                              CultureInfo.InvariantCulture, out index))
                return index;
            return 0;
        }
    }

    /// <summary>
    /// Turns a real-world name — a loaded model's file name, a search set name, or
    /// a schedule task name — into a <see cref="FourDName"/>.
    ///
    /// The approach is AutoNAV's, widened. AutoNAV splits a file name on its
    /// separators and matches the resulting tokens against a discipline
    /// dictionary, falling back to a user picker for anything it cannot place.
    /// The same idea works for level and work scope, and matching all three from
    /// one token pass means `L01_ARCS` and `Level 1 - Architectural - Framing`
    /// resolve identically.
    ///
    /// Nothing here guesses silently: every result carries a
    /// <see cref="FourDName.Confidence"/> and a human-readable
    /// <see cref="FourDName.Basis"/>, and unresolved parts are left blank for the
    /// review grid rather than filled with a plausible-looking default.
    /// </summary>
    public class NameClassifier
    {
        private readonly SequenceProfile _profile;

        // Learned overrides: token -> code. Populated from the review grid, so
        // correcting "SS" to STRC once fixes it everywhere and on later runs.
        public readonly Dictionary<string, string> DisciplineOverrides =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, string> ActivityOverrides =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public NameClassifier(SequenceProfile profile)
        {
            _profile = profile ?? new SequenceProfile();
        }

        /// <summary>The sequencing table and identifier library this classifier reads.</summary>
        public SequenceProfile Profile { get { return _profile; } }

        public FourDName Classify(string rawName) { return Classify(rawName, null); }

        /// <summary>
        /// <paramref name="context"/> is extra text that should influence the match
        /// but not the description — a schedule task's notes, or the source file
        /// name of a search set.
        /// </summary>
        public FourDName Classify(string rawName, string context)
        {
            FourDName existing = FourDName.TryParse(rawName);
            if (existing != null) return existing;

            string combined = (rawName ?? "") + " " + (context ?? "");
            var basis = new List<string>();
            double score = 0;

            // The level is read first, and the phrase that produced it is taken
            // out of the text before anything else looks at it.
            //
            // That ordering is what makes the plain category names work. "Floor"
            // is both a level word and the Revit category for a structural slab,
            // so "3rd floor plumbing" and "Floors" have to be told apart — and the
            // only reliable difference is that the first one already spent its
            // FLOOR token on a level.
            int levelIndex;
            string residual;
            bool levelFound = TryMatchLevel(combined, null, out levelIndex, out residual);
            if (levelFound) { basis.Add("level " + FourDName.LevelTagFor(levelIndex)); score += 0.3; }

            // Aliases are stored concatenated ("CURTAINWALL"), but real names
            // separate the words ("Curtain Wall"). Adding the whole string with
            // its separators stripped lets the substring rule bridge the two.
            List<string> tokens = Tokenize(residual);
            tokens.Add(Squash(residual));

            // Rules read the untouched name: a site's own vocabulary may well live
            // inside the part the level matcher consumed.
            List<string> ruleTokens = Tokenize(combined);
            ruleTokens.Add(Squash(combined));

            RuleHit hit = ApplyRules(ruleTokens, Squash(combined), combined, basis, ref score);
            if (hit.LevelIndex.HasValue)
            {
                levelIndex = hit.LevelIndex.Value;
                levelFound = true;
            }

            string disciplineCode = hit.DisciplineCode;
            if (string.IsNullOrEmpty(disciplineCode))
                disciplineCode = MatchDiscipline(tokens, basis, ref score);

            Activity activity = null;
            if (!string.IsNullOrEmpty(hit.ActivityCode))
            {
                activity = _profile.Resolve(hit.ActivityCode);
                if (activity == null) basis.Add("rule names unknown activity '" + hit.ActivityCode + "'");
            }
            if (activity == null)
                activity = MatchActivity(tokens, disciplineCode, basis, ref score);

            // An activity implies its discipline when the name never named one.
            if (string.IsNullOrEmpty(disciplineCode) && activity != null)
            {
                disciplineCode = activity.DisciplineCode;
                basis.Add("discipline from activity");
            }

            var name = new FourDName
            {
                LevelIndex = levelIndex,
                LevelTag = FourDName.LevelTagFor(levelIndex),
                DisciplineCode = disciplineCode ?? "",
                ActivityCode = activity == null ? "" : activity.Code,
                Description = CleanDescription(rawName),
                Basis = basis.Count == 0 ? "no match" : string.Join(", ", basis.ToArray()),
                Confidence = Math.Min(1.0, score)
            };

            name.SequenceCode = SequenceModel.SequenceCode(levelIndex, activity);
            return name;
        }

        /// <summary>Recomputes the sequence code after a manual edit in the review grid.</summary>
        public void Recompute(FourDName name)
        {
            if (name == null) return;
            name.LevelIndex = FourDName.LevelIndexFromTag(name.LevelTag);
            Activity activity = _profile.Resolve(name.ActivityCode);
            name.SequenceCode = SequenceModel.SequenceCode(name.LevelIndex, activity);
        }

        // ── User rules ───────────────────────────────────────────────────────

        /// <summary>What the project's own identifier rules had to say about a name.</summary>
        private struct RuleHit
        {
            public string DisciplineCode;
            public string ActivityCode;
            public int? LevelIndex;
        }

        /// <summary>
        /// Runs the library's rules, most specific first, and takes the first
        /// answer offered for each of the three parts. A rule that only names a
        /// discipline therefore leaves the activity to the next rule, or to the
        /// built-in dictionary — which is what lets a handful of narrow rules
        /// compose instead of each having to restate everything.
        /// </summary>
        private RuleHit ApplyRules(List<string> tokens, string squashed, string raw,
                                   List<string> basis, ref double score)
        {
            var hit = new RuleHit();

            foreach (IdentifierRule rule in _profile.ActiveRules())
            {
                if (hit.DisciplineCode != null && hit.ActivityCode != null && hit.LevelIndex.HasValue) break;
                if (!rule.Matches(tokens, squashed, raw)) continue;

                bool used = false;

                if (hit.DisciplineCode == null && !string.IsNullOrEmpty(rule.DisciplineCode))
                {
                    hit.DisciplineCode = rule.DisciplineCode;
                    used = true;
                }
                if (hit.ActivityCode == null && !string.IsNullOrEmpty(rule.ActivityCode))
                {
                    hit.ActivityCode = rule.ActivityCode;
                    used = true;
                }
                if (!hit.LevelIndex.HasValue && !string.IsNullOrEmpty(rule.LevelTag))
                {
                    hit.LevelIndex = FourDName.LevelIndexFromTag(rule.LevelTag);
                    used = true;
                }

                if (!used) continue;

                basis.Add("rule '" + rule.Pattern + "'");
                score += 0.45;
            }

            return hit;
        }

        // ── Token matching ───────────────────────────────────────────────────

        /// <summary>
        /// Splits on every separator convention in use — underscores, hyphens,
        /// spaces, dots — and additionally on camel-case and letter/digit
        /// boundaries, so "L05ARCH" and "Level05_Arch" both yield usable tokens.
        /// </summary>
        /// <summary>The input reduced to bare alphanumerics, uppercased.</summary>
        public static string Squash(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            var sb = new StringBuilder(value.Length);
            foreach (char c in value)
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToUpperInvariant(c));
            return sb.ToString();
        }

        public static List<string> Tokenize(string value)
        {
            var tokens = new List<string>();
            if (string.IsNullOrEmpty(value)) return tokens;

            foreach (string chunk in Regex.Split(value, @"[^A-Za-z0-9&]+"))
            {
                if (chunk.Length == 0) continue;
                tokens.Add(chunk.ToUpperInvariant());

                foreach (string part in Regex.Split(chunk, @"(?<=[a-z])(?=[A-Z])|(?<=[A-Za-z])(?=\d)|(?<=\d)(?=[A-Za-z])"))
                    if (part.Length > 0 && !string.Equals(part, chunk, StringComparison.OrdinalIgnoreCase))
                        tokens.Add(part.ToUpperInvariant());
            }

            return tokens;
        }

        private string MatchDiscipline(List<string> tokens, List<string> basis, ref double score)
        {
            foreach (string token in tokens)
            {
                string overridden;
                if (DisciplineOverrides.TryGetValue(token, out overridden))
                {
                    basis.Add("discipline '" + token + "' (learned)");
                    score += 0.4;
                    return overridden;
                }
            }

            // Exact alias hits first; a bare "S" or "M" must never outrank "STRC".
            foreach (Discipline discipline in _profile.AllDisciplines())
            {
                foreach (string alias in discipline.Aliases)
                {
                    if (alias.Length < 3) continue;
                    if (tokens.Any(t => string.Equals(t, alias, StringComparison.OrdinalIgnoreCase)))
                    {
                        basis.Add("discipline '" + alias + "'");
                        score += 0.4;
                        return discipline.Code;
                    }
                }
            }

            // Single-letter discipline prefixes are only trusted when they stand
            // alone as a token, which is the AIA sheet-code convention.
            foreach (Discipline discipline in _profile.AllDisciplines())
            {
                foreach (string alias in discipline.Aliases)
                {
                    if (alias.Length > 2) continue;
                    if (tokens.Any(t => string.Equals(t, alias, StringComparison.OrdinalIgnoreCase)))
                    {
                        basis.Add("discipline '" + alias + "' (short code)");
                        score += 0.2;
                        return discipline.Code;
                    }
                }
            }

            return null;
        }

        private Activity MatchActivity(List<string> tokens, string disciplineCode,
                                       List<string> basis, ref double score)
        {
            foreach (string token in tokens)
            {
                string overridden;
                if (ActivityOverrides.TryGetValue(token, out overridden))
                {
                    Activity learned = _profile.Resolve(overridden);
                    if (learned != null)
                    {
                        basis.Add("activity '" + token + "' (learned)");
                        score += 0.4;
                        return learned;
                    }
                }
            }

            Activity best = null;
            int bestAliasLength = 0;
            string bestAlias = null;

            foreach (Activity activity in _profile.AllResolved())
            {
                // The activity's own code counts as an alias.
                if (Matches(tokens, activity.Code) && activity.Code.Length > bestAliasLength)
                {
                    best = activity; bestAliasLength = activity.Code.Length; bestAlias = activity.Code;
                }

                foreach (string alias in activity.Aliases)
                {
                    if (alias.Length <= bestAliasLength) continue;
                    if (!Matches(tokens, alias)) continue;

                    // A longer alias is a more specific match: "CURTAINWALL" must
                    // beat "WALL", and "PLUMBROUGH" must beat "ROUGH".
                    best = activity; bestAliasLength = alias.Length; bestAlias = alias;
                }
            }

            if (best == null) return null;

            // A discipline named in the text disambiguates shared vocabulary —
            // "rough-in" belongs to whichever trade the name already identified.
            if (!string.IsNullOrEmpty(disciplineCode) &&
                !string.Equals(best.DisciplineCode, disciplineCode, StringComparison.OrdinalIgnoreCase))
            {
                Activity sameDiscipline = _profile.AllResolved()
                    .FirstOrDefault(a => string.Equals(a.DisciplineCode, disciplineCode, StringComparison.OrdinalIgnoreCase) &&
                                         (Matches(tokens, a.Code) || a.Aliases.Any(x => Matches(tokens, x))));
                if (sameDiscipline != null)
                {
                    basis.Add("activity '" + sameDiscipline.Code + "' (discipline-aligned)");
                    score += 0.4;
                    return sameDiscipline;
                }
                score += 0.2;
            }
            else
            {
                score += 0.4;
            }

            basis.Add("activity '" + bestAlias + "'");
            return best;
        }

        private static bool Matches(List<string> tokens, string alias)
        {
            if (string.IsNullOrEmpty(alias)) return false;
            foreach (string token in tokens)
            {
                if (string.Equals(token, alias, StringComparison.OrdinalIgnoreCase)) return true;
                // Concatenated names ("CURTAINWALLSOUTH") still need to hit, but
                // only for aliases long enough that a substring is not a coincidence.
                if (alias.Length >= 5 && token.Length > alias.Length &&
                    token.IndexOf(alias, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        /// <summary>
        /// Finds the level. Handles L01 / LVL2 / LEVEL 03 / 3RD FLOOR / FLOOR 4 /
        /// GROUND / ROOF / BASEMENT / B1 / P2, and returns false when the name
        /// simply has no level in it (a site-wide or building-wide package).
        /// </summary>
        private const string Edge = @"(?<![A-Z0-9])";
        private const string EdgeEnd = @"(?![A-Z0-9])";

        public static bool TryMatchLevel(string raw, List<string> tokens, out int levelIndex)
        {
            string residual;
            return TryMatchLevel(raw, tokens, out levelIndex, out residual);
        }

        /// <summary>
        /// As above, and additionally reports the name with the phrase that
        /// produced the level removed.
        ///
        /// Only the numbered forms are consumed. The keyword forms — ROOF, SITE,
        /// GROUND — are left in place because they are the category name as well
        /// as the level: a set called "Roof" has to keep its ROOF token or it
        /// classifies as nothing at all.
        /// </summary>
        public static bool TryMatchLevel(string raw, List<string> tokens, out int levelIndex, out string residual)
        {
            levelIndex = 0;
            residual = raw ?? "";
            if (string.IsNullOrEmpty(raw)) return false;

            string upper = raw.ToUpperInvariant();

            if (Regex.IsMatch(upper, @"\bROOFS?\b|\bPENTHOUSE\b|\bPH\b")) { levelIndex = 99; return true; }
            if (Regex.IsMatch(upper, @"\bSITE\b|\bSITEWORK\b|\bCIVIL\b|\bGRADE\b")) { levelIndex = SequenceModel.SiteLevelIndex; return true; }

            // Below grade: B1 / BSMT / P2 / LEVEL -1.
            //
            // These use explicit character-class boundaries rather than \b because
            // underscore counts as a word character: \b never fires between "L01"
            // and "_STRC", which is the single most common name shape there is.
            Match below = Regex.Match(upper, Edge + @"(?:B|BSMT|BASEMENT|P|PARKING)\s*-?\s*(\d{1,2})" + EdgeEnd);
            if (below.Success)
            {
                int n;
                if (int.TryParse(below.Groups[1].Value, out n) && n > 0)
                {
                    levelIndex = -n;
                    residual = Without(raw, below);
                    return true;
                }
            }
            Match negative = Regex.Match(upper, Edge + @"(?:L|LVL|LEVEL|FLOOR|FL)\s*-\s*(\d{1,2})" + EdgeEnd);
            if (negative.Success)
            {
                int n;
                if (int.TryParse(negative.Groups[1].Value, out n) && n > 0)
                {
                    levelIndex = -n;
                    residual = Without(raw, negative);
                    return true;
                }
            }

            // L01 / LVL2 / LEVEL 03 / FLOOR 4 / FL05.
            Match numbered = Regex.Match(upper, Edge + @"(?:L|LVL|LEVEL|FLOOR|FL)\s*0*(\d{1,3})" + EdgeEnd);
            if (numbered.Success)
            {
                int n;
                if (int.TryParse(numbered.Groups[1].Value, out n))
                {
                    levelIndex = n;
                    residual = Without(raw, numbered);
                    return true;
                }
            }

            // "3RD FLOOR", "12TH FLOOR".
            Match ordinal = Regex.Match(upper, @"\b(\d{1,3})\s*(?:ST|ND|RD|TH)\s*(?:FLOOR|FLR|LEVEL)\b");
            if (ordinal.Success)
            {
                int n;
                if (int.TryParse(ordinal.Groups[1].Value, out n))
                {
                    levelIndex = n;
                    residual = Without(raw, ordinal);
                    return true;
                }
            }

            if (Regex.IsMatch(upper, @"\bGROUND\b|\bLOBBY\b|\bGRND\b|\bGF\b")) { levelIndex = 1; return true; }

            return false;
        }

        /// <summary>
        /// The name with a matched span replaced by a separator. Indexes come from
        /// the uppercased copy, which is the same length as the original for every
        /// character an invariant upper-casing leaves alone; the bounds check keeps
        /// the odd exception from throwing.
        /// </summary>
        private static string Without(string raw, Match match)
        {
            if (match.Index < 0 || match.Index + match.Length > raw.Length) return raw;
            return raw.Substring(0, match.Index) + " " + raw.Substring(match.Index + match.Length);
        }

        private static string CleanDescription(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            string cleaned = Regex.Replace(raw, @"\.(nwc|nwd|nwf|rvt|ifc|dwg)$", "", RegexOptions.IgnoreCase);
            return FourDName.Sanitize(cleaned);
        }
    }
}
