# Datasmith SDK integration

How NavEx will produce importable `.udatasmith` files, taken from Epic's own SDK
documentation at `Engine/Source/Programs/Enterprise/Datasmith/DatasmithSDK/Documentation`
in the UE GitHub repo (branch `5.8`).

Read this before touching `DatasmithWriter`. The current hand-written XML + FBX
payload cannot be imported by anything and is being replaced wholesale.


## What the SDK actually is

One monolithic library, not a source drop you compile into your app:

    Engine\Binaries\Win64\DatasmithSDK\DatasmithSDK.dll
    Engine\Binaries\Win64\DatasmithSDK\DatasmithSDK.lib

Headers come from any UE install, launcher build included. Only the `.dll`/`.lib`
have to be built, and only the one `DatasmithSDK` program target — not the editor.
That is why `D:\EPIC\UE_5.8` has every Datasmith header but no `.cpp` and no
`Datasmith*.lib`: a launcher install ships the interface, never the implementation.


## Prerequisite (one time)

    git clone --branch 5.8 --depth 1 https://github.com/EpicGames/UnrealEngine.git D:\UE58src
    cd D:\UE58src
    Setup.bat
    GenerateProjectFiles.bat

then build the `Programs/Enterprise/DatasmithSDK` project from `UE5.sln`
(Development | Win64).

Build against the branch matching the editor that will import the files. Epic:
"For best results, use the same version of the Unreal Editor that you used to build
your export application." Earth4D runs 5.8.2, so branch `5.8`.


## Consuming it from a plain C++ DLL

The bridge is an ordinary MSVC DLL project, the same shape as Epic's
`DatasmithSDKSample` — **not** a UBT module. It builds with the MSBuild already on
this machine.

Additional include directories:

    <UE>\Engine\Source\Programs\Enterprise\Datasmith\DatasmithSDK\Public
    <UE>\Engine\Source\Runtime\Core\Public
    <UE>\Engine\Source\Runtime\CoreUObject\Public
    <UE>\Engine\Source\Runtime\Projects\Public
    <UE>\Engine\Source\Runtime\TraceLog\Public

Preprocessor definitions (all of them; the `*_API=DLLIMPORT` ones are what make the
monolithic DLL's symbols resolve):

    UE_BUILD_DEVELOPMENT=1  UE_BUILD_MINIMAL=1
    WITH_EDITOR=0  WITH_EDITORONLY_DATA=1  WITH_SERVER_CODE=1  WITH_ENGINE=0
    WITH_UNREAL_DEVELOPER_TOOLS=0  WITH_PLUGIN_SUPPORT=0
    IS_MONOLITHIC=1  IS_PROGRAM=1
    PLATFORM_WINDOWS=1  WIN32=1  _WIN32_WINNT=0x0601  WINVER=0x0601
    UNICODE  _UNICODE
    CORE_API=DLLIMPORT  COREUOBJECT_API=DLLIMPORT
    DATASMITHEXPORTER_API=DLLIMPORT  DATASMITHCORE_API=DLLIMPORT
    DIRECTLINK_API=DLLIMPORT
    UE_BUILD_DEVELOPMENT_WITH_DEBUGGAME=0  UBT_COMPILED_PLATFORM=Windows

Link `DatasmithSDK.lib` from `Engine\Binaries\Win64\DatasmithSDK`, and ship
`DatasmithSDK.dll` beside `NavEx.dll` in the Navisworks plugin folder.

C++20 is required.


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
"value to choose the normal", so this is the easy way to ship a mesh whose shading
is quietly wrong.


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
