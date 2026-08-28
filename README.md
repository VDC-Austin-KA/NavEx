# NavEx

Export Navisworks search sets, selection sets and the current selection as
lightweight **glTF 2.0** (`.glb` / `.gltf`) or **Wavefront OBJ**, with materials
embedded and geometry accurate to the source tessellation.

Supports **Navisworks Manage 2024 / 2025 / 2026 / 2027**.

---

## What it does

| | |
|---|---|
| **Batch export** | Tick any number of search sets — one file each, or all of them merged into a single file. |
| **Current selection** | Whatever is selected in the model is always available as an export target, no set required. |
| **GLB by default** | One self-contained binary: geometry, normals, materials and provenance in a single file. |
| **glTF / OBJ** | `.gltf` + `.bin`, `.gltf` with an embedded base64 buffer, or `.obj` + `.mtl` for older toolchains. |
| **Materials** | Colour and transparency as Navisworks renders them, including appearance overrides, converted to glTF metallic-roughness and de-duplicated into a small palette. |
| **Lightweight output** | Vertex welding, material merging, 16-bit indices where possible, and no extensions — small files that open everywhere. |
| **Geometric accuracy** | Full double-precision transform chain and automatic recentring, so survey-grid models don't lose centimetres to 32-bit float. |
| **Metadata** | Item identity in glTF `extras`, plus an optional `.properties.json` sidecar carrying every Navisworks property, joinable on item GUID. |
| **Estimate first** | Reports item counts, triangle counts and a rough output size before you commit to a long export. |
| **Presets** | Save and reload named option sets; the last-used settings are restored automatically. |

## Install

Download **`NavEx-Plugin.zip`** from the build artifacts and extract it. Then
right-click **`Install.cmd`** → **Run as administrator**.

```
NavEx-Plugin/
    Install.cmd          <- run this
    Uninstall.cmd
    README.txt
    V24/NavEx.dll        <- Navisworks Manage 2024
    V24/NavEx.addin
    V25/…                <- 2025
    V26/…                <- 2026
    V27/…                <- 2027
```

The installer only does `mkdir` and `copy` into
`C:\Program Files\Autodesk\Navisworks Manage <year>\Plugins\NavEx\`. There is no
embedded payload and no `certutil` step, which is what keeps Windows Defender
from flagging it the way self-extracting installers get flagged. To install by
hand, create that folder and copy the two files in yourself.

Restart Navisworks → **Add-Ins** ribbon tab → **NavEx**.

## Using it

1. **Add-Ins → NavEx.**
2. Tick the sets to export. `◆ Current Selection` sits at the top and is ticked
   automatically when something is selected. Ticking a folder includes every set
   inside it, and the filter box narrows a long list.
3. Pick an output folder and a format.
4. **Estimate** (optional) reports what you're about to export.
5. **Export.**

Defaults: one `.glb` per set, metres, Y-up, recentred on the selection, welded
vertices, merged materials.

### Key options

**Origin** — Navisworks models are often on a survey grid, hundreds of thousands
of units from the origin. glTF stores positions as 32-bit floats, which at that
distance quantise to centimetres or worse; geometry visibly wobbles and
coincident faces separate. NavEx therefore recentres by default and writes the
offset it applied into the file:

```json
"asset": {
  "extras": {
    "navex:appliedOffset": [123456.7, 98765.4, 0],
    "navex:offsetNote": "Add appliedOffset to exported coordinates to return to source world coordinates."
  }
}
```

Add that vector back (before undoing the Y-up swap) to return to Navisworks
world coordinates. Choose *Recentre on whole model* when several sets are
exported separately but must line up when reassembled.

**Structure** — *Merge by material* gives the smallest file and the fewest draw
calls: one node, one mesh, one primitive per material. *One node per item* keeps
Navisworks names and per-object selectability at the cost of size and load time.
*One node per source file* sits in between.

**Welding** — the tessellator emits three independent vertices per triangle.
Welding typically halves the file. Positions are snapped only to build the hash
key; the stored coordinate is always the original, so welding never moves
geometry. Normals participate in the key, so hard edges keep their creases.

**Metadata** — glTF `extras` can't carry properties for ten thousand merged
elements without bloating the model, so the sidecar keeps identity data beside
it instead:

```json
{"guid":"…","name":"Basic Wall:Generic - 200mm","set":"ARCH","properties":{…}}
```

## 4D sequencing (Nav4DEx)

The **4D** tab turns a model and a schedule into a linked 4D sequence, and gives
every model, set and export file a name that sorts into build order.

### The naming scheme

```
SSSSS_LEVEL_DISC_ACTV_description
02868_L05_ARCS_FRMG_Interior-framing
│     │     │    └── activity: what work this is
│     │     └────── discipline: STRC, ARCS, MECH, PLBG, ELEC, FIRE…
│     └──────────── level: L01…L99, L-01 for below grade, SITE, ROOF
└────────────────── sequence: when it happens
```

Those five leading digits are the whole trick. They are
`(level + trade lag + bias)` for the first three and the activity's position in
the floor cycle for the last two — so sorting the strings ASCII-ascending sorts
the work into the order it gets built.

**Why a lag.** On a high-rise the structure races ahead and every following trade
trails it by a roughly constant number of floors. So the moment a piece of work
happens is not its floor, it is the floor *the structure has reached* by then:

```
time index = level + trade lag
```

Structure on L08, framing on L05 (lag 3) and curtain wall on L03 (lag 5) all
land on time index 8 — which is exactly the stagger you see on site. Fifteen
storeys of structure, MEP, framing, curtain wall and drywall sort like this:

```
02140_L01_STRC_DECK      structure alone for the first three floors
02240_L02_STRC_DECK
02340_L03_STRC_DECK
02440_L04_STRC_DECK
02460_L01_MECH_MEPH      MEP and framing join, 3 floors behind
02468_L01_ARCS_FRMG
02540_L05_STRC_DECK
02560_L02_MECH_MEPH
02568_L02_ARCS_FRMG
02640_L06_STRC_DECK
02660_L03_MECH_MEPH
02668_L03_ARCS_FRMG
02672_L01_ARCS_CWAL      curtain wall joins, 5 floors behind
…
02840_L08_STRC_DECK      by the time structure tops L08…
02860_L05_MECH_MEPH      …MEP and framing are on L05…
02868_L05_ARCS_FRMG
02872_L03_ARCS_CWAL      …and the skin is on L03
02880_L02_ARCS_DRYW
```

Cycle order rises with lag, so two activities sharing a time index list top-down
the way you would see them standing on site — structure above the framing below
it above the skin below that. The **Sequence** sub-tab makes every lag and order
editable, because they vary by project; the defaults are a starting point, not a
rule.

### Naming your models and sets

The **Naming** sub-tab classifies loaded models and search sets by discipline,
level and work scope, using the token-dictionary approach from AutoNAV widened to
cover all three. `L01_ARCS`, `Level 1 - Architectural`, and
`ARCH-L01-Framing.nwc` all resolve identically, and longest-alias-wins means
"Curtain Wall" is glazing rather than a generic wall.

The dictionary covers the plain model categories, not only the trade vocabulary:
`Walls`, `Floors`, `Ceilings`, `Roofs`, `Railings`, `Stairs`, `Ducts`, `Pipes`,
`Structural Framing`, `Generic Models`, `Miscellaneous` and the rest all classify
straight out of a Revit or Navisworks export. Specificity still decides ties, so
a structural wall sequences with the structure and a curtain wall with the skin,
while a bare "Walls" set lands with the partitions.

"Floor" is both a level word and a category name, so the level is read first and
the phrase that produced it is taken out of the name before anything else looks
at it. `3rd Floor Plumbing` is level 3 plumbing; `Floors` is a slab.

Batch renaming is preview-first: you get a proposal per set with the reason it
was classified that way, and nothing is renamed until you press Apply. Sets the
classifier could not fully resolve are shown but left unticked — a guessed
sequence number sorts the folder wrong, which is worse than no number at all.

### Teaching it your own names

No shipped dictionary covers a particular office's vocabulary. The
**Identifiers** sub-tab is where you add it, once, for every model you open.

A **rule** maps a pattern onto a discipline, an activity, a level, or any
combination, and outranks the built-in dictionary. It can match a whole token, any
part of the name, or a regular expression:

| Pattern | Match | → | Meaning |
|---|---|---|---|
| `Misc` | token | `MISC` `MISC` | your catch-all sets |
| `Generic Models` | contains | `MISC` `MISC` | Revit's own category name |
| `^WD-?\d+` | regex | `ARCS` `DRYW` | a work-package prefix |
| `P-DECK` | token | ` ` `ROOF` | a level your project names its own way |

Rules compose: one can name the discipline and another the activity, so narrow
rules stack instead of each having to restate everything. Longer patterns win by
default; an explicit priority overrides that. Any rule can be unticked to keep it
without applying it.

Scopes the built-in table has no code for get their own **activity** — four
letters, a discipline, the lag and cycle order that place it in the build, and the
names it answers to. A custom activity sequences exactly like a built-in one and
appears in the **Sequence** table; giving it the code of a built-in replaces that
built-in rather than competing with it.

Type a real set name into **Try a name** to see the code it would get and which
rule or alias produced it. The library lives in
`%AppData%\NavEx\4D\identifiers.txt` — one file per machine, not per document —
and Import/Export merge it with a colleague's.

### Regrouping search sets

The **Grouping** sub-tab files sets into folders by discipline, level, activity,
or level-then-discipline, using the same identity as the renamer — so anything
your identifiers taught it applies here too. Sets with nothing to file them by go
to `ZZ Unsorted` rather than to a folder named after a guess. Like the rename, it
is preview-first and moves nothing until you press Apply; the sets keep their
names, so clash tests and viewpoints that reference them are unaffected.

### Getting a schedule in

**Read TimeLiner** loads the schedule the model already has. That is the common
case — somebody linked P6 to TimeLiner months ago, or built the tasks by hand —
and it needs no file, no export and no column mapping. Tasks come back with their
dates, task types, synchronisation IDs and WBS path, and any set TimeLiner
already has attached is carried in as a match you made rather than one to
re-derive. Pressing **Match to model** with nothing loaded reads TimeLiner too.

Or import a file: P6 XER, P6 XML, MS Project XML and any delimited text. Column detection
runs on header aliases first, then on value shape for anything left over, which
is what rescues P6's internal names (`target_start_date`) and headerless exports.

Only **Task Name**, **Planned Start** and **Planned Finish** are required.
Description, Task ID, Actual Start/Finish, Duration and Task Type are all used
when present and skipped when not. Anything unresolved goes to a mapping dialog
showing real sample values from your file — never guessed silently, because a
wrongly guessed date column corrupts the whole sequence invisibly.

### Matching tasks to geometry

Every task and every set reduces to the same level/discipline/activity identity,
and agreement there counts for far more than word overlap — "Level 5 structural
deck" and `02840_L05_STRC_DECK` share almost no literal text but describe the
same work. Word overlap breaks ties.

A level conflict is a veto, not a penalty: "Level 12 drywall" will never attach
to the level 2 set, because an unmatched task is visible in the review grid and a
wrongly matched one is not. Near-ties are flagged as ambiguous for the same
reason. Procurement and administrative activities with no geometry stay
unmatched, which is the correct answer.

Manual corrections are remembered per document and keyed on the task ID, so
re-importing an updated schedule refreshes dates and keeps every human decision.

### Editing tasks, and writing them back

Select a task in the Gantt and its name, planned dates and task type are editable
in the panel beside it. Edits land in NavEx's copy and become real on the next
write, the same preview-then-commit shape the renamer uses.

**Write to TimeLiner** has two modes:

- **Update** — tasks that already exist are updated in place, the rest are added.
  Existing tasks are found by the object they were read from first, then by
  synchronisation ID, then by name, and at any depth in the task tree. The handle
  is what makes a rename land on the task you renamed instead of forking it.
- **Replace** — every existing task is deleted first, then this list is written.
  It confirms before doing anything, and says plainly that the result is a flat
  list and that anything added outside NavEx is lost.

The TimeLiner managed API lives in `Autodesk.Navisworks.Timeliner.dll`, which
ships inside Navisworks but is not redistributable and is not on NuGet.
Referencing it directly would mean NavEx could only be compiled on a machine with
Navisworks installed, and would pin one build to one release. So the bridge binds
late, looks up every member defensively, and reports rather than throws — reading
included. If the bridge cannot bind at all, NavEx writes a CSV that TimeLiner's
own data-source import reads.

## Format notes

| Format | Carries |
|---|---|
| **GLB** | Geometry, normals, vertex colours, PBR materials, node metadata, provenance. One file. |
| **glTF + .bin** | Same, split into a JSON file and a binary buffer. Useful when the JSON needs inspecting or patching. |
| **glTF embedded** | Same as one JSON file with a base64 buffer — roughly 33% larger, but text-only. |
| **OBJ + MTL** | Geometry, normals, colour and opacity. No PBR, no vertex colours, no metadata, no units. |

## For developers

```
NavEx/
  NavEx.csproj                local build against an installed Navisworks
  NavEx.CI.csproj             SDK-style build via the Speckle.Navisworks.API NuGet package
  PluginMain.cs               AddInPlugin entry point
  MainWindow.xaml[.cs]        the UI
  NavEx.addin                 Navisworks plugin manifest
  Core/
    GeometryExtractor.cs      COM traversal: selection -> paths -> fragments -> primitives
    PrimitiveCallback.cs      InwSimplePrimitivesCB; the local->world->target transform chain
    MeshBuilder.cs            vertex welding, primitive buckets, node/scene model
    MaterialDef.cs            appearance resolution and material de-duplication
    SelectionSetTree.cs       the export tree, and resolving sets to model items
    ExportRunner.cs           orchestration, file naming, estimates
    PropertyExporter.cs       .properties.json sidecar
    SettingsStore.cs          settings and presets
    Exporters/
      GltfWriter.cs           glTF 2.0 / GLB
      ObjWriter.cs            OBJ + MTL
      Json.cs                 minimal JSON DOM (no external dependency)
```

Build one Navisworks year without having Navisworks installed:

```powershell
msbuild NavEx\NavEx.CI.csproj -t:restore,build `
    -p:Configuration=Release -p:Platform=x64 `
    -p:NWYear=2027 -p:NWPackageVersion=2027.0.0
```

Supported `NWYear` / `NWPackageVersion` pairs: `2024 / 2024.0.0`,
`2025 / 2025.0.0`, `2026 / 2026.0.1`, `2027 / 2027.0.0`.

Use MSBuild rather than `dotnet build`: WPF XAML for `net48` is only compiled by
full MSBuild. `.github/workflows/build.yml` builds all four years and assembles
the plugin zip.

### Verifying without Windows

Two harnesses under `tools/` check the code on any platform, which is what CI
runs on Linux:

```bash
# Type-check every source file, code-behind included, against the real
# Navisworks API. gen_xaml_stub.py stands in for the WPF XAML compiler.
python3 tools/typecheck/gen_xaml_stub.py
dotnet build tools/typecheck/typecheck.csproj

# Build a known cube through GltfWriter / ObjWriter and validate the output.
mkdir -p artifacts
dotnet build tools/writer-tests/WriterTests.csproj -o artifacts/bin
(cd artifacts && dotnet exec bin/WriterTests.dll)
python3 tools/writer-tests/validate_gltf.py artifacts
```

The validator parses the GLB container by hand — header length, chunk padding,
bufferView and accessor alignment, declared POSITION min/max against the actual
data — then reconstructs the cube from the buffers and asserts it is closed,
unit-volume and wound counter-clockwise. That last part is what catches a
transform or winding regression; well-formed JSON on its own proves nothing.

The type check also catches a Windows-only class of bug: a control referenced in
`MainWindow.xaml.cs` whose `x:Name` no longer exists in the XAML.

Pass `-p:NWPackageVersion=` to check one year:

```bash
dotnet build tools/typecheck/typecheck.csproj -p:NWPackageVersion=2025.0.0
```

CI runs that across all four supported years, so a member that disappears or
changes signature in any single release fails that year's leg and leaves the
others green.

### Why one build works across 2024-2027

NavEx compiles unchanged against every supported year, with no `#if NW20xx`
branches, because it stays on the part of the API that has not moved. Comparing
the 2024, 2025, 2026 and 2027 reference assemblies:

* The COM geometry path NavEx depends on — `ComApiBridge`, `InwOpSelection`,
  `InwOaPath`, `InwOaFragment3`, `InwLTransform3f3`, `InwSimpleVertex`,
  `InwSimplePrimitivesCB`, `InwOaMaterial` — is **identical** in all four, down
  to the `nwEVertexProperty` flag values (`eNORMAL=1`, `eCOLOR=2`,
  `eTEX_COORD=4`).
* The managed API did change across those releases, but only in members NavEx
  never calls: `Document.PublishFile` gained an `NwdExportOptions` parameter and
  `ExportToNwd` appeared, `CreateCommentWithUniqueId` took an `Assignee` instead
  of a string, `SavedItem` gained `CanTransform` / `Transform`, and
  `GroupItem.RemoveAllChildrenAndTakeOwn` was dropped after 2025.

The per-year DLLs exist because each links against that year's assemblies, not
because the source differs.

### How geometry actually comes out of Navisworks

The managed API exposes geometry only as bounding boxes and primitive counts —
there is no mesh accessor. Vertices come from the COM API:

```
ModelItemCollection
  -> ComApiBridge.ToInwOpSelection
  -> InwOpSelection.Paths()
  -> InwOaPath.Fragments()
  -> InwOaFragment3.GenerateSimplePrimitives(eNORMAL, callback)
  -> InwSimplePrimitivesCB.Triangle(v1, v2, v3)
```

Two traps worth knowing about, both handled in `GeometryExtractor`:

* `path.Fragments()` returns the fragments of the path's *node*, and an
  instanced node is shared by several paths. Without comparing
  `fragment.path.ArrayData` against `path.ArrayData`, every instance exports
  every other instance's geometry, at the wrong locations.
* The COM RCWs returned by `Paths()` and `Fragments()` can be collected while
  still being enumerated, which shows up as a process crash rather than an
  exception. `GC.KeepAlive` pins them for the duration of the loop.

Vertices arrive in fragment-local space; `InwOaFragment3.GetLocalToWorldMatrix()`
returns a 16-element column-major matrix, and normals are transformed by its
inverse-transpose. A mirroring transform (negative determinant) reverses winding
in world space, so those triangles are re-ordered to keep glTF's
counter-clockwise front faces.

Everything stays in double precision until the final write into float32 buffers,
by which point recentring has brought the coordinates near the origin.

## Licence

Internal use only. © Keith Acker.
