// NavEx <-> Datasmith Export SDK bridge.
//
// A flat C API over the SDK so NavEx (C#, .NET Framework 4.8, in-process in
// Navisworks) can produce real `.udatasmith` scenes with `.udsmesh` payloads.
// Everything the SDK exposes is C++ with UE types in the signatures, none of
// which survive P/Invoke, so this is the thinnest possible waist: opaque scene
// handle, flat arrays, wide strings, int return codes.
//
// Threading: the SDK initialises engine core systems on first use and is not
// re-entrant. NavEx runs the whole export on the Navisworks main thread, which
// is what the SDK wants anyway, so no locking here.
//
// Every entry point returns 0 on success and non-zero on failure; call
// NavExDs_LastError for the reason.

#pragma once

#ifdef NAVEXDS_EXPORTS
#define NAVEXDS_API __declspec(dllexport)
#else
#define NAVEXDS_API __declspec(dllimport)
#endif

extern "C"
{

/**
 * Boots the Datasmith exporter (engine core systems, logging, module loading).
 * Idempotent: safe to call before every export, does the work once.
 *
 * The SDK's own contract is that Initialize runs once per process and Shutdown
 * runs once at exit; it does NOT support an Initialize/Shutdown cycle. Since a
 * Navisworks session loads NavEx once and may export many times, the guard here
 * is what keeps that contract.
 */
NAVEXDS_API int NavExDs_Initialize();

/**
 * Releases the engine systems. Call once, when the plugin unloads. Exporting
 * after this in the same process is not supported by the SDK.
 */
NAVEXDS_API void NavExDs_Shutdown();

/** Reason the last failing call failed. Valid until the next call on this thread. */
NAVEXDS_API const wchar_t* NavExDs_LastError();

/**
 * Starts one Datasmith scene. `sceneName` becomes both the `.udatasmith` file
 * name and the `_Assets` folder name, so pass the Navisworks selection set --
 * that is the granularity the 4D schedule links on.
 *
 * Returns null on failure.
 */
NAVEXDS_API void* NavExDs_BeginScene(
	const wchar_t* sceneName,
	const wchar_t* outputDir,
	const wchar_t* host,
	const wchar_t* vendor,
	const wchar_t* productName,
	const wchar_t* productVersion);

/**
 * Adds one mesh and writes its `.udsmesh` payload immediately, into the scene's
 * assets folder. This is the call the whole bridge exists for: it is what NavEx
 * was faking by writing an FBX and pointing <StaticMesh><File path> at it.
 *
 * Geometry is taken in NavEx's own layout -- welded vertices with one normal and
 * one UV each -- and expanded here to the per-face-corner normals Datasmith
 * wants. Epic's own sample fills 36 normals for a 12-triangle box, i.e. index
 * `face * 3 + corner`, NOT one per vertex.
 *
 * positions      3 floats per vertex, vertexCount vertices
 * indices        3 ints per triangle, triangleCount triangles
 * vertexNormals  3 floats per vertex, or null to let Datasmith compute them
 * vertexUVs      2 floats per vertex, or null for no UV channel
 * faceMaterialIds 1 int per triangle, or null for all-zero
 */
NAVEXDS_API int NavExDs_AddMesh(
	void* scene,
	const wchar_t* meshName,
	const float* positions, int vertexCount,
	const int* indices, int triangleCount,
	const float* vertexNormals,
	const float* vertexUVs,
	const int* faceMaterialIds);

/**
 * Places a mesh in the scene. `meshName` must match a NavExDs_AddMesh call.
 * `label` is the display name Unreal shows; `layer` may be null.
 */
NAVEXDS_API int NavExDs_AddMeshActor(
	void* scene,
	const wchar_t* actorName,
	const wchar_t* meshName,
	const wchar_t* label,
	const wchar_t* layer);

/**
 * Attaches key/value metadata to an actor added earlier. This carries the
 * `pixmy.*` schedule linkage, spelled exactly as the hand-written XML spelled
 * it -- `pixmy.set.name` is what Earth4D joins tasks on.
 */
NAVEXDS_API int NavExDs_AddMetaData(
	void* scene,
	const wchar_t* actorName,
	const wchar_t* const* keys,
	const wchar_t* const* values,
	int count);

/** Writes the `.udatasmith` referencing the payloads already on disk. */
NAVEXDS_API int NavExDs_Export(void* scene);

/** Frees the scene. Safe on null. Does not write anything. */
NAVEXDS_API void NavExDs_DestroyScene(void* scene);

} // extern "C"
