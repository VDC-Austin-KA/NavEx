using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace NavEx.FourD
{
    /// <summary>
    /// A construction discipline, with the four-letter code that appears in model
    /// and set names. Codes follow the common US convention (and the AutoNAV
    /// dictionary) so existing file names classify without renaming anything.
    /// </summary>
    public class Discipline
    {
        public string Code;          // STRC, ARCS, MECH…
        public string DisplayName;   // Structural, Architectural…
        public string[] Aliases;     // tokens seen in real file names

        public Discipline(string code, string displayName, params string[] aliases)
        {
            Code = code;
            DisplayName = displayName;
            Aliases = aliases ?? new string[0];
        }
    }

    /// <summary>
    /// A unit of work within one floor cycle — the thing that actually gets
    /// sequenced. <see cref="LagFloors"/> is how many floors behind the structural
    /// leading edge this activity runs; <see cref="CycleOrder"/> is its position
    /// within a single floor's cycle.
    ///
    /// Those two numbers are the whole sequencing model. On a high-rise the
    /// structure races ahead and every following trade trails it by a roughly
    /// constant number of floors, so "when does activity A on floor L happen" is
    /// well approximated by the floor the structure has reached at that moment:
    ///
    ///     time index = L + LagFloors
    ///
    /// Slab on floor 8, interior framing on floor 5 and curtain wall on floor 3
    /// therefore all land at time index 8 — which is exactly what you see on site.
    /// </summary>
    public class Activity
    {
        public string Code;            // DECK, FRMG, CWAL…
        public string DisplayName;
        public string DisciplineCode;  // owning discipline
        public int LagFloors;          // floors behind the structural leading edge
        public int CycleOrder;         // order within one floor cycle (10, 20, 30…)
        public string TaskType;        // TimeLiner task type: Construct / Demolish / Temporary
        public string[] Aliases;       // tokens in file, set and schedule-task names

        public Activity(string code, string displayName, string disciplineCode,
                        int lagFloors, int cycleOrder, params string[] aliases)
        {
            Code = code;
            DisplayName = displayName;
            DisciplineCode = disciplineCode;
            LagFloors = lagFloors;
            CycleOrder = cycleOrder;
            TaskType = "Construct";
            Aliases = aliases ?? new string[0];
        }
    }

    /// <summary>
    /// The default sequencing table plus the code generator.
    ///
    /// Everything here is a starting point, not gospel: lags and orders vary by
    /// project, delivery method and crew loading, so <see cref="SequenceProfile"/>
    /// makes the whole table editable and saveable. What must not change is the
    /// *shape* of the code, because that is what makes a folder listing sort into
    /// build order.
    /// </summary>
    public static class SequenceModel
    {
        // ── Disciplines ──────────────────────────────────────────────────────
        public static readonly Discipline[] Disciplines =
        {
            new Discipline("CIVL", "Civil / Sitework",   "CIVL", "CIVIL", "SITE", "CV", "C"),
            new Discipline("STRC", "Structural",          "STRC", "STRUCT", "STRUCTURAL", "STR", "S"),
            new Discipline("ARCS", "Architectural",       "ARCS", "ARCH", "ARCHITECTURAL", "AR", "A"),
            new Discipline("INTR", "Interiors",           "INTR", "INTERIOR", "INTERIORS", "ID"),
            new Discipline("MECH", "Mechanical / HVAC",   "MECH", "HVAC", "MECHANICAL", "MP", "M"),
            new Discipline("PLBG", "Plumbing",            "PLBG", "PLUM", "PLUMB", "PLUMBING", "PL", "P"),
            new Discipline("ELEC", "Electrical",          "ELEC", "ELECTRICAL", "EL", "E"),
            new Discipline("FIRE", "Fire Protection",     "FIRE", "FP", "SPRK", "SPRINKLER", "FIREPROTECTION"),
            new Discipline("TELE", "Telecommunications",  "TELE", "TELECOM", "COMM", "IT", "DATA", "LV"),
            new Discipline("SECU", "Security",            "SECU", "SEC", "SECURITY", "ACS"),
            new Discipline("AUDV", "Audio / Visual",      "AUDV", "AV", "AUDIOVISUAL"),
            new Discipline("LAND", "Landscape",           "LAND", "LSCP", "LANDSCAPE"),
            new Discipline("EQPT", "Equipment / FF&E",    "EQPT", "EQUIP", "FFE", "FF&E"),
            new Discipline("VERT", "Conveying / Elevators","VERT", "ELEV", "ELEVATOR", "LIFT"),
            new Discipline("TEMP", "Temporary works",     "TEMP", "TMP", "SHORING", "CRANE", "HOIST"),

            // Every model has content that belongs to no trade — generic models,
            // proxies, survey points, the "everything else" set someone made at
            // 4pm. Naming that honestly is better than leaving it unresolved,
            // because an unresolved set is invisible to sequencing and to
            // TimeLiner alike.
            new Discipline("MISC", "Miscellaneous",       "MISC", "MISCELLANEOUS", "GENERIC", "UNCLASSIFIED",
                                                          "UNASSIGNED", "OTHER", "GENERICMODEL", "GENERICMODELS"),
        };

        // ── Activities, in the order a floor cycle actually runs ─────────────
        //
        // CycleOrder is a position in the *whole* floor cycle, not within a lag
        // group, and it therefore rises with LagFloors. That relationship is what
        // makes the tie-break read correctly: two activities sharing a time index
        // are concurrent on different floors, and listing them by cycle order
        // shows structure above the framing below it above the skin below that —
        // the same top-down stack you would see standing on site.
        //
        // Get this wrong (say, numbering curtain wall 10 because it leads the
        // enclosure group) and a folder listing puts the skin ahead of the
        // structure it hangs on.
        public static readonly Activity[] Activities =
        {
            // Below-grade and site work: all "lag" ahead of the superstructure,
            // expressed as a negative floor index rather than a lag.
            new Activity("EXCV", "Excavation",              "CIVL",  0,  10, "EXCAVAT", "DIG", "MASSEX",
                         "TOPO", "TOPOGRAPHY", "GRADING", "EARTHWORK"),
            new Activity("FOUN", "Foundations",             "STRC",  0,  20, "FOUND", "FOOTING", "PILE", "CAISSON", "MAT",
                         "FOUNDATION", "FOUNDATIONS", "FOOTINGS", "STRUCTURALFOUNDATION", "PILECAP", "GRADEBEAM"),
            new Activity("SOGR", "Slab on grade",           "STRC",  0,  30, "SOG", "SLABONGRADE", "GROUNDSLAB"),
            new Activity("UGUT", "Underground utilities",   "CIVL",  0,  40, "UNDERGROUND", "UGUTIL", "SITEUTIL",
                         "STORMDRAIN", "SANITARYSEWER", "WATERMAIN"),

            // Temporary works lead the cycle they serve.
            new Activity("TCRN", "Tower crane",             "TEMP",  0,   5, "CRANE", "TOWERCRANE"),
            new Activity("HOST", "Hoist",                   "TEMP",  0,   6, "HOIST", "BUCKHOIST"),
            new Activity("SHOR", "Shoring",                 "TEMP",  0,   7, "SHORING", "RESHORE"),

            // The superstructure cycle. Structure is the leading edge: lag 0.
            //
            // "Structural wall" and "concrete wall" live here rather than with the
            // generic WALL activity below, and win because they are longer aliases:
            // a shear wall is structure and must not sequence with partitions.
            new Activity("CORE", "Core / shear walls",      "STRC",  0,  10, "SHEARWALL", "COREWALL", "JUMPFORM",
                         "SHEARWALLS", "STRUCTURALWALL", "STRUCTURALWALLS", "CONCRETEWALL", "CONCRETEWALLS"),
            new Activity("COLS", "Columns",                 "STRC",  0,  20, "COLUMN", "COL",
                         "COLUMNS", "STRUCTURALCOLUMN", "STRUCTURALCOLUMNS"),
            new Activity("FRAM", "Structural frame",        "STRC",  0,  30, "STEEL", "FRAME", "BEAM", "GIRDER", "ERECT",
                         "STRUCTURALFRAMING", "JOIST", "JOISTS", "TRUSS", "TRUSSES", "BRACING",
                         "STRUCTURALCONNECTION", "STRUCTURALCONNECTIONS"),

            // "Floors" is the Revit/Navisworks category for the structural floor,
            // not the finish — that is FLOR. The level matcher consumes any
            // "Level 3" / "3rd floor" phrasing before this runs, so a bare FLOOR
            // token reaching here really is the category name.
            new Activity("DECK", "Deck / slab",             "STRC",  0,  40, "SLAB", "METALDECK", "POUR", "TOPPING",
                         "SLABS", "DECKING", "FLOOR", "FLOORS", "STRUCTURALFLOOR", "FLOORSLAB"),

            // Stairs go in with the frame they hang off, well before the finishes
            // that eventually cover them.
            new Activity("STAR", "Stairs and ramps",        "ARCS",  1,  45, "STAIR", "STAIRS", "STAIRCASE",
                         "STAIRWELL", "STAIRRUN", "RAMP", "RAMPS"),

            // Skin and enclosure trail the structure far enough that the deck
            // below has cured and the hoist is clear.
            new Activity("SPRY", "Fireproofing",            "STRC",  2,  50, "FIREPROOF", "SFRM", "INTUMESCENT"),
            new Activity("MEPH", "MEP overhead rough-in",   "MECH",  3,  60, "OVERHEAD", "ROUGHIN", "ROUGH", "MAINS", "RISER",
                         "DUCT", "DUCTS", "DUCTWORK", "DUCTFITTING", "DUCTFITTINGS", "FLEXDUCT", "DUCTACCESSORY"),
            new Activity("FIRE", "Sprinkler rough-in",      "FIRE",  3,  62, "SPRINKLER", "SPRK", "FIREMAIN",
                         "SPRINKLERS", "STANDPIPE"),
            new Activity("PLBR", "Plumbing rough-in",       "PLBG",  3,  64, "PLUMBROUGH", "WASTE", "VENT", "DOMESTIC",
                         "PIPE", "PIPES", "PIPING", "PIPEFITTING", "PIPEFITTINGS", "PIPEACCESSORY",
                         "PIPEINSULATION", "SANITARY", "STORM"),
            new Activity("ELER", "Electrical rough-in",     "ELEC",  3,  66, "CONDUIT", "CABLETRAY", "BUSDUCT", "FEEDER",
                         "CONDUITS", "CABLETRAYS", "CABLETRAYFITTING", "WIREWAY", "ELECTRICALCONDUIT"),
            new Activity("FRMG", "Interior framing",        "ARCS",  3,  68, "FRAMING", "STUD", "METALSTUD", "PARTITION", "LAYOUT",
                         "PARTITIONS", "METALSTUDS", "COLDFORMED"),

            // The generic "Walls" category. Deliberately the shortest wall alias in
            // the table, so every more specific wall — curtain, exterior,
            // structural — outranks it rather than being swallowed by it.
            new Activity("WALL", "Walls / partitions",      "ARCS",  3,  69, "WALL", "WALLS", "BASICWALL",
                         "BASICWALLS", "WALLTYPE", "INTERIORWALL", "INTERIORWALLS", "STACKEDWALL"),

            new Activity("INWL", "In-wall rough-in",        "MECH",  4,  70, "INWALL", "BOXES", "STUBOUT"),
            new Activity("CWAL", "Curtain wall / glazing",  "ARCS",  5,  72, "CURTAINWALL", "GLAZ", "GLASS", "FACADE", "WINDOW", "STOREFRONT", "SKIN", "ENCLOSURE",
                         "CURTAINWALLS", "CURTAINPANEL", "CURTAINPANELS", "MULLION", "MULLIONS",
                         "CURTAINWALLMULLION", "WINDOWS", "GLAZING"),
            new Activity("EXTW", "Exterior wall / cladding","ARCS",  5,  74, "CLADDING", "PANEL", "MASONRY", "BRICK", "PRECAST", "EIFS",
                         "EXTERIORWALL", "EXTERIORWALLS", "RAINSCREEN", "METALPANEL", "STUCCO"),
            new Activity("ROOF", "Roofing",                 "ARCS",  5,  76, "ROOFING", "MEMBRANE", "TPO", "ROOFS"),

            // Interior build-out, after the floor is dried in.
            new Activity("INSU", "Insulation",              "ARCS",  6,  78, "INSULAT", "BATT"),
            new Activity("DRYW", "Drywall / board",         "ARCS",  6,  80, "DRYWALL", "GYPSUM", "GWB", "BOARD", "SHEETROCK"),
            new Activity("TAPE", "Tape and finish",         "ARCS",  6,  82, "TAPING", "FINISHDRYWALL", "SKIM"),
            new Activity("CEIL", "Ceiling grid",            "ARCS",  7,  84, "CEILING", "ACT", "GRID", "SOFFIT",
                         "CEILINGS", "CEILINGGRID", "ACOUSTICCEILING"),
            new Activity("PANT", "Paint",                   "ARCS",  7,  86, "PAINTING", "COATING"),
            new Activity("FLOR", "Flooring",                "ARCS",  8,  88, "FLOORING", "CARPET", "TILE", "RESILIENT", "TERRAZZO",
                         "FLOORFINISH", "FLOORFINISHES", "FINISHFLOOR"),
            new Activity("MILL", "Millwork / casework",     "INTR",  8,  90, "CASEWORK", "MILLWORK", "CABINET",
                         "CABINETRY", "COUNTERTOP", "FURNITURE", "FURNITURESYSTEM", "FURNITURESYSTEMS"),
            new Activity("DOOR", "Doors and hardware",      "ARCS",  8,  91, "DOORS", "HARDWARE", "FRAMES"),

            // Railings arrive with the finishes, after the stair and floor
            // surfaces they land on are in.
            new Activity("RAIL", "Railings and handrails",  "ARCS",  8,  89, "RAIL", "RAILS", "RAILING", "RAILINGS",
                         "HANDRAIL", "HANDRAILS", "GUARDRAIL", "GUARDRAILS", "BALUSTRADE", "TOPRAIL"),

            new Activity("MEPT", "MEP trim / devices",      "MECH",  9,  92, "TRIM", "DEVICE", "FIXTURE", "DIFFUSER", "GRILLE", "REGISTER",
                         "AIRTERMINAL", "AIRTERMINALS", "PLUMBINGFIXTURE", "PLUMBINGFIXTURES", "MECHANICALDEVICE"),
            new Activity("ELET", "Electrical trim",         "ELEC",  9,  93, "LIGHTING", "LIGHT", "SWITCH", "RECEPT", "PANELBOARD",
                         "LIGHTINGFIXTURE", "LIGHTINGFIXTURES", "ELECTRICALFIXTURE", "ELECTRICALFIXTURES",
                         "ELECTRICALDEVICE", "ELECTRICALDEVICES"),
            new Activity("EQIP", "Equipment set",           "EQPT",  9,  94, "EQUIPMENT", "AHU", "RTU", "CHILLER", "PUMP", "GENERATOR",
                         "MECHANICALEQUIPMENT", "ELECTRICALEQUIPMENT", "PLUMBINGEQUIPMENT",
                         "SPECIALTYEQUIPMENT", "BOILER"),
            new Activity("VERT", "Elevators",               "VERT",  9,  95, "ELEVATOR", "LIFT", "ESCALATOR"),
            new Activity("COMM", "Commissioning",           "MECH", 11,  96, "COMMISSION", "CX", "TESTING", "BALANCE", "TAB"),
            new Activity("LSCP", "Landscape / hardscape",   "LAND", 12,  97, "LANDSCAPE", "LANDSCAPING", "PLANTING",
                         "HARDSCAPE", "PAVING", "SIDEWALK", "CURB"),
            new Activity("FINL", "Final finishes",          "ARCS", 12,  98, "PUNCH", "FINAL", "CLEAN", "TURNOVER"),

            // The catch-all. Last in the cycle on purpose: content nobody has
            // classified should not sort ahead of work that has been.
            new Activity("MISC", "Miscellaneous",           "MISC",  6,  99, "MISC", "MISCELLANEOUS", "GENERIC",
                         "GENERICMODEL", "GENERICMODELS", "UNCLASSIFIED", "UNASSIGNED", "SPECIALTY",
                         "MISCMETAL", "MISCELLANEOUSMETAL"),
        };

        /// <summary>Levels that are not numbered floors, and where they sit in the build.</summary>
        public const int SiteLevelIndex = -3;
        public const int BasementBaseIndex = -2;
        public const int RoofLevelOffset = 1;

        private static readonly Dictionary<string, Discipline> DisciplineByCode =
            Disciplines.ToDictionary(d => d.Code, StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<string, Activity> ActivityByCode =
            Activities.ToDictionary(a => a.Code, StringComparer.OrdinalIgnoreCase);

        public static Discipline FindDiscipline(string code)
        {
            if (string.IsNullOrEmpty(code)) return null;
            Discipline discipline;
            return DisciplineByCode.TryGetValue(code, out discipline) ? discipline : null;
        }

        public static Activity FindActivity(string code)
        {
            if (string.IsNullOrEmpty(code)) return null;
            Activity activity;
            return ActivityByCode.TryGetValue(code, out activity) ? activity : null;
        }

        /// <summary>
        /// The global time index for an activity on a level: the floor the
        /// structural leading edge has reached when this work happens.
        /// </summary>
        public static int TimeIndex(int levelIndex, Activity activity)
        {
            int lag = activity == null ? 0 : activity.LagFloors;
            return levelIndex + lag;
        }

        /// <summary>
        /// The sort key that makes a directory listing read as a build sequence.
        ///
        /// Five digits: three for the time index and two for the position within
        /// that floor cycle. The time index is biased by <see cref="IndexBias"/> so
        /// basements and sitework — which carry negative level indices — still
        /// produce non-negative, zero-padded, correctly ordering numbers.
        /// </summary>
        public const int IndexBias = 20;

        public static string SequenceCode(int levelIndex, Activity activity)
        {
            int time = TimeIndex(levelIndex, activity) + IndexBias;
            if (time < 0) time = 0;
            if (time > 999) time = 999;

            int order = activity == null ? 50 : activity.CycleOrder;
            if (order < 0) order = 0;
            if (order > 99) order = 99;

            return time.ToString("000", CultureInfo.InvariantCulture)
                 + order.ToString("00", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// A project's editable copy of the sequencing table. Lags and cycle orders
    /// are the two numbers a scheduler will always want to argue with, so they are
    /// overridable per activity and persisted with the rest of the settings.
    /// </summary>
    public class SequenceProfile
    {
        public string Name = "Default";
        public readonly Dictionary<string, int> LagOverrides = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, int> OrderOverrides = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The project's own vocabulary. Anything defined here shadows the built-in
        /// table under the same code, so a site can redefine what NavEx ships with
        /// rather than working around it.
        /// </summary>
        public IdentifierLibrary Identifiers = new IdentifierLibrary();

        /// <summary>Floors the structure gains while the trailing trades work one floor.</summary>
        public double CycleDaysPerFloor = 5.0;

        /// <summary>
        /// The activity as defined, before lag/order overrides — a custom
        /// definition if there is one, otherwise the built-in.
        /// </summary>
        public Activity FindDefinition(string activityCode)
        {
            if (string.IsNullOrEmpty(activityCode)) return null;

            if (Identifiers != null)
            {
                foreach (Activity custom in Identifiers.Activities)
                    if (string.Equals(custom.Code, activityCode, StringComparison.OrdinalIgnoreCase))
                        return custom;
            }

            return SequenceModel.FindActivity(activityCode);
        }

        public Discipline FindDisciplineDefinition(string disciplineCode)
        {
            if (string.IsNullOrEmpty(disciplineCode)) return null;

            if (Identifiers != null)
            {
                foreach (Discipline custom in Identifiers.Disciplines)
                    if (string.Equals(custom.Code, disciplineCode, StringComparison.OrdinalIgnoreCase))
                        return custom;
            }

            return SequenceModel.FindDiscipline(disciplineCode);
        }

        public Activity Resolve(string activityCode)
        {
            Activity baseActivity = FindDefinition(activityCode);
            if (baseActivity == null) return null;

            int lag = baseActivity.LagFloors;
            int order = baseActivity.CycleOrder;

            int overridden;
            if (LagOverrides.TryGetValue(activityCode, out overridden)) lag = overridden;
            if (OrderOverrides.TryGetValue(activityCode, out overridden)) order = overridden;

            if (lag == baseActivity.LagFloors && order == baseActivity.CycleOrder)
                return baseActivity;

            return new Activity(baseActivity.Code, baseActivity.DisplayName, baseActivity.DisciplineCode,
                                lag, order, baseActivity.Aliases)
            {
                TaskType = baseActivity.TaskType
            };
        }

        /// <summary>
        /// Every activity the classifier and the sequencing table should know
        /// about: custom definitions first so they shadow same-coded built-ins.
        /// </summary>
        public IEnumerable<Activity> AllResolved()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (Identifiers != null)
            {
                foreach (Activity custom in Identifiers.Activities)
                {
                    if (custom == null || string.IsNullOrEmpty(custom.Code)) continue;
                    if (!seen.Add(custom.Code)) continue;
                    yield return Resolve(custom.Code) ?? custom;
                }
            }

            foreach (Activity activity in SequenceModel.Activities)
            {
                if (!seen.Add(activity.Code)) continue;
                yield return Resolve(activity.Code) ?? activity;
            }
        }

        /// <summary>Custom disciplines first, then the built-ins they do not shadow.</summary>
        public IEnumerable<Discipline> AllDisciplines()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (Identifiers != null)
            {
                foreach (Discipline custom in Identifiers.Disciplines)
                {
                    if (custom == null || string.IsNullOrEmpty(custom.Code)) continue;
                    if (!seen.Add(custom.Code)) continue;
                    yield return custom;
                }
            }

            foreach (Discipline discipline in SequenceModel.Disciplines)
            {
                if (!seen.Add(discipline.Code)) continue;
                yield return discipline;
            }
        }

        /// <summary>Enabled rules, most specific first. Empty when no library is loaded.</summary>
        public IEnumerable<IdentifierRule> ActiveRules()
        {
            if (Identifiers == null) return new IdentifierRule[0];
            return Identifiers.Rules
                .Where(r => r != null && r.Enabled && r.IsUsable)
                .OrderByDescending(r => r.EffectivePriority);
        }
    }
}
