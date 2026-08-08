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
