NavEx — Navisworks geometry exporter
====================================

Export search sets, selection sets and the current selection out of Navisworks
as lightweight glTF 2.0 (.glb / .gltf) or Wavefront OBJ, with materials
embedded and geometry accurate to the source tessellation.


INSTALL
-------

1. Close Navisworks.
2. Right-click Install.cmd  ->  "Run as administrator"  ->  accept the prompt.
3. Start Navisworks. NavEx appears on the Add-Ins ribbon tab.

The installer copies two files per Navisworks version into

    C:\Program Files\Autodesk\Navisworks Manage <year>\Plugins\NavEx\

That is all it does — mkdir and copy, nothing else.


MANUAL INSTALL
--------------

For each Navisworks Manage year you have:

1. Create   C:\Program Files\Autodesk\Navisworks Manage <year>\Plugins\NavEx\
2. Copy NavEx.dll and NavEx.addin from the matching folder in this download:

       V24  ->  Navisworks Manage 2024
       V25  ->  Navisworks Manage 2025
       V26  ->  Navisworks Manage 2026
       V27  ->  Navisworks Manage 2027


UNINSTALL
---------

Run Uninstall.cmd as administrator, or delete the Plugins\NavEx folder from
each Navisworks install by hand.


QUICK START
-----------

1. Add-Ins tab  ->  NavEx
2. Tick the search sets you want (or "Current Selection" at the top).
3. Choose an output folder.
4. Press Export.

Defaults produce one .glb per set, in metres, Y-up, recentred on the selection,
with vertices welded and materials merged — the smallest file that still opens
correctly in Blender, Unreal, Unity, three.js and Windows 3D Viewer.


NOTES
-----

* Recentring: survey coordinates lose accuracy when stored as 32-bit floats.
  NavEx subtracts an offset so the geometry sits near the origin, and records
  that offset in the file (asset.extras in glTF, a header comment in OBJ) so
  the model can be georeferenced back.

* Metadata: tick "Write a .properties.json sidecar" to get every item's
  Navisworks properties next to the model, joinable on item GUID.

* Very large sets: press Estimate first. It reports triangle counts and a
  rough file size before you commit to the export.
