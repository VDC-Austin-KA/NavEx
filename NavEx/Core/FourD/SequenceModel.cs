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
            new Activity("EXCV", "Excavation",              "CIVL",  0,  10, "EXCAVAT", "DIG", "MASSEX"),
            new Activity("FOUN", "Foundations",             "STRC",  0,  20, "FOUND", "FOOTING", "PILE", "CAISSON", "MAT"),
            new Activity("SOGR", "Slab on grade",           "STRC",  0,  30, "SOG", "SLABONGRADE"),
            new Activity("UGUT", "Underground utilities",   "CIVL",  0,  40, "UNDERGROUND", "UGUTIL", "SITEUTIL"),

            // Temporary works lead the cycle they serve.
            new Activity("TCRN", "Tower crane",             "TEMP",  0,   5, "CRANE", "TOWERCRANE"),
            new Activity("HOST", "Hoist",                   "TEMP",  0,   6, "HOIST", "BUCKHOIST"),
            new Activity("SHOR", "Shoring",                 "TEMP",  0,   7, "SHORING", "RESHORE"),

            // The superstructure cycle. Structure is the leading edge: lag 0.
            new Activity("CORE", "Core / shear walls",      "STRC",  0,  10, "SHEARWALL", "COREWALL", "JUMPFORM"),
            new Activity("COLS", "Columns",                 "STRC",  0,  20, "COLUMN", "COL"),
            new Activity("FRAM", "Structural frame",        "STRC",  0,  30, "STEEL", "FRAME", "BEAM", "GIRDER", "ERECT"),
            new Activity("DECK", "Deck / slab",             "STRC",  0,  40, "SLAB", "METALDECK", "POUR", "TOPPING"),

            // Skin and enclosure trail the structure far enough that the deck
            // below has cured and the hoist is clear.
            new Activity("SPRY", "Fireproofing",            "STRC",  2,  50, "FIREPROOF", "SFRM", "INTUMESCENT"),
            new Activity("MEPH", "MEP overhead rough-in",   "MECH",  3,  60, "OVERHEAD", "ROUGHIN", "ROUGH", "MAINS", "RISER"),
            new Activity("FIRE", "Sprinkler rough-in",      "FIRE",  3,  62, "SPRINKLER", "SPRK", "FIREMAIN"),
            new Activity("PLBR", "Plumbing rough-in",       "PLBG",  3,  64, "PLUMBROUGH", "WASTE", "VENT", "DOMESTIC"),
            new Activity("ELER", "Electrical rough-in",     "ELEC",  3,  66, "CONDUIT", "CABLETRAY", "BUSDUCT", "FEEDER"),
            new Activity("FRMG", "Interior framing",        "ARCS",  3,  68, "FRAMING", "STUD", "METALSTUD", "PARTITION", "LAYOUT"),
            new Activity("INWL", "In-wall rough-in",        "MECH",  4,  70, "INWALL", "BOXES", "STUBOUT"),
            new Activity("CWAL", "Curtain wall / glazing",  "ARCS",  5,  72, "CURTAINWALL", "GLAZ", "GLASS", "FACADE", "WINDOW", "STOREFRONT", "SKIN", "ENCLOSURE"),
            new Activity("EXTW", "Exterior wall / cladding","ARCS",  5,  74, "CLADDING", "PANEL", "MASONRY", "BRICK", "PRECAST", "EIFS"),
            new Activity("ROOF", "Roofing",                 "ARCS",  5,  76, "ROOFING", "MEMBRANE", "TPO"),

            // Interior build-out, after the floor is dried in.
            new Activity("INSU", "Insulation",              "ARCS",  6,  78, "INSULAT", "BATT"),
            new Activity("DRYW", "Drywall / board",         "ARCS",  6,  80, "DRYWALL", "GYPSUM", "GWB", "BOARD", "SHEETROCK"),
            new Activity("TAPE", "Tape and finish",         "ARCS",  6,  82, "TAPING", "FINISHDRYWALL", "SKIM"),
            new Activity("CEIL", "Ceiling grid",            "ARCS",  7,  84, "CEILING", "ACT", "GRID", "SOFFIT"),
            new Activity("PANT", "Paint",                   "ARCS",  7,  86, "PAINTING", "COATING"),
            new Activity("FLOR", "Flooring",                "ARCS",  8,  88, "FLOORING", "CARPET", "TILE", "RESILIENT", "TERRAZZO"),
            new Activity("MILL", "Millwork / casework",     "INTR",  8,  90, "CASEWORK", "MILLWORK", "CABINET"),
            new Activity("DOOR", "Doors and hardware",      "ARCS",  8,  91, "DOORS", "HARDWARE", "FRAMES"),
            new Activity("MEPT", "MEP trim / devices",      "MECH",  9,  92, "TRIM", "DEVICE", "FIXTURE", "DIFFUSER", "GRILLE", "REGISTER"),
            new Activity("ELET", "Electrical trim",         "ELEC",  9,  93, "LIGHTING", "LIGHT", "SWITCH", "RECEPT", "PANELBOARD"),
            new Activity("EQIP", "Equipment set",           "EQPT",  9,  94, "EQUIPMENT", "AHU", "RTU", "CHILLER", "PUMP", "GENERATOR"),
            new Activity("VERT", "Elevators",               "VERT",  9,  95, "ELEVATOR", "LIFT", "ESCALATOR"),
            new Activity("COMM", "Commissioning",           "MECH", 11,  96, "COMMISSION", "CX", "TESTING", "BALANCE", "TAB"),
            new Activity("FINL", "Final finishes",          "ARCS", 12,  98, "PUNCH", "FINAL", "CLEAN", "TURNOVER"),
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

        /// <summary>Floors the structure gains while the trailing trades work one floor.</summary>
        public double CycleDaysPerFloor = 5.0;

        public Activity Resolve(string activityCode)
        {
            Activity baseActivity = SequenceModel.FindActivity(activityCode);
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

        public IEnumerable<Activity> AllResolved()
        {
            foreach (Activity activity in SequenceModel.Activities)
                yield return Resolve(activity.Code) ?? activity;
        }
    }
}
