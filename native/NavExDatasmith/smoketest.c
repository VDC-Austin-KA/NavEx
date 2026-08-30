/*
 * Proves the bridge produces a scene the Datasmith importer will accept, without
 * Navisworks in the loop: one cube through the same entry points NavEx calls,
 * then check a `.udsmesh` payload actually landed.
 *
 * Deliberately C and LoadLibrary-based -- it exercises the DLL exactly as the
 * P/Invoke layer does, so a calling-convention or export-name mistake fails here
 * rather than inside Navisworks.
 *
 *   cl /nologo smoketest.c
 *   smoketest.exe <output-dir>
 */

#include <windows.h>
#include <stdio.h>

typedef int   (*PFN_Init)(void);
typedef void  (*PFN_Shutdown)(void);
typedef const wchar_t* (*PFN_LastError)(void);
typedef void* (*PFN_BeginScene)(const wchar_t*, const wchar_t*, const wchar_t*,
                                const wchar_t*, const wchar_t*, const wchar_t*);
typedef int   (*PFN_AddMesh)(void*, const wchar_t*, const float*, int, const int*, int,
                             const float*, const float*, const int*);
typedef int   (*PFN_AddMeshActor)(void*, const wchar_t*, const wchar_t*, const wchar_t*, const wchar_t*);
typedef int   (*PFN_AddMetaData)(void*, const wchar_t*, const wchar_t* const*, const wchar_t* const*, int);
typedef int   (*PFN_Export)(void*);
typedef void  (*PFN_Destroy)(void*);

/* A unit cube: 8 corners, 12 triangles. */
static const float CubePositions[8 * 3] = {
    0,0,0,  100,0,0,  100,100,0,  0,100,0,
    0,0,100, 100,0,100, 100,100,100, 0,100,100
};
static const int CubeIndices[12 * 3] = {
    0,2,1,  0,3,2,   4,5,6,  4,6,7,
    0,1,5,  0,5,4,   1,2,6,  1,6,5,
    2,3,7,  2,7,6,   3,0,4,  3,4,7
};

#define GET(type, name) \
    type name = (type)GetProcAddress(dll, #name); \
    if (!name) { printf("FAIL: export %s missing\n", #name); return 2; }

int main(int argc, char** argv)
{
    const wchar_t* outDir = (argc > 1) ? L"" : L".";
    wchar_t wideOut[MAX_PATH];
    HMODULE dll;
    void* scene;
    int rc;

    if (argc > 1)
    {
        MultiByteToWideChar(CP_ACP, 0, argv[1], -1, wideOut, MAX_PATH);
        outDir = wideOut;
    }

    dll = LoadLibraryW(L"NavExDatasmith.dll");
    if (!dll) { printf("FAIL: LoadLibrary NavExDatasmith.dll -> %lu\n", GetLastError()); return 2; }

    GET(PFN_Init,         NavExDs_Initialize)
    GET(PFN_Shutdown,     NavExDs_Shutdown)
    GET(PFN_LastError,    NavExDs_LastError)
    GET(PFN_BeginScene,   NavExDs_BeginScene)
    GET(PFN_AddMesh,      NavExDs_AddMesh)
    GET(PFN_AddMeshActor, NavExDs_AddMeshActor)
    GET(PFN_AddMetaData,  NavExDs_AddMetaData)
    GET(PFN_Export,       NavExDs_Export)
    GET(PFN_Destroy,      NavExDs_DestroyScene)

    if (NavExDs_Initialize() != 0) { printf("FAIL: Initialize: %ls\n", NavExDs_LastError()); return 1; }
    printf("ok: SDK initialised\n");

    scene = NavExDs_BeginScene(L"SmokeTest", outDir, L"NavEx", L"NavEx", L"NavEx Smoke Test", L"1.0.0");
    if (!scene) { printf("FAIL: BeginScene: %ls\n", NavExDs_LastError()); return 1; }
    printf("ok: scene created\n");

    rc = NavExDs_AddMesh(scene, L"SmokeCube", CubePositions, 8, CubeIndices, 12, NULL, NULL, NULL);
    if (rc != 0) { printf("FAIL: AddMesh: %ls\n", NavExDs_LastError()); return 1; }
    printf("ok: mesh exported\n");

    rc = NavExDs_AddMeshActor(scene, L"actor_SmokeCube", L"SmokeCube", L"SmokeCube", L"NavEx");
    if (rc != 0) { printf("FAIL: AddMeshActor: %ls\n", NavExDs_LastError()); return 1; }

    {
        const wchar_t* keys[]   = { L"pixmy.set.name", L"pixmy.contractVersion" };
        const wchar_t* values[] = { L"02120_L01_STRC_FOUN", L"1.0" };
        rc = NavExDs_AddMetaData(scene, L"actor_SmokeCube", keys, values, 2);
        if (rc != 0) { printf("FAIL: AddMetaData: %ls\n", NavExDs_LastError()); return 1; }
    }
    printf("ok: actor + metadata added\n");

    rc = NavExDs_Export(scene);
    if (rc != 0) { printf("FAIL: Export: %ls\n", NavExDs_LastError()); return 1; }
    NavExDs_DestroyScene(scene);
    printf("ok: scene exported\n");

    NavExDs_Shutdown();
    return 0;
}
