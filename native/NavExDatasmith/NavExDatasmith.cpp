// NavEx <-> Datasmith Export SDK bridge. See NavExDatasmith.h for the contract.

#include "NavExDatasmith.h"

#include "DatasmithExporterManager.h"
#include "DatasmithSceneExporter.h"
#include "DatasmithSceneFactory.h"
#include "DatasmithExportOptions.h"
#include "DatasmithMesh.h"
#include "DatasmithMeshExporter.h"
#include "IDatasmithSceneElements.h"

#include "Containers/Map.h"
#include "Containers/UnrealString.h"
#include "Templates/SharedPointer.h"

namespace
{
	/** Reason the last failing call failed. One per thread; NavEx exports on one. */
	thread_local FString GLastError;

	bool GInitialized = false;

	void SetError(const FString& Message)
	{
		GLastError = Message;
	}

	/**
	 * One in-flight scene. Holds the exporter alongside the scene because
	 * FDatasmithMeshExporter needs the exporter's assets path to put the
	 * `.udsmesh` files exactly where the importer will look for them, and that
	 * path is only known once SetName/SetOutputPath have been applied.
	 */
	struct FBridgeScene
	{
		FDatasmithSceneExporter Exporter;
		TSharedPtr<IDatasmithScene> Scene;
		/** Actors by name, so metadata can be attached after the actor is added. */
		TMap<FString, TSharedPtr<IDatasmithActorElement>> ActorsByName;
	};

	FBridgeScene* AsScene(void* Handle)
	{
		if (!Handle)
		{
			SetError(TEXT("Null scene handle"));
			return nullptr;
		}
		return static_cast<FBridgeScene*>(Handle);
	}
}

int NavExDs_Initialize()
{
	if (GInitialized)
	{
		return 0;
	}

	FDatasmithExporterManager::FInitOptions Options;
	// NavEx has its own log window and this runs inside Navisworks, so keep the
	// SDK quiet and off the host's stdout. The exporter UI would need its own
	// engine directory and a second thread; we do not use it.
	Options.bSuppressLogs = true;
	Options.bSaveLogToUserDir = false;
	Options.bEnableMessaging = false;      // DirectLink is not used
	Options.bUseDatasmithExporterUI = false;

	if (!FDatasmithExporterManager::Initialize(Options))
	{
		SetError(TEXT("FDatasmithExporterManager::Initialize failed"));
		return 1;
	}

	GInitialized = true;
	return 0;
}

void NavExDs_Shutdown()
{
	if (!GInitialized)
	{
		return;
	}
	FDatasmithExporterManager::Shutdown();
	GInitialized = false;
}

const wchar_t* NavExDs_LastError()
{
	return *GLastError;
}

void* NavExDs_BeginScene(
	const wchar_t* sceneName,
	const wchar_t* outputDir,
	const wchar_t* host,
	const wchar_t* vendor,
	const wchar_t* productName,
	const wchar_t* productVersion)
{
	if (!GInitialized)
	{
		SetError(TEXT("NavExDs_Initialize has not been called"));
		return nullptr;
	}
	if (!sceneName || !*sceneName || !outputDir || !*outputDir)
	{
		SetError(TEXT("Scene name and output directory are both required"));
		return nullptr;
	}

	FBridgeScene* Bridge = new FBridgeScene();

	// Order matters: the assets path is derived from these two, and the mesh
	// exporter needs it before any payload is written.
	Bridge->Exporter.SetName(sceneName);
	Bridge->Exporter.SetOutputPath(outputDir);
	Bridge->Exporter.PreExport();

	Bridge->Scene = FDatasmithSceneFactory::CreateScene(sceneName);
	if (!Bridge->Scene.IsValid())
	{
		SetError(TEXT("FDatasmithSceneFactory::CreateScene returned null"));
		delete Bridge;
		return nullptr;
	}

	if (host)           { Bridge->Scene->SetHost(host); }
	if (vendor)         { Bridge->Scene->SetVendor(vendor); }
	if (productName)    { Bridge->Scene->SetProductName(productName); }
	if (productVersion) { Bridge->Scene->SetProductVersion(productVersion); }

	return Bridge;
}

int NavExDs_AddMesh(
	void* scene,
	const wchar_t* meshName,
	const float* positions, int vertexCount,
	const int* indices, int triangleCount,
	const float* vertexNormals,
	const float* vertexUVs,
	const int* faceMaterialIds)
{
	FBridgeScene* Bridge = AsScene(scene);
	if (!Bridge) { return 1; }

	if (!meshName || !*meshName)          { SetError(TEXT("Mesh name is required")); return 1; }
	if (!positions || vertexCount <= 0)   { SetError(TEXT("Mesh has no vertices")); return 1; }
	if (!indices || triangleCount <= 0)   { SetError(TEXT("Mesh has no triangles")); return 1; }

	FDatasmithMesh Mesh;
	Mesh.SetName(meshName);
	Mesh.SetVerticesCount(vertexCount);
	Mesh.SetFacesCount(triangleCount);

	for (int32 v = 0; v < vertexCount; ++v)
	{
		Mesh.SetVertex(v, positions[v * 3], positions[v * 3 + 1], positions[v * 3 + 2]);
	}

	for (int32 f = 0; f < triangleCount; ++f)
	{
		const int32 I0 = indices[f * 3];
		const int32 I1 = indices[f * 3 + 1];
		const int32 I2 = indices[f * 3 + 2];

		// A single out-of-range index corrupts the payload in a way that only
		// shows up as a crash inside the importer, so refuse it here.
		if (I0 < 0 || I1 < 0 || I2 < 0 ||
			I0 >= vertexCount || I1 >= vertexCount || I2 >= vertexCount)
		{
			SetError(FString::Printf(
				TEXT("Triangle %d references a vertex outside 0..%d (%d, %d, %d)"),
				f, vertexCount - 1, I0, I1, I2));
			return 1;
		}

		Mesh.SetFace(f, I0, I1, I2, faceMaterialIds ? faceMaterialIds[f] : 0);
	}

	// Normals are per FACE CORNER, not per vertex: Epic's own sample fills 36 of
	// them for a 12-triangle box. NavEx welds one normal per vertex, so fan it
	// back out here rather than making the C# side carry a duplicated array.
	if (vertexNormals)
	{
		for (int32 f = 0; f < triangleCount; ++f)
		{
			for (int32 c = 0; c < 3; ++c)
			{
				const int32 V = indices[f * 3 + c];
				Mesh.SetNormal(f * 3 + c,
					vertexNormals[V * 3], vertexNormals[V * 3 + 1], vertexNormals[V * 3 + 2]);
			}
		}
	}

	// UVs are indexed independently of positions in Datasmith. NavEx has one UV
	// per welded vertex, so the UV indices are simply the vertex indices.
	if (vertexUVs)
	{
		Mesh.AddUVChannel();
		Mesh.SetUVCount(0, vertexCount);
		for (int32 v = 0; v < vertexCount; ++v)
		{
			Mesh.SetUV(0, v, vertexUVs[v * 2], vertexUVs[v * 2 + 1]);
		}
		for (int32 f = 0; f < triangleCount; ++f)
		{
			Mesh.SetFaceUV(f, 0, indices[f * 3], indices[f * 3 + 1], indices[f * 3 + 2]);
		}
	}

	// THE call. Writes <scene>_Assets/<meshName>.udsmesh. GetAssetsOutputPath is
	// the only correct destination -- anywhere else and the importer will not
	// find the payload.
	FDatasmithMeshExporter MeshExporter;
	TSharedPtr<IDatasmithMeshElement> MeshElement = MeshExporter.ExportToUObject(
		Bridge->Exporter.GetAssetsOutputPath(),
		meshName,
		Mesh,
		nullptr,
		EDSExportLightmapUV::Always);

	if (!MeshElement.IsValid())
	{
		FString Reason = MeshExporter.GetLastError();
		SetError(Reason.IsEmpty()
			? FString::Printf(TEXT("ExportToUObject failed for mesh '%s'"), meshName)
			: Reason);
		return 1;
	}

	Bridge->Scene->AddMesh(MeshElement);
	return 0;
}

int NavExDs_AddMeshActor(
	void* scene,
	const wchar_t* actorName,
	const wchar_t* meshName,
	const wchar_t* label,
	const wchar_t* layer)
{
	FBridgeScene* Bridge = AsScene(scene);
	if (!Bridge) { return 1; }

	if (!actorName || !*actorName) { SetError(TEXT("Actor name is required")); return 1; }
	if (!meshName || !*meshName)   { SetError(TEXT("Mesh name is required")); return 1; }

	TSharedRef<IDatasmithMeshActorElement> Actor = FDatasmithSceneFactory::CreateMeshActor(actorName);
	Actor->SetStaticMeshPathName(meshName);
	if (label && *label) { Actor->SetLabel(label); }
	if (layer && *layer) { Actor->SetLayer(layer); }

	Bridge->Scene->AddActor(Actor);
	Bridge->ActorsByName.Add(FString(actorName), Actor);
	return 0;
}

int NavExDs_AddMetaData(
	void* scene,
	const wchar_t* actorName,
	const wchar_t* const* keys,
	const wchar_t* const* values,
	int count)
{
	FBridgeScene* Bridge = AsScene(scene);
	if (!Bridge) { return 1; }

	if (count <= 0) { return 0; }
	if (!keys || !values) { SetError(TEXT("Metadata keys/values are required")); return 1; }

	TSharedPtr<IDatasmithActorElement>* Actor = Bridge->ActorsByName.Find(FString(actorName));
	if (!Actor || !Actor->IsValid())
	{
		SetError(FString::Printf(TEXT("No actor named '%s' to attach metadata to"), actorName));
		return 1;
	}

	// The metadata element needs its own unique name, distinct from the actor's.
	TSharedRef<IDatasmithMetaDataElement> MetaData =
		FDatasmithSceneFactory::CreateMetaData(*(FString(TEXT("md_")) + actorName));
	MetaData->SetAssociatedElement(*Actor);

	for (int32 i = 0; i < count; ++i)
	{
		if (!keys[i] || !*keys[i]) { continue; }

		TSharedRef<IDatasmithKeyValueProperty> Property =
			FDatasmithSceneFactory::CreateKeyValueProperty(keys[i]);
		Property->SetPropertyType(EDatasmithKeyValuePropertyType::String);
		Property->SetValue(values[i] ? values[i] : TEXT(""));
		MetaData->AddProperty(Property);
	}

	Bridge->Scene->AddMetaData(MetaData);
	return 0;
}

int NavExDs_Export(void* scene)
{
	FBridgeScene* Bridge = AsScene(scene);
	if (!Bridge) { return 1; }

	if (!Bridge->Scene.IsValid())
	{
		SetError(TEXT("Scene was not created"));
		return 1;
	}

	// bCleanupUnusedElements=false: NavEx decides what belongs in a set, and a
	// mesh with no actor yet is a NavEx bug worth seeing rather than something
	// to silently drop.
	Bridge->Exporter.Export(Bridge->Scene.ToSharedRef(), /*bCleanupUnusedElements=*/false);
	return 0;
}

void NavExDs_DestroyScene(void* scene)
{
	delete static_cast<FBridgeScene*>(scene);
}
