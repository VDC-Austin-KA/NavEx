# Datasmith SDK integration

How NavEx produces importable `.udatasmith` files. Built and verified against
UE 5.8.2; the SDK documentation is at
`Engine/Source/Programs/Enterprise/Datasmith/DatasmithSDK/Documentation` in the UE
GitHub repo.

Read this before touching `DatasmithWriter`. It replaced a hand-written XML + FBX
payload that nothing could import.


## What the SDK actually is

One monolithic library, not a source drop you compile into your app:

    Engine\Binaries\Win64\DatasmithSDK\DatasmithSDK.dll   (~49 MB)
    ...\DatasmithSDK.lib
    ...\Public\     41 SDK headers
    ...\Private\    Core / CoreUObject / TraceLog headers

Building the target populates all of that, so nothing else needs the engine
source tree afterwards.

A launcher install cannot substitute, even one that has had engine source added
to it: the `DatasmithSDK` program target is not shipped in it at all, and
`Engine\Build\InstalledBuild.txt` blocks the monolithic link the target
requires. A full source clone is the only route.


## Prerequisite (one time)

    git clone --branch 5.8 --depth 1 https://github.com/EpicGames/UnrealEngine.git D:\UE58src
    cd D:\UE58src
    Setup.bat
    GenerateProjectFiles.bat

then build the target (Build.bat is the supported entry point for a Program
target; going through `UE5.sln` drags in the editor):

    Engine\Build\BatchFiles\Build.bat DatasmithSDK Win64 Development

Setup.bat pulls ~31 GB and the SDK build takes ~12 minutes.

Build against the branch matching the editor that will import the files. Epic:
"For best results, use the same version of the Unreal Editor that you used to build
your export application." Earth4D runs 5.8.2, so branch `5.8`.


## Consuming it from a plain C++ DLL

The bridge is an ordinary MSVC DLL project, the same shape as Epic's
`DatasmithSDKSample` — **not** a UBT module. It builds with plain MSBuild. Note the
C++ toolset must be present: VS Build Tools 2026 on this machine ships without it,
so the bridge builds with the 2022 instance.

**Ignore Epic's "Setting Up your Project" page for the compiler settings.** It dates
from UE4 and is materially wrong for UE5: its include paths point into the engine
source tree (the built SDK is self-contained under
`Engine\Binaries\Win64\DatasmithSDK`), its define list is a fraction of what UBT
passes, it omits the entire `*_NON_ATTRIBUTED_API` family, and its
`_WIN32_WINNT=0x0601` disagrees with the `0x0A00` the SDK is built with. Following it
produces a wall of errors inside UE's math headers.

The settings that work come from UBT's own response file, left behind after building
the SDK:

    Engine\Intermediate\Build\Win64\x64\DatasmithSDK\Development\
        Core\Core.Shared.rsp              compiler switches
        DatasmithExporter\Definitions.h   the ~160 defines, force-included

`native/NavExDatasmith/DatasmithSDK.props` mirrors those switches, and
`UEDefinitions.h` is that generated header with every `*_API` flipped from
`DLLEXPORT` to `DLLIMPORT` (regenerate with `gen_defs.py`). Unreal's headers are
configuration-dependent — struct layout, alignment and inlining all change with
these macros — so a mismatch is runtime corruption, not a link error.

Three that are easy to get wrong:

- **`/MD`, not `/MT`.** The SDK is built against the dynamic CRT in every
  configuration. Epic's own sample `.vcxproj` says `MultiThreaded`; it is stale.
  Mixing CRTs across this boundary crashes at runtime rather than failing to link.
- **`/Zc:preprocessor`**, plus `/permissive-`, `/std:c++20`, `/bigobj`, `/Zp8`,
  `/fp:precise`.
- **`#include <strsafe.h>` before any UE header.** Epic's sample does this in its
  pch. Afterwards it is a wall of C4141 `'inline' used more than once`.

Link `DatasmithSDK.lib`, and ship `DatasmithSDK.dll` (plus `tbb12.dll` and
`tbbmalloc.dll`) beside `NavEx.dll` in the plugin folder. None of those are in the
repo: `DatasmithSDK.dll` alone is ~49 MB and ships under Epic's EULA.


## The export sequence

Per set, mirroring what NavEx writes today:

```cpp
FDatasmithExporterManager::Initialize();          // once per process

FDatasmithSceneExporter Exporter;
Exporter.SetName(TEXT("02120_L01_STRC_FOUN"));    // the selection set
Exporter.SetOutputPath(OutputFolder);
Exporter.PreExport();

TSharedRef<IDatasmithScene> Scene =
    FDatasmithSceneFactory::CreateScene(TEXT("02120_L01_STRC_FOUN"));

// ---- geometry: this is the call that writes the .udsmesh ----
FDatasmithMesh Mesh;
Mesh.SetVerticesCount(n);  Mesh.SetVertex(i, x, y, z);
Mesh.SetFacesCount(f);     Mesh.SetFace(i, v0, v1, v2, materialId);
Mesh.SetNormal(i, x, y, z);                       // per FACE CORNER: face*3 + corner
Mesh.SetUVChannelsCount(1); Mesh.SetUVCount(0, n); Mesh.SetUV(0, i, u, v);
Mesh.SetFaceUV(i, 0, v0, v1, v2);

FDatasmithMeshExporter MeshExporter;
TSharedPtr<IDatasmithMeshElement> MeshElement = MeshExporter.ExportToUObject(
    Exporter.GetAssetsOutputPath(),               // must be this, not a path of our own
    TEXT("02120_L01_STRC_FOUN"),
    Mesh, nullptr, EDSExportLightmapUV::Always);
Scene->AddMesh(MeshElement);

TSharedPtr<IDatasmithMeshActorElement> Actor =
    FDatasmithSceneFactory::CreateMeshActor(TEXT("02120_L01_STRC_FOUN"));
Actor->SetStaticMeshPathName(TEXT("02120_L01_STRC_FOUN"));
Scene->AddActor(Actor);

// ---- pixmy.* linkage, unchanged in spelling ----
// FDatasmithSceneFactory::CreateMetaData + CreateKeyValueProperty,
// SetAssociatedElement(Actor), Scene->AddMetaData(...)

Exporter.Export(Scene);
FDatasmithExporterManager::Shutdown();            // once, at process exit
```

`SetNormal` is indexed per face corner, not per vertex. The header comment says only
"value to choose the normal"; Epic's own `MeshUtils.h` settles it by filling 36
normals for a 12-triangle box. This is the easy way to ship a mesh whose shading is
quietly wrong.

## Two things about running in-process

The SDK is designed for a standalone exporter, and a Navisworks add-in violates two
of its assumptions:

- **Shutdown must run on the thread that initialised.** Unreal pins its main thread
  at static init and asserts on it while tearing down the linker and object systems.
  A host gives no say in which thread unwinds it — for NavEx it is the CLR's
  `ProcessExit` thread — and `FTaskTagScope(ETaskTag::EGameThread)` does not help,
  because the tag scope asserts on the same condition. The bridge records the
  initialising thread and skips shutdown when it cannot do it correctly, leaving the
  SDK to the process teardown already in progress.
- **`bSaveLogToUserDir` must be true.** Left false, the SDK writes its `Engine/` log
  and crash folders relative to the working directory, which for an in-process
  plugin is wherever the host was launched from — for Navisworks, its own install
  under Program Files.


## What carries over from the current writer

- One export per Navisworks selection set. That is the schedule's granularity.
- Every `pixmy.*` key, spelled identically. `pixmy.set.name` is the join key
  Earth4D links tasks on.
- `navex_schedule.json` beside the output, unchanged — the SDK has nothing to do
  with it.

## What goes away

- The hand-written XML in `DatasmithWriter`. Beyond the FBX payload, a real Epic
  export shows the schema differs in several ways we got wrong: `<Version>0.24</Version>`
  (format version, not app version), lowercase `<file path>` URL-encoded with no
  `object` attribute, `label` as an attribute on `<StaticMesh>`, plus required
  `<Size>`, `<Hash>`, `<LightmapCoordinateIndex>`, `<LightmapUV>` and `<Material>`.
- `FbxWriter` as the Datasmith payload. It stays as its own export format.

## Caveat Epic states plainly

The SDK API is not final and `.udatasmith` files are not guaranteed
backward-compatible across engine versions. Pin the SDK build to the engine version
Earth4D runs on, and rebuild both together.
