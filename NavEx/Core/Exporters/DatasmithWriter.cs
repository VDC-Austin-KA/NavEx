using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using NavEx.FourD;

namespace NavEx.Core.Exporters
{
    /// <summary>
    /// Writes a <see cref="SceneData"/> as a real Datasmith scene, through Epic's
    /// Datasmith Export SDK via <see cref="DatasmithNative"/>.
    ///
    /// This replaces a hand-written `.udatasmith` that pointed its mesh payload at an
    /// FBX. Nothing could import those: Datasmith reads only its own binary
    /// `.udsmesh`, and handed an FBX the loader reads a garbage element count and
    /// loops on read-past-EOF, which is why a full-model import produced a multi-GB
    /// log and took the editor down rather than failing. The XML was wrong in
    /// several other ways besides — format version, lowercase `file` element,
    /// `label` attribute, required `Size`/`Hash`/`Material` children — so none of it
    /// survived; the SDK owns the whole format now.
    ///
    /// What did survive is the contract that matters downstream: one scene per
    /// Navisworks selection set, and every `pixmy.*` key spelled exactly as before.
    /// `pixmy.set.name` is what Unrealistic4D joins schedule tasks on, and
    /// <see cref="ScheduleJsonWriter"/> emits the same value.
    /// </summary>
    internal class DatasmithWriter
    {
        private const string ContractVersion = "1.0";

        private readonly ExportOptions _options;

        public DatasmithWriter(ExportOptions options)
        {
            _options = options;
        }

        public ExportResult Write(SceneData scene, string outputPath)
        {
            if (!DatasmithNative.Available)
                throw new InvalidOperationException(
                    "Datasmith export needs the native SDK bridge. " + DatasmithNative.UnavailableReason);

            DatasmithNative.EnsureInitialized();

            var result = new ExportResult
            {
                FilePath = outputPath,
                TriangleCount = scene.TriangleCount,
                VertexCount = scene.VertexCount,
                // No material elements are created yet, matching what the previous
                // writer produced. Face material ids are carried no further than the
                // mesh, so nothing references a material that does not exist.
                MaterialCount = 0
            };

            string sceneName = Path.GetFileNameWithoutExtension(outputPath);
            string outputDir = Path.GetDirectoryName(outputPath) ?? "";

            IntPtr handle = DatasmithNative.NavExDs_BeginScene(
                sceneName, outputDir,
                "NavEx", "NavEx", "NavEx Navisworks Exporter", PluginInfo.Version);

            if (handle == IntPtr.Zero)
                throw new InvalidOperationException(
                    "Datasmith could not start a scene for '" + sceneName + "': " + DatasmithNative.LastError());

            try
            {
                int index = 0;
                foreach (NodeBuilder node in scene.NonEmptyNodes)
                {
                    string meshName = ElementName(node, index);
                    string actorName = "actor_" + meshName;

                    if (WriteNode(handle, node, meshName, actorName, index, scene))
                        result.NodeCount++;

                    index++;
                }

                if (result.NodeCount == 0)
                    throw new InvalidOperationException(
                        "'" + sceneName + "' produced no triangles that Datasmith could accept.");

                if (DatasmithNative.NavExDs_Export(handle) != 0)
                    throw new InvalidOperationException(
                        "Datasmith export of '" + sceneName + "' failed: " + DatasmithNative.LastError());
            }
            finally
            {
                DatasmithNative.NavExDs_DestroyScene(handle);
            }

            result.FileSizeBytes = SizeOf(outputPath);

            // The SDK writes the mesh payloads into a sibling `_Assets` folder. Report
            // them so the export summary accounts for the bytes actually produced.
            string assetsDir = Path.Combine(outputDir, sceneName + "_Assets");
            if (Directory.Exists(assetsDir))
            {
                foreach (string payload in Directory.GetFiles(assetsDir))
                {
                    result.SidecarFiles.Add(payload);
                    result.FileSizeBytes += SizeOf(payload);
                }
            }

            return result;
        }

        /// <summary>
        /// Flattens one node's buckets into the single mesh Datasmith wants, places an
        /// actor for it and attaches the schedule linkage. Returns false when the node
        /// held no triangles (line-only geometry, for instance).
        /// </summary>
        private bool WriteNode(IntPtr handle, NodeBuilder node, string meshName, string actorName,
                               int index, SceneData scene)
        {
            var positions = new List<float>();
            var normals = new List<float>();
            var indices = new List<int>();
            bool allHaveNormals = true;

            foreach (PrimitiveBucket bucket in node.Buckets.Values)
            {
                // Datasmith meshes are triangles. Line primitives have no
                // representation here and are simply not exported.
                if (bucket.Mode != PrimitiveMode.Triangles) continue;

                foreach (MeshBuilder builder in bucket.Builders)
                {
                    if (builder.IsEmpty) continue;

                    // Builders are split at the vertex cap, so each one restarts its
                    // indices at zero and has to be rebased as it is appended.
                    int baseVertex = positions.Count / 3;

                    foreach (float value in builder.Positions) positions.Add(value);

                    if (builder.HasNormals && builder.Normals.Count == builder.Positions.Count)
                    {
                        foreach (float value in builder.Normals) normals.Add(value);
                    }
                    else
                    {
                        allHaveNormals = false;
                    }

                    foreach (uint i in builder.Indices) indices.Add(baseVertex + (int)i);
                }
            }

            if (indices.Count < 3) return false;

            // Partial normals cannot be handed over: the array has to line up with the
            // vertices or every face after the gap is shaded from the wrong data. All
            // or nothing, and Datasmith computes them when they are absent.
            float[] normalArray = allHaveNormals && normals.Count == positions.Count
                ? normals.ToArray()
                : null;

            if (DatasmithNative.NavExDs_AddMesh(
                    handle, meshName,
                    positions.ToArray(), positions.Count / 3,
                    indices.ToArray(), indices.Count / 3,
                    normalArray,
                    null,   // NavEx generates no UVs; the SDK builds lightmap UVs itself
                    null) != 0)
            {
                throw new InvalidOperationException(
                    "Datasmith rejected mesh '" + meshName + "': " + DatasmithNative.LastError());
            }

            if (DatasmithNative.NavExDs_AddMeshActor(handle, actorName, meshName, meshName, "NavEx") != 0)
            {
                throw new InvalidOperationException(
                    "Datasmith rejected actor '" + actorName + "': " + DatasmithNative.LastError());
            }

            List<KeyValuePair<string, string>> meta = BuildNodeMetaData(node, index, scene);
            if (meta.Count > 0)
            {
                var keys = new string[meta.Count];
                var values = new string[meta.Count];
                for (int i = 0; i < meta.Count; i++)
                {
                    keys[i] = meta[i].Key;
                    values[i] = meta[i].Value;
                }

                if (DatasmithNative.NavExDs_AddMetaData(handle, actorName, keys, values, meta.Count) != 0)
                {
                    throw new InvalidOperationException(
                        "Datasmith rejected metadata for '" + actorName + "': " + DatasmithNative.LastError());
                }
            }

            return true;
        }

        /// <summary>
        /// The name Unreal will end up calling this node's StaticMesh asset. Prefers
        /// the selection set: that is the schedule's granularity, the value of
        /// pixmy.set.name, and the folder the Datasmith import drops the asset into, so
        /// an asset named after it resolves by exact path on the Unreal side. Falls
        /// back to the node name when the node came from no named set, and suffixes the
        /// index when one file holds several nodes, since Datasmith element names have
        /// to be unique within a scene.
        /// </summary>
        internal static string ElementName(NodeBuilder node, int index)
        {
            string setName;
            node.Extras.TryGetValue("navex:set", out setName);
            string basis = string.IsNullOrEmpty(setName) ? node.Name : setName;
            string safe = FbxWriter.SanitizeName(basis, "node_" + index);
            return index == 0 ? safe : safe + "_" + index.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// The `pixmy.*` schedule linkage plus the `navex:*` provenance, as key/value
        /// pairs for the SDK's metadata element.
        ///
        /// Provenance used to be its own top-level XML element, which is not something
        /// the SDK's schema has a place for, so it rides along per actor instead. The
        /// values and their spellings are unchanged either way — Unrealistic4D reads
        /// them by name.
        /// </summary>
        internal List<KeyValuePair<string, string>> BuildNodeMetaData(NodeBuilder node, int index, SceneData scene)
        {
            var invariant = CultureInfo.InvariantCulture;
            var meta = new List<KeyValuePair<string, string>>();

            string setName;
            node.Extras.TryGetValue("navex:set", out setName);
            setName = setName ?? "";

            Add(meta, "pixmy.contractVersion", ContractVersion);

            string guid;
            bool synthetic;
            ResolveSourceGuid(node, index, scene, out guid, out synthetic);
            Add(meta, "pixmy.sourceGuid", guid);
            Add(meta, "pixmy.guidSynthetic", synthetic ? "true" : "false");

            Add(meta, "pixmy.set.name", setName);

            ScheduleTask task = null;
            if (_options.DatasmithTaskLinks != null && setName.Length > 0)
                _options.DatasmithTaskLinks.TryGetValue(setName, out task);

            if (task != null)
            {
                Add(meta, "pixmy.task.stableKey", task.StableKey);
                AddIfPresent(meta, "pixmy.task.displayName", task.Name);
                AddDateIfPresent(meta, "pixmy.task.plannedStart", task.PlannedStart);
                AddDateIfPresent(meta, "pixmy.task.plannedFinish", task.PlannedFinish);
                AddDateIfPresent(meta, "pixmy.task.actualStart", task.ActualStart);
                AddDateIfPresent(meta, "pixmy.task.actualFinish", task.ActualFinish);
                Add(meta, "pixmy.task.type", task.NormalizedTaskType());
            }

            FourDName parsed = FourDName.TryParse(setName);
            if (parsed != null)
            {
                AddIfPresent(meta, "pixmy.name.sequenceCode", parsed.SequenceCode);
                AddIfPresent(meta, "pixmy.name.zone", parsed.Zone);
                AddIfPresent(meta, "pixmy.name.level", parsed.LevelTag);
                AddIfPresent(meta, "pixmy.name.discipline", parsed.DisciplineCode);
                AddIfPresent(meta, "pixmy.name.activity", parsed.ActivityCode);
            }

            Add(meta, "navex:sourceDocument", scene.SourceDocument ?? "");
            Add(meta, "navex:sourceUnits", scene.SourceUnits ?? "");
            Add(meta, "navex:targetUnits", scene.TargetUnits ?? "");
            Add(meta, "navex:upAxis", scene.YUpApplied ? "Y (converted from Z-up)" : "Z (unchanged)");
            Add(meta, "navex:originMode", _options.Origin.ToString());
            Add(meta, "navex:appliedOffset", string.Format(invariant, "{0},{1},{2}",
                scene.AppliedOffset.X, scene.AppliedOffset.Y, scene.AppliedOffset.Z));
            Add(meta, "navex:offsetNote",
                "Add appliedOffset to exported coordinates to return to source world coordinates.");
            Add(meta, "navex:exportedUtc", DateTime.UtcNow.ToString("o", invariant));

            return meta;
        }

        /// <summary>
        /// The whole point of this contract: a stable, non-zero, per-export-unique GUID.
        /// Prefers the real Navisworks instance GUID NavEx already stamps into
        /// node.Extras["navex:guid"] for per-item exports (see GeometryExtractor.
        /// PopulateItemExtras) — but that field is all-zeroes in the real sample data
        /// (the bug this contract exists to replace) and is entirely absent for
        /// merge-by-material/per-model-file exports, so both cases fall back to a
        /// deterministic hash of the source document plus the node's identity within
        /// this export. That hash is stable across re-exports of an unchanged model
        /// (same document name, same node name/index) and unique within one export
        /// (the index is folded in).
        /// </summary>
        private static void ResolveSourceGuid(NodeBuilder node, int index, SceneData scene, out string guid, out bool synthetic)
        {
            string raw;
            Guid parsed;
            if (node.Extras.TryGetValue("navex:guid", out raw) &&
                Guid.TryParse(raw, out parsed) && parsed != Guid.Empty)
            {
                guid = parsed.ToString();
                synthetic = false;
                return;
            }

            guid = SynthesizeGuid(scene.SourceDocument, index + "/" + node.Name).ToString();
            synthetic = true;
        }

        private static Guid SynthesizeGuid(string modelFileName, string itemPath)
        {
            using (MD5 md5 = MD5.Create())
            {
                byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes((modelFileName ?? "") + "|" + (itemPath ?? "")));

                // Stamp RFC 4122 version/variant bits so the result reads as an ordinary
                // (version 4, name-derived) GUID rather than raw hash bytes.
                hash[7] = (byte)((hash[7] & 0x0F) | 0x40);
                hash[8] = (byte)((hash[8] & 0x3F) | 0x80);

                bool allZero = true;
                for (int i = 0; i < hash.Length; i++)
                {
                    if (hash[i] != 0) { allZero = false; break; }
                }
                // MD5 of a non-empty string being all-zero is not realistically reachable,
                // but the contract is explicit that this must never happen — guard it anyway.
                if (allZero) hash[0] = 0x01;

                return new Guid(hash);
            }
        }

        private static long SizeOf(string path)
        {
            try
            {
                return File.Exists(path) ? new FileInfo(path).Length : 0;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private static void Add(List<KeyValuePair<string, string>> meta, string name, string value)
        {
            meta.Add(new KeyValuePair<string, string>(name, value ?? ""));
        }

        private static void AddIfPresent(List<KeyValuePair<string, string>> meta, string name, string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            Add(meta, name, value);
        }

        private static void AddDateIfPresent(List<KeyValuePair<string, string>> meta, string name, DateTime? value)
        {
            if (!value.HasValue) return;
            // ISO 8601, no time zone offset — the schedule-link contract is explicit about this.
            Add(meta, name, value.Value.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture));
        }
    }
}
