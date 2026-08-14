using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace NavEx.Core.Exporters
{
    /// <summary>
    /// Writes a <see cref="SceneData"/> as FBX ASCII 7.4 — hand-rolled, same reasoning
    /// as <see cref="GltfWriter"/> and <see cref="Json.cs"/>: no binary SDK dependency,
    /// so the plugin stays a single DLL and the writer stays offline-testable.
    ///
    /// Geometry only: one Geometry+Model pair per scene node, vertices already baked
    /// with the export's origin/unit/up-axis transform (mirrors how GltfWriter treats
    /// nodes — no separate node-local transform is written). No materials, no
    /// animation, no skinning: FBX consumers that need more than geometry are better
    /// served by the glTF or Datasmith outputs.
    /// </summary>
    internal class FbxWriter
    {
        private readonly ExportOptions _options;
        private long _nextId = 100000000; // arbitrary but stable base, away from FBX's reserved low IDs

        public FbxWriter(ExportOptions options)
        {
            _options = options;
        }

        public ExportResult Write(SceneData scene, string outputPath)
        {
            var result = new ExportResult
            {
                FilePath = outputPath,
                TriangleCount = scene.TriangleCount,
                VertexCount = scene.VertexCount,
                MaterialCount = 0
            };

            var invariant = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();

            sb.Append("; FBX 7.4.0 project file\n");
            sb.Append("; Written by NavEx ").Append(PluginInfo.Version).Append(" (hand-rolled ASCII FBX, no SDK)\n");
            sb.Append("; navex:sourceDocument=").Append(Escape(scene.SourceDocument)).Append('\n');
            sb.Append("; navex:sourceUnits=").Append(Escape(scene.SourceUnits)).Append('\n');
            sb.Append("; navex:targetUnits=").Append(Escape(scene.TargetUnits)).Append('\n');
            sb.Append("; navex:upAxis=").Append(scene.YUpApplied ? "Y (converted from Z-up)" : "Z (unchanged)").Append('\n');
            sb.Append("; navex:appliedOffset=").Append(string.Format(invariant, "{0},{1},{2}",
                scene.AppliedOffset.X, scene.AppliedOffset.Y, scene.AppliedOffset.Z)).Append('\n');
            sb.Append("; navex:offsetNote=Add appliedOffset to exported coordinates to return to source world coordinates.\n");
            sb.Append("; ----------------------------------------------------\n\n");

            sb.Append("FBXHeaderExtension:  {\n");
            sb.Append("\tFBXHeaderVersion: 1003\n");
            sb.Append("\tFBXVersion: 7400\n");
            sb.Append("\tCreator: \"NavEx ").Append(Escape(PluginInfo.Version)).Append("\"\n");
            sb.Append("}\n\n");

            sb.Append("GlobalSettings:  {\n");
            sb.Append("\tVersion: 1000\n");
            sb.Append("\tProperties70:  {\n");
            sb.Append("\t\tP: \"UpAxis\", \"int\", \"Integer\", \"\",1\n");
            sb.Append("\t\tP: \"UpAxisSign\", \"int\", \"Integer\", \"\",1\n");
            sb.Append("\t\tP: \"UnitScaleFactor\", \"double\", \"Number\", \"\",1\n");
            sb.Append("\t}\n");
            sb.Append("}\n\n");

            var connections = new List<string>();
            int nodeCount = 0;

            sb.Append("Objects:  {\n");

            foreach (NodeBuilder node in scene.NonEmptyNodes)
            {
                long geomId = _nextId++;
                long modelId = _nextId++;
                string safeName = SanitizeName(node.Name, "node_" + nodeCount);

                WriteGeometry(sb, geomId, safeName, node, invariant);
                WriteModel(sb, modelId, safeName);

                connections.Add(string.Format(invariant, "\tC: \"OO\",{0},{1}", geomId, modelId));
                connections.Add(string.Format(invariant, "\tC: \"OO\",{0},0", modelId));

                nodeCount++;
                result.NodeCount++;
            }

            sb.Append("}\n\n");

            sb.Append("Connections:  {\n");
            foreach (string c in connections) sb.Append(c).Append('\n');
            sb.Append("}\n");

            File.WriteAllText(outputPath, sb.ToString(), new UTF8Encoding(false));
            result.FileSizeBytes = new FileInfo(outputPath).Length;
            return result;
        }

        private static void WriteGeometry(StringBuilder sb, long id, string name, NodeBuilder node, CultureInfo invariant)
        {
            sb.Append("\tGeometry: ").Append(id).Append(", \"Geometry::").Append(name).Append("\", \"Mesh\" {\n");

            var positions = new List<float>();
            var normals = new List<float>();
            var polyIndex = new List<int>();
            bool anyNormals = false;

            foreach (PrimitiveBucket bucket in node.Buckets.Values)
            {
                if (bucket.Mode != PrimitiveMode.Triangles) continue;

                foreach (MeshBuilder builder in bucket.Builders)
                {
                    if (builder.IsEmpty) continue;

                    int vertexBase = positions.Count / 3;
                    positions.AddRange(builder.Positions);

                    bool hasNormals = builder.HasNormals && builder.Normals.Count == builder.Positions.Count;
                    if (hasNormals) { normals.AddRange(builder.Normals); anyNormals = true; }

                    IList<uint> indices = builder.Indices;
                    for (int i = 0; i + 2 < indices.Count; i += 3)
                    {
                        // FBX polygon convention: the last index of a polygon is bitwise-negated-minus-one.
                        polyIndex.Add(vertexBase + (int)indices[i]);
                        polyIndex.Add(vertexBase + (int)indices[i + 1]);
                        polyIndex.Add(~(vertexBase + (int)indices[i + 2]));
                    }
                }
            }

            sb.Append("\t\tVertices: *").Append(positions.Count).Append(" {\n\t\t\ta: ");
            AppendFloats(sb, positions, invariant);
            sb.Append("\n\t\t}\n");

            sb.Append("\t\tPolygonVertexIndex: *").Append(polyIndex.Count).Append(" {\n\t\t\ta: ");
            for (int i = 0; i < polyIndex.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(polyIndex[i].ToString(invariant));
            }
            sb.Append("\n\t\t}\n");

            if (anyNormals && normals.Count == positions.Count)
            {
                sb.Append("\t\tLayerElementNormal: 0 {\n");
                sb.Append("\t\t\tVersion: 101\n");
                sb.Append("\t\t\tName: \"\"\n");
                sb.Append("\t\t\tMappingInformationType: \"ByPolygonVertex\"\n");
                sb.Append("\t\t\tReferenceInformationType: \"Direct\"\n");
                sb.Append("\t\t\tNormals: *").Append(normals.Count).Append(" {\n\t\t\t\ta: ");
                AppendFloats(sb, normals, invariant);
                sb.Append("\n\t\t\t}\n");
                sb.Append("\t\t}\n");

                sb.Append("\t\tLayer: 0 {\n");
                sb.Append("\t\t\tVersion: 100\n");
                sb.Append("\t\t\tLayerElement:  {\n");
                sb.Append("\t\t\t\tType: \"LayerElementNormal\"\n");
                sb.Append("\t\t\t\tTypedIndex: 0\n");
                sb.Append("\t\t\t}\n");
                sb.Append("\t\t}\n");
            }

            sb.Append("\t}\n");
        }

        private static void WriteModel(StringBuilder sb, long id, string name)
        {
            sb.Append("\tModel: ").Append(id).Append(", \"Model::").Append(name).Append("\", \"Mesh\" {\n");
            sb.Append("\t\tVersion: 232\n");
            sb.Append("\t\tProperties70:  {\n");
            sb.Append("\t\t\tP: \"Lcl Translation\", \"Lcl Translation\", \"\", \"A\",0,0,0\n");
            sb.Append("\t\t\tP: \"Lcl Rotation\", \"Lcl Rotation\", \"\", \"A\",0,0,0\n");
            sb.Append("\t\t\tP: \"Lcl Scaling\", \"Lcl Scaling\", \"\", \"A\",1,1,1\n");
            sb.Append("\t\t}\n");
            sb.Append("\t}\n");
        }

        private static void AppendFloats(StringBuilder sb, List<float> values, CultureInfo invariant)
        {
            for (int i = 0; i < values.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(values[i].ToString("0.######", invariant));
            }
        }

        private static string Escape(string value)
        {
            return (value ?? "").Replace("\"", "'");
        }

        /// <summary>Exposed so DatasmithWriter's payload references line up with the names this writer emits.</summary>
        internal static string SanitizeName(string value, string fallback)
        {
            if (string.IsNullOrWhiteSpace(value)) return fallback;
            var sb = new StringBuilder(value.Length);
            foreach (char c in value)
                sb.Append(char.IsLetterOrDigit(c) ? c : '_');
            return sb.Length == 0 ? fallback : sb.ToString();
        }
    }
}
