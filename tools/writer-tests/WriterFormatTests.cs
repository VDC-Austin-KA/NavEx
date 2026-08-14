using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using NavEx.Core;
using NavEx.Core.Exporters;
using NavEx.FourD;

namespace NavEx
{
    /// <summary>
    /// Structural checks for the hand-rolled FBX and Datasmith writers (Tasks N2/N3).
    /// No Navisworks types, no Datasmith/FBX SDK — same offline-testable shape as the
    /// GLB/OBJ checks in Program.cs. This cannot confirm a real Unreal or DCC import
    /// succeeds; it confirms the files are structurally sound and, for Datasmith,
    /// that every field the schedule-link contract requires is present, correctly
    /// named, and that no emitted GUID is the all-zeroes placeholder the contract
    /// exists to get rid of.
    /// </summary>
    internal static class WriterFormatTests
    {
        private static int _failures;
        private static int _checks;

        public static int Run()
        {
            _failures = 0;
            _checks = 0;

            Console.WriteLine();
            Console.WriteLine("Writer format tests (FBX / Datasmith)");
            Console.WriteLine("--------------------------------------");

            FbxStructuralInvariants();
            DatasmithContractFields();

            Console.WriteLine();
            Console.WriteLine(_failures == 0
                ? string.Format(CultureInfo.InvariantCulture, "All {0} writer-format checks passed.", _checks)
                : string.Format(CultureInfo.InvariantCulture, "{0} of {1} writer-format checks FAILED.", _failures, _checks));

            return _failures;
        }

        private static void FbxStructuralInvariants()
        {
            var options = new ExportOptions { OutputFolder = ".", IncludeNormals = true, WeldVertices = false };
            SceneData scene = BuildUnitCube(options, out int triangleFaces);

            string path = "out_test.fbx";
            ExportResult result = new FbxWriter(options).Write(scene, path);
            string text = File.ReadAllText(path);

            Check("FBX file was written", File.Exists(path));
            Check("FBX header names the format", text.Contains("FBXHeaderExtension"));
            // Trailing space distinguishes the "Geometry: <id>," block header from the
            // "Geometry::cube" quoted object name on the same line, which also contains
            // the bare substring "Geometry:".
            Check("FBX declares one Geometry block", CountOccurrences(text, "Geometry: ") == 1);
            Check("FBX declares one Model block", CountOccurrences(text, "Model: ") == 1);
            Check("FBX carries a PolygonVertexIndex block", text.Contains("PolygonVertexIndex:"));
            Check("FBX triangle count matches the source scene", result.TriangleCount == triangleFaces,
                result.TriangleCount.ToString(CultureInfo.InvariantCulture));
            Check("FBX node count is 1", result.NodeCount == 1, result.NodeCount.ToString(CultureInfo.InvariantCulture));

            // Unwelded cube: 12 triangles * 3 = 36 vertices, so *36 on the Vertices array (3 floats each -> *108).
            Check("FBX Vertices block declares the unwelded vertex count",
                text.Contains("Vertices: *108"), FirstLineContaining(text, "Vertices: *"));
        }

        private static SceneData BuildUnitCube(ExportOptions options, out int triangleFaces)
        {
            double[][] corners =
            {
                new double[]{0,0,0}, new double[]{1,0,0}, new double[]{1,1,0}, new double[]{0,1,0},
                new double[]{0,0,1}, new double[]{1,0,1}, new double[]{1,1,1}, new double[]{0,1,1}
            };
            int[][] faces =
            {
                new[]{0,2,1}, new[]{0,3,2}, new[]{4,5,6}, new[]{4,6,7},
                new[]{0,1,5}, new[]{0,5,4}, new[]{1,2,6}, new[]{1,6,5},
                new[]{2,3,7}, new[]{2,7,6}, new[]{3,0,4}, new[]{3,4,7}
            };

            var materials = new MaterialResolver(options);
            var scene = new SceneData
            {
                Name = "UnitCube",
                Materials = materials.Materials,
                SourceDocument = "cube.nwd",
                SourceUnits = "Feet",
                TargetUnits = "Meters",
                YUpApplied = true,
                AppliedOffset = new Vec3(0, 0, 0)
            };

            var node = new NodeBuilder("cube", options);
            node.Extras["navex:set"] = "CUBE";
            PrimitiveBucket bucket = node.Bucket(materials.DefaultMaterialIndex, PrimitiveMode.Triangles);

            foreach (int[] face in faces)
            {
                Vec3 p1 = Corner(corners, face[0]), p2 = Corner(corners, face[1]), p3 = Corner(corners, face[2]);
                Vec3 n = Vec3.Cross(p2 - p1, p3 - p1).Normalized();
                MeshBuilder builder = bucket.Current(3);
                builder.AddIndex(builder.AddVertex(p1, n, 1, 1, 1, 1));
                builder.AddIndex(builder.AddVertex(p2, n, 1, 1, 1, 1));
                builder.AddIndex(builder.AddVertex(p3, n, 1, 1, 1, 1));
            }

            scene.Nodes.Add(node);
            foreach (MeshBuilder b in bucket.Builders) scene.Bounds.Add(b.Bounds);

            triangleFaces = faces.Length;
            return scene;
        }

        private static Vec3 Corner(double[][] corners, int index)
        {
            double[] c = corners[index];
            return new Vec3(c[0], c[1], c[2]);
        }

        private static void DatasmithContractFields()
        {
            var options = new ExportOptions { OutputFolder = ".", IncludeNormals = true, WeldVertices = false };

            var materials = new MaterialResolver(options);
            var scene = new SceneData
            {
                Name = "DatasmithTest",
                Materials = materials.Materials,
                SourceDocument = "site.nwd",
                SourceUnits = "Feet",
                TargetUnits = "Meters",
                YUpApplied = true,
                AppliedOffset = new Vec3(10, 20, 30)
            };

            // Node A: no real instance GUID (the common case for merge-by-material /
            // per-model-file grouping) — must synthesise. Set name is already-coded,
            // so FourDName.TryParse resolves the name.* fields.
            const string setNameA = "00840_A_L05_ARCH_FRMG_Interior framing";
            NodeBuilder nodeA = MakeTriangleNode("Level 5 framing", setNameA, options, materials);
            scene.Nodes.Add(nodeA);

            // Node B: carries a real (non-zero) Navisworks instance GUID — must be used
            // as-is, not synthesised.
            const string setNameB = "Miscellaneous Set";
            NodeBuilder nodeB = MakeTriangleNode("A real item", setNameB, options, materials);
            nodeB.Extras["navex:guid"] = "3fa85f64-5717-4562-b3fc-2c963f66afa6";
            scene.Nodes.Add(nodeB);

            // Node C: carries the all-zeroes GUID NavEx's own FBX path is known to
            // produce for every element in the real sample — must be treated as absent
            // and synthesised instead of passed through, since that all-zeroes value is
            // exactly the bug this contract replaces.
            const string setNameC = "Zero Guid Set";
            NodeBuilder nodeC = MakeTriangleNode("Zero guid item", setNameC, options, materials);
            nodeC.Extras["navex:guid"] = "00000000-0000-0000-0000-000000000000";
            scene.Nodes.Add(nodeC);

            var task = new ScheduleTask
            {
                TaskId = "T-500",
                Name = "Erect Level 5 structural steel",
                TaskType = "Construction",
                PlannedStart = new DateTime(2026, 1, 15),
                PlannedFinish = new DateTime(2026, 2, 1)
            };
            options.DatasmithTaskLinks[setNameA] = task;

            string path = "out_test.udatasmith";
            ExportResult result = new DatasmithWriter(options).Write(scene, path);
            string xml = File.ReadAllText(path);

            Check(".udatasmith file was written", File.Exists(path));
            Check("payload FBX sidecar was written", result.SidecarFiles.Count == 1 && File.Exists(result.SidecarFiles[0]));
            Check("Datasmith wrote 3 actors", result.NodeCount == 3, result.NodeCount.ToString(CultureInfo.InvariantCulture));

            // ── Contract field presence, exact names, per docs/contracts/schedule-link.md ──
            Check("pixmy.contractVersion present and = 1.0", xml.Contains("name=\"pixmy.contractVersion\" type=\"String\" val=\"1.0\""));
            Check("pixmy.sourceGuid present", xml.Contains("name=\"pixmy.sourceGuid\""));
            Check("pixmy.guidSynthetic present", xml.Contains("name=\"pixmy.guidSynthetic\""));
            Check("pixmy.set.name present for node A", xml.Contains("val=\"" + setNameA + "\""));
            Check("pixmy.task.stableKey present and matches ScheduleTask.StableKey", xml.Contains("name=\"pixmy.task.stableKey\" type=\"String\" val=\"" + task.StableKey + "\""));
            Check("pixmy.task.displayName present", xml.Contains("name=\"pixmy.task.displayName\" type=\"String\" val=\"" + task.Name + "\""));
            Check("pixmy.task.plannedStart is ISO 8601 with no offset", xml.Contains("name=\"pixmy.task.plannedStart\" type=\"String\" val=\"2026-01-15T00:00:00\""));
            Check("pixmy.task.plannedFinish is ISO 8601 with no offset", xml.Contains("name=\"pixmy.task.plannedFinish\" type=\"String\" val=\"2026-02-01T00:00:00\""));
            Check("pixmy.task.type normalises to Construct", xml.Contains("name=\"pixmy.task.type\" type=\"String\" val=\"Construct\""));
            Check("pixmy.name.sequenceCode present", xml.Contains("name=\"pixmy.name.sequenceCode\" type=\"String\" val=\"00840\""));
            Check("pixmy.name.zone present", xml.Contains("name=\"pixmy.name.zone\" type=\"String\" val=\"A\""));
            Check("pixmy.name.level present (canonical form)", xml.Contains("name=\"pixmy.name.level\" type=\"String\" val=\"L05\""));
            Check("pixmy.name.discipline present", xml.Contains("name=\"pixmy.name.discipline\" type=\"String\" val=\"ARCH\""));
            Check("pixmy.name.activity present", xml.Contains("name=\"pixmy.name.activity\" type=\"String\" val=\"FRMG\""));

            // Node B / C are unscheduled: absence of task.* is legal, not an error — only
            // node A (the one entry in DatasmithTaskLinks) should emit task fields at all.
            Check("only the scheduled node emits pixmy.task.stableKey (unscheduled nodes are legal, not errors)",
                CountOccurrences(xml, "name=\"pixmy.task.stableKey\"") == 1);

            // ── Identity rules: never all-zeroes, real GUID passed through, zero GUID replaced ──
            List<string> guids = ExtractValues(xml, "pixmy.sourceGuid");
            Check("exactly 3 pixmy.sourceGuid values emitted (one per node)", guids.Count == 3, guids.Count.ToString(CultureInfo.InvariantCulture));
            bool anyAllZero = false;
            foreach (string g in guids) if (string.Equals(g, "00000000-0000-0000-0000-000000000000", StringComparison.OrdinalIgnoreCase)) anyAllZero = true;
            Check("no emitted pixmy.sourceGuid is the all-zeroes GUID", !anyAllZero);

            var guidSet = new HashSet<string>(guids, StringComparer.OrdinalIgnoreCase);
            Check("all pixmy.sourceGuid values are unique within the export", guidSet.Count == guids.Count);

            Check("node B's real instance GUID is carried through verbatim",
                xml.Contains("val=\"3fa85f64-5717-4562-b3fc-2c963f66afa6\""));

            List<string> syntheticFlags = ExtractValues(xml, "pixmy.guidSynthetic");
            Check("3 guidSynthetic flags emitted", syntheticFlags.Count == 3, syntheticFlags.Count.ToString(CultureInfo.InvariantCulture));
            Check("exactly one node (B, the real GUID) is non-synthetic", CountEqual(syntheticFlags, "false") == 1,
                string.Join(",", syntheticFlags));
            Check("exactly two nodes (A with no GUID, C with the zero GUID) are synthetic", CountEqual(syntheticFlags, "true") == 2,
                string.Join(",", syntheticFlags));

            // ── Provenance carried verbatim from GltfWriter's convention ──
            Check("navex:sourceDocument carried verbatim", xml.Contains("name=\"navex:sourceDocument\" type=\"String\" val=\"site.nwd\""));
            Check("navex:sourceUnits carried verbatim", xml.Contains("name=\"navex:sourceUnits\" type=\"String\" val=\"Feet\""));
            Check("navex:targetUnits carried verbatim", xml.Contains("name=\"navex:targetUnits\" type=\"String\" val=\"Meters\""));
            Check("navex:upAxis carried verbatim", xml.Contains("name=\"navex:upAxis\""));
            Check("navex:originMode carried verbatim", xml.Contains("name=\"navex:originMode\""));
            Check("navex:appliedOffset carried verbatim", xml.Contains("name=\"navex:appliedOffset\" type=\"String\" val=\"10,20,30\""));
            Check("navex:offsetNote carried verbatim, exact string",
                xml.Contains("val=\"Add appliedOffset to exported coordinates to return to source world coordinates.\""));
            Check("navex:exportedUtc carried verbatim", xml.Contains("name=\"navex:exportedUtc\""));

            // Re-export of the unchanged scene must reproduce the same synthetic GUIDs
            // (stability across re-exports is the other identity-rule requirement).
            string path2 = "out_test2.udatasmith";
            new DatasmithWriter(options).Write(scene, path2);
            string xml2 = File.ReadAllText(path2);
            List<string> guids2 = ExtractValues(xml2, "pixmy.sourceGuid");
            Check("synthetic sourceGuid is stable across re-exports of an unchanged scene",
                guids.Count == guids2.Count && guids[0] == guids2[0] && guids[2] == guids2[2]);
        }

        private static NodeBuilder MakeTriangleNode(string name, string setName, ExportOptions options, MaterialResolver materials)
        {
            var node = new NodeBuilder(name, options);
            node.Extras["navex:set"] = setName;
            PrimitiveBucket bucket = node.Bucket(materials.DefaultMaterialIndex, PrimitiveMode.Triangles);
            MeshBuilder builder = bucket.Current(3);
            var n = new Vec3(0, 0, 1);
            builder.AddIndex(builder.AddVertex(new Vec3(0, 0, 0), n, 1, 1, 1, 1));
            builder.AddIndex(builder.AddVertex(new Vec3(1, 0, 0), n, 1, 1, 1, 1));
            builder.AddIndex(builder.AddVertex(new Vec3(0, 1, 0), n, 1, 1, 1, 1));
            return node;
        }

        // ── Small text-scraping helpers — deliberately simple, this is a hand-rolled
        // XML/FBX writer being checked against exact strings it is expected to emit. ──

        private static int CountOccurrences(string haystack, string needle)
        {
            int count = 0, index = 0;
            while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += needle.Length;
            }
            return count;
        }

        private static string FirstLineContaining(string text, string needle)
        {
            foreach (string line in text.Split('\n'))
                if (line.Contains(needle)) return line.Trim();
            return "(not found)";
        }

        private static List<string> ExtractValues(string xml, string propertyName)
        {
            var results = new List<string>();
            string marker = "name=\"" + propertyName + "\" type=\"String\" val=\"";
            int index = 0;
            while ((index = xml.IndexOf(marker, index, StringComparison.Ordinal)) >= 0)
            {
                int start = index + marker.Length;
                int end = xml.IndexOf('"', start);
                if (end < 0) break;
                results.Add(xml.Substring(start, end - start));
                index = end;
            }
            return results;
        }

        private static int CountEqual(List<string> values, string expected)
        {
            int count = 0;
            foreach (string v in values) if (string.Equals(v, expected, StringComparison.Ordinal)) count++;
            return count;
        }

        private static void Check(string description, bool condition) { Check(description, condition, null); }

        private static void Check(string description, bool condition, string detail)
        {
            _checks++;
            if (condition)
            {
                Console.WriteLine("  ok   " + description);
            }
            else
            {
                _failures++;
                Console.WriteLine("  FAIL " + description + (string.IsNullOrEmpty(detail) ? "" : "  [got: " + detail + "]"));
            }
        }
    }
}
