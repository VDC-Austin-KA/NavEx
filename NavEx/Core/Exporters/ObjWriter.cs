using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace NavEx.Core.Exporters
{
    /// <summary>
    /// Wavefront .obj + .mtl writer.
    ///
    /// OBJ has no binary form, no scene units and no PBR, so it is always the
    /// larger and lossier option — it is here because a lot of older fabrication
    /// and estimating software still only reads OBJ. Colour and opacity survive
    /// (Kd/d); roughness and metallic do not.
    /// </summary>
    internal class ObjWriter
    {
        private readonly ExportOptions _options;

        public ObjWriter(ExportOptions options)
        {
            _options = options;
        }

        public ExportResult Write(SceneData scene, string outputPath)
        {
            string materialFileName = Path.GetFileNameWithoutExtension(outputPath) + ".mtl";
            string materialPath = Path.Combine(Path.GetDirectoryName(outputPath) ?? "", materialFileName);

            var result = new ExportResult
            {
                FilePath = outputPath,
                TriangleCount = scene.TriangleCount,
                VertexCount = scene.VertexCount,
                MaterialCount = scene.Materials == null ? 0 : scene.Materials.Count
            };

            var invariant = CultureInfo.InvariantCulture;

            using (var writer = new StreamWriter(outputPath, false, new UTF8Encoding(false)))
            {
                writer.WriteLine("# Exported by NavEx " + PluginInfo.Version);
                writer.WriteLine("# Source: " + scene.SourceDocument);
                writer.WriteLine("# Units: " + scene.TargetUnits + "   Up axis: " + (scene.YUpApplied ? "Y" : "Z"));
                writer.WriteLine(string.Format(invariant,
                    "# Applied offset (add to return to source world coordinates): {0} {1} {2}",
                    scene.AppliedOffset.X, scene.AppliedOffset.Y, scene.AppliedOffset.Z));

                if (_options.EmitMaterials && scene.Materials != null && scene.Materials.Count > 0)
                    writer.WriteLine("mtllib " + materialFileName);

                // OBJ indices are 1-based and file-global, so both counters run
                // across every node rather than resetting per group.
                int positionBase = 1;
                int normalBase = 1;
                int nodeIndex = 0;

                foreach (NodeBuilder node in scene.NonEmptyNodes)
                {
                    writer.WriteLine("o " + SanitizeToken(node.Name, "node_" + nodeIndex.ToString(invariant)));
                    nodeIndex++;
                    result.NodeCount++;

                    foreach (PrimitiveBucket bucket in node.Buckets.Values)
                    {
                        if (bucket.Mode != PrimitiveMode.Triangles) continue;

                        if (_options.EmitMaterials && scene.Materials != null &&
                            bucket.MaterialIndex >= 0 && bucket.MaterialIndex < scene.Materials.Count)
                        {
                            writer.WriteLine("usemtl " + SanitizeToken(scene.Materials[bucket.MaterialIndex].Name, "material"));
                        }

                        foreach (MeshBuilder builder in bucket.Builders)
                        {
                            if (builder.IsEmpty) continue;

                            IList<float> positions = builder.Positions;
                            for (int i = 0; i < positions.Count; i += 3)
                            {
                                writer.WriteLine(string.Format(invariant, "v {0:0.######} {1:0.######} {2:0.######}",
                                    positions[i], positions[i + 1], positions[i + 2]));
                            }

                            bool hasNormals = builder.HasNormals && builder.Normals.Count == positions.Count;
                            if (hasNormals)
                            {
                                IList<float> normals = builder.Normals;
                                for (int i = 0; i < normals.Count; i += 3)
                                {
                                    writer.WriteLine(string.Format(invariant, "vn {0:0.#####} {1:0.#####} {2:0.#####}",
                                        normals[i], normals[i + 1], normals[i + 2]));
                                }
                            }

                            IList<uint> indices = builder.Indices;
                            for (int i = 0; i + 2 < indices.Count; i += 3)
                            {
                                long a = positionBase + indices[i];
                                long b = positionBase + indices[i + 1];
                                long c = positionBase + indices[i + 2];

                                if (hasNormals)
                                {
                                    long na = normalBase + indices[i];
                                    long nb = normalBase + indices[i + 1];
                                    long nc = normalBase + indices[i + 2];
                                    writer.WriteLine(string.Format(invariant, "f {0}//{1} {2}//{3} {4}//{5}",
                                        a, na, b, nb, c, nc));
                                }
                                else
                                {
                                    writer.WriteLine(string.Format(invariant, "f {0} {1} {2}", a, b, c));
                                }
                            }

                            positionBase += builder.VertexCount;
                            if (hasNormals) normalBase += builder.VertexCount;
                        }
                    }
                }
            }

            if (_options.EmitMaterials && scene.Materials != null && scene.Materials.Count > 0)
            {
                WriteMaterialLibrary(scene, materialPath);
                result.SidecarFiles.Add(materialPath);
            }

            result.FileSizeBytes = new FileInfo(outputPath).Length;
            foreach (string sidecar in result.SidecarFiles)
                if (File.Exists(sidecar)) result.FileSizeBytes += new FileInfo(sidecar).Length;

            return result;
        }

        private static void WriteMaterialLibrary(SceneData scene, string materialPath)
        {
            var invariant = CultureInfo.InvariantCulture;

            using (var writer = new StreamWriter(materialPath, false, new UTF8Encoding(false)))
            {
                writer.WriteLine("# Exported by NavEx " + PluginInfo.Version);

                foreach (MaterialDef material in scene.Materials)
                {
                    writer.WriteLine();
                    writer.WriteLine("newmtl " + SanitizeToken(material.Name, "material"));
                    writer.WriteLine(string.Format(invariant, "Kd {0:0.####} {1:0.####} {2:0.####}",
                        material.R, material.G, material.B));
                    writer.WriteLine(string.Format(invariant, "Ka {0:0.####} {1:0.####} {2:0.####}",
                        material.R * 0.2, material.G * 0.2, material.B * 0.2));

                    // Roughness has no OBJ equivalent; the closest honest mapping is
                    // a specular exponent derived from it.
                    double specularExponent = Math.Max(1.0, (1.0 - material.Roughness) * 200.0);
                    writer.WriteLine(string.Format(invariant, "Ks 0.1 0.1 0.1"));
                    writer.WriteLine(string.Format(invariant, "Ns {0:0.##}", specularExponent));
                    writer.WriteLine(string.Format(invariant, "d {0:0.####}", material.Alpha));
                    writer.WriteLine("illum 2");

                    if (material.EmissiveR > 0 || material.EmissiveG > 0 || material.EmissiveB > 0)
                    {
                        writer.WriteLine(string.Format(invariant, "Ke {0:0.####} {1:0.####} {2:0.####}",
                            material.EmissiveR, material.EmissiveG, material.EmissiveB));
                    }
                }
            }
        }

        /// <summary>OBJ tokens are whitespace-delimited, so names cannot contain spaces.</summary>
        private static string SanitizeToken(string value, string fallback)
        {
            if (string.IsNullOrWhiteSpace(value)) return fallback;

            var sb = new StringBuilder(value.Length);
            foreach (char c in value)
                sb.Append(char.IsWhiteSpace(c) ? '_' : c);
            return sb.ToString();
        }
    }
}
