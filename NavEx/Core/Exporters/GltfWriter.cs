using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace NavEx.Core.Exporters
{
    /// <summary>
    /// Writes a <see cref="SceneData"/> as glTF 2.0 — either a single self-contained
    /// .glb, a .gltf with a sibling .bin, or a .gltf with the buffer inlined as a
    /// data URI.
    ///
    /// The output deliberately sticks to the core spec with no extensions: no Draco,
    /// no quantisation, no KHR_* anything. That keeps the files loadable by every
    /// consumer that claims glTF support (Blender, Unreal, Unity, three.js, Windows
    /// 3D Viewer, Power BI, Speckle, Twinmotion) without a plugin. Size comes from
    /// welding, material merging and index narrowing instead.
    /// </summary>
    internal class GltfWriter
    {
        private const int ComponentFloat = 5126;
        private const int ComponentUShort = 5123;
        private const int ComponentUInt = 5125;
        private const int TargetArrayBuffer = 34962;
        private const int TargetElementArrayBuffer = 34963;

        private readonly ExportOptions _options;
        private readonly MemoryStream _binary = new MemoryStream();
        private readonly JArr _bufferViews = new JArr();
        private readonly JArr _accessors = new JArr();

        public GltfWriter(ExportOptions options)
        {
            _options = options;
        }

        public ExportResult Write(SceneData scene, string outputPath)
        {
            JObj gltf = BuildDocument(scene, Path.GetFileNameWithoutExtension(outputPath));

            var result = new ExportResult
            {
                FilePath = outputPath,
                TriangleCount = scene.TriangleCount,
                VertexCount = scene.VertexCount,
                MaterialCount = scene.Materials == null ? 0 : scene.Materials.Count,
                NodeCount = CountNodes(scene)
            };

            switch (_options.Format)
            {
                case ExportFormat.Glb:
                    WriteGlb(gltf, outputPath);
                    break;

                case ExportFormat.GltfEmbedded:
                    gltf.Set("buffers", new JArr().Add(new JObj()
                        .Set("byteLength", _binary.Length)
                        .Set("uri", "data:application/octet-stream;base64," + Convert.ToBase64String(_binary.ToArray()))));
                    File.WriteAllText(outputPath, gltf.ToString(), new UTF8Encoding(false));
                    break;

                default:
                {
                    string binName = Path.GetFileNameWithoutExtension(outputPath) + ".bin";
                    string binPath = Path.Combine(Path.GetDirectoryName(outputPath) ?? "", binName);
                    File.WriteAllBytes(binPath, _binary.ToArray());
                    gltf.Set("buffers", new JArr().Add(new JObj()
                        .Set("byteLength", _binary.Length)
                        .Set("uri", Uri.EscapeDataString(binName))));
                    File.WriteAllText(outputPath, gltf.ToString(), new UTF8Encoding(false));
                    result.SidecarFiles.Add(binPath);
                    break;
                }
            }

            result.FileSizeBytes = new FileInfo(outputPath).Length;
            foreach (string sidecar in result.SidecarFiles)
                if (File.Exists(sidecar)) result.FileSizeBytes += new FileInfo(sidecar).Length;

            return result;
        }

        private JObj BuildDocument(SceneData scene, string documentName)
        {
            var nodes = new JArr();
            var meshes = new JArr();
            var sceneNodeIndices = new JArr();

            foreach (NodeBuilder node in scene.NonEmptyNodes)
            {
                var primitives = new JArr();

                foreach (PrimitiveBucket bucket in node.Buckets.Values)
                {
                    foreach (MeshBuilder builder in bucket.Builders)
                    {
                        if (builder.IsEmpty) continue;
                        primitives.Add(BuildPrimitive(builder, bucket));
                    }
                }

                if (primitives.Count == 0) continue;

                meshes.Add(new JObj().Set("name", node.Name).Set("primitives", primitives));

                var jsonNode = new JObj()
                    .Set("name", node.Name)
                    .Set("mesh", meshes.Count - 1);

                if (_options.EmbedItemExtras && node.Extras.Count > 0)
                {
                    var extras = new JObj();
                    foreach (KeyValuePair<string, string> entry in node.Extras)
                        extras.Set(entry.Key, entry.Value);
                    jsonNode.Set("extras", extras);
                }

                nodes.Add(jsonNode);
                sceneNodeIndices.Add(nodes.Count - 1);
            }

            var asset = new JObj()
                .Set("version", "2.0")
                .Set("generator", "NavEx " + PluginInfo.Version + " (Navisworks glTF exporter)")
                .Set("extras", BuildProvenance(scene));

            var gltf = new JObj()
                .Set("asset", asset)
                .Set("scene", 0)
                .Set("scenes", new JArr().Add(new JObj()
                    .Set("name", string.IsNullOrEmpty(scene.Name) ? documentName : scene.Name)
                    .Set("nodes", sceneNodeIndices)));

            if (nodes.Count > 0) gltf.Set("nodes", nodes);
            if (meshes.Count > 0) gltf.Set("meshes", meshes);
            if (_options.EmitMaterials && scene.Materials != null && scene.Materials.Count > 0)
                gltf.Set("materials", BuildMaterials(scene));
            if (_accessors.Count > 0) gltf.Set("accessors", _accessors);
            if (_bufferViews.Count > 0) gltf.Set("bufferViews", _bufferViews);

            return gltf;
        }

        /// <summary>
        /// Records exactly how the coordinates were transformed. Without this a
        /// recentred export is un-georeferenceable: adding the offset back (before
        /// undoing the axis swap) returns the model to Navisworks world coordinates.
        /// </summary>
        private JObj BuildProvenance(SceneData scene)
        {
            return new JObj()
                .Set("navex:sourceDocument", scene.SourceDocument ?? "")
                .Set("navex:sourceUnits", scene.SourceUnits ?? "")
                .Set("navex:targetUnits", scene.TargetUnits ?? "")
                .Set("navex:upAxis", scene.YUpApplied ? "Y (converted from Z-up)" : "Z (unchanged)")
                .Set("navex:originMode", _options.Origin.ToString())
                .Set("navex:appliedOffset", JArr.Of(scene.AppliedOffset.X, scene.AppliedOffset.Y, scene.AppliedOffset.Z))
                .Set("navex:offsetNote", "Add appliedOffset to exported coordinates to return to source world coordinates.")
                .Set("navex:exportedUtc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
        }

        private JObj BuildPrimitive(MeshBuilder builder, PrimitiveBucket bucket)
        {
            var attributes = new JObj();
            attributes.Set("POSITION", AddFloatAccessor(builder.Positions, 3, "VEC3", true));

            if (builder.HasNormals && builder.Normals.Count == builder.Positions.Count)
                attributes.Set("NORMAL", AddFloatAccessor(builder.Normals, 3, "VEC3", false));

            if (builder.HasColors && builder.Colors.Count / 4 == builder.Positions.Count / 3)
                attributes.Set("COLOR_0", AddFloatAccessor(builder.Colors, 4, "VEC4", false));

            var primitive = new JObj()
                .Set("attributes", attributes)
                .Set("indices", AddIndexAccessor(builder.Indices, builder.VertexCount))
                .Set("mode", (int)bucket.Mode);

            if (_options.EmitMaterials && bucket.MaterialIndex >= 0)
                primitive.Set("material", bucket.MaterialIndex);

            return primitive;
        }

        private JArr BuildMaterials(SceneData scene)
        {
            var materials = new JArr();
            foreach (MaterialDef material in scene.Materials)
            {
                var pbr = new JObj()
                    .Set("baseColorFactor", JArr.Of(material.R, material.G, material.B, material.Alpha))
                    .Set("metallicFactor", material.Metallic)
                    .Set("roughnessFactor", material.Roughness);

                var json = new JObj()
                    .Set("name", material.Name ?? "material")
                    .Set("pbrMetallicRoughness", pbr)
                    .Set("doubleSided", material.DoubleSided);

                if (material.IsTransparent)
                    json.Set("alphaMode", "BLEND");

                if (material.EmissiveR > 0 || material.EmissiveG > 0 || material.EmissiveB > 0)
                    json.Set("emissiveFactor", JArr.Of(material.EmissiveR, material.EmissiveG, material.EmissiveB));

                materials.Add(json);
            }
            return materials;
        }

        // ── Buffer plumbing ───────────────────────────────────────────────────

        private int AddFloatAccessor(IList<float> values, int componentsPerElement, string type, bool withBounds)
        {
            int count = values.Count / componentsPerElement;
            var bytes = new byte[values.Count * 4];
            Buffer.BlockCopy(ToArray(values), 0, bytes, 0, bytes.Length);

            int bufferView = AddBufferView(bytes, TargetArrayBuffer);

            var accessor = new JObj()
                .Set("bufferView", bufferView)
                .Set("componentType", ComponentFloat)
                .Set("count", count)
                .Set("type", type);

            if (withBounds)
            {
                var min = new float[componentsPerElement];
                var max = new float[componentsPerElement];
                for (int c = 0; c < componentsPerElement; c++)
                {
                    min[c] = float.MaxValue;
                    max[c] = float.MinValue;
                }

                for (int i = 0; i < values.Count; i++)
                {
                    int c = i % componentsPerElement;
                    if (values[i] < min[c]) min[c] = values[i];
                    if (values[i] > max[c]) max[c] = values[i];
                }

                var minArray = new JArr();
                var maxArray = new JArr();
                for (int c = 0; c < componentsPerElement; c++)
                {
                    minArray.Add(count == 0 ? 0.0 : min[c]);
                    maxArray.Add(count == 0 ? 0.0 : max[c]);
                }

                // POSITION accessors are required by the spec to carry min/max —
                // viewers use them for frustum culling and auto-framing.
                accessor.Set("min", minArray).Set("max", maxArray);
            }

            _accessors.Add(accessor);
            return _accessors.Count - 1;
        }

        private int AddIndexAccessor(IList<uint> indices, int vertexCount)
        {
            // 16-bit indices halve the index buffer, which on welded BIM meshes is a
            // meaningful share of the file. Anything larger falls back to 32-bit.
            bool narrow = vertexCount <= ushort.MaxValue;
            byte[] bytes;

            if (narrow)
            {
                bytes = new byte[indices.Count * 2];
                for (int i = 0; i < indices.Count; i++)
                {
                    ushort value = (ushort)indices[i];
                    bytes[i * 2] = (byte)(value & 0xFF);
                    bytes[i * 2 + 1] = (byte)(value >> 8);
                }
            }
            else
            {
                bytes = new byte[indices.Count * 4];
                for (int i = 0; i < indices.Count; i++)
                {
                    uint value = indices[i];
                    bytes[i * 4] = (byte)(value & 0xFF);
                    bytes[i * 4 + 1] = (byte)((value >> 8) & 0xFF);
                    bytes[i * 4 + 2] = (byte)((value >> 16) & 0xFF);
                    bytes[i * 4 + 3] = (byte)((value >> 24) & 0xFF);
                }
            }

            int bufferView = AddBufferView(bytes, TargetElementArrayBuffer);

            _accessors.Add(new JObj()
                .Set("bufferView", bufferView)
                .Set("componentType", narrow ? ComponentUShort : ComponentUInt)
                .Set("count", indices.Count)
                .Set("type", "SCALAR"));

            return _accessors.Count - 1;
        }

        private int AddBufferView(byte[] data, int target)
        {
            // glTF requires each bufferView's offset to satisfy its accessor's
            // component alignment; padding to 4 covers every type we emit.
            PadTo(4);
            long offset = _binary.Length;
            _binary.Write(data, 0, data.Length);

            _bufferViews.Add(new JObj()
                .Set("buffer", 0)
                .Set("byteOffset", offset)
                .Set("byteLength", data.Length)
                .Set("target", target));

            return _bufferViews.Count - 1;
        }

        private void PadTo(int alignment)
        {
            long remainder = _binary.Length % alignment;
            if (remainder == 0) return;
            for (long i = remainder; i < alignment; i++)
                _binary.WriteByte(0);
        }

        private static float[] ToArray(IList<float> values)
        {
            var array = values as float[];
            if (array != null) return array;

            var list = values as List<float>;
            if (list != null) return list.ToArray();

            var copy = new float[values.Count];
            values.CopyTo(copy, 0);
            return copy;
        }

        private void WriteGlb(JObj gltf, string outputPath)
        {
            gltf.Set("buffers", new JArr().Add(new JObj().Set("byteLength", _binary.Length)));

            byte[] json = Encoding.UTF8.GetBytes(gltf.ToString());
            int jsonPadding = (4 - (json.Length % 4)) % 4;
            byte[] binary = _binary.ToArray();
            int binPadding = (4 - (binary.Length % 4)) % 4;

            int totalLength = 12 + 8 + json.Length + jsonPadding + 8 + binary.Length + binPadding;

            using (var stream = new FileStream(outputPath, FileMode.Create, FileAccess.Write))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(0x46546C67);          // "glTF"
                writer.Write(2);                   // container version
                writer.Write(totalLength);

                writer.Write(json.Length + jsonPadding);
                writer.Write(0x4E4F534A);          // "JSON"
                writer.Write(json);
                // The JSON chunk pads with spaces, the BIN chunk with zeros.
                for (int i = 0; i < jsonPadding; i++) writer.Write((byte)0x20);

                writer.Write(binary.Length + binPadding);
                writer.Write(0x004E4942);          // "BIN\0"
                writer.Write(binary);
                for (int i = 0; i < binPadding; i++) writer.Write((byte)0x00);
            }
        }

        private static int CountNodes(SceneData scene)
        {
            int count = 0;
            foreach (NodeBuilder node in scene.NonEmptyNodes) count++;
            return count;
        }
    }

    /// <summary>What one written file turned out to be.</summary>
    public class ExportResult
    {
        public string SetName = "";
        public string FilePath = "";
        public long FileSizeBytes;
        public int TriangleCount;
        public int VertexCount;
        public int MaterialCount;
        public int NodeCount;
        public bool Failed;
        public string FailureReason = "";
        public readonly List<string> SidecarFiles = new List<string>();

        public string SizeDisplay
        {
            get
            {
                double bytes = FileSizeBytes;
                string[] units = { "B", "KB", "MB", "GB" };
                int unit = 0;
                while (bytes >= 1024 && unit < units.Length - 1) { bytes /= 1024; unit++; }
                return string.Format(CultureInfo.InvariantCulture, "{0:0.#} {1}", bytes, units[unit]);
            }
        }
    }
}
