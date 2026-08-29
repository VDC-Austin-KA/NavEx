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
            ScheduleSidecarShape();

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

        /// <summary>
        /// The pixmy.* schedule linkage and navex:* provenance that a Datasmith export
        /// attaches to every actor.
        ///
        /// The geometry itself now goes through Epic's Datasmith Export SDK, which
        /// needs a native bridge DLL and a built SDK, so it cannot run here. What is
        /// checked is the part that used to be hand-written XML and is still ours to
        /// get right: every field name Unrealistic4D reads, and the identity rules for
        /// pixmy.sourceGuid.
        /// </summary>
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
            // per-model-file grouping) -- must synthesise. Set name is already-coded,
            // so FourDName.TryParse resolves the name.* fields.
            const string setNameA = "00840_A_L05_ARCH_FRMG_Interior framing";
            NodeBuilder nodeA = MakeTriangleNode("Level 5 framing", setNameA, options, materials);
            scene.Nodes.Add(nodeA);

            // Node B: carries a real (non-zero) Navisworks instance GUID -- used as-is.
            const string setNameB = "Miscellaneous Set";
            NodeBuilder nodeB = MakeTriangleNode("A real item", setNameB, options, materials);
            nodeB.Extras["navex:guid"] = "3fa85f64-5717-4562-b3fc-2c963f66afa6";
            scene.Nodes.Add(nodeB);

            // Node C: the all-zeroes GUID NavEx's own FBX path produces for every
            // element in the real sample data -- must be treated as absent and
            // synthesised, since that value is the bug this contract replaces.
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

            var writer = new DatasmithWriter(options);
            List<KeyValuePair<string, string>> metaA = writer.BuildNodeMetaData(nodeA, 0, scene);
            List<KeyValuePair<string, string>> metaB = writer.BuildNodeMetaData(nodeB, 1, scene);
            List<KeyValuePair<string, string>> metaC = writer.BuildNodeMetaData(nodeC, 2, scene);

            // -- Contract field presence, exact names --
            Check("pixmy.contractVersion present and = 1.0", Value(metaA, "pixmy.contractVersion") == "1.0");
            Check("pixmy.sourceGuid present", Has(metaA, "pixmy.sourceGuid"));
            Check("pixmy.guidSynthetic present", Has(metaA, "pixmy.guidSynthetic"));
            Check("pixmy.set.name is the selection set", Value(metaA, "pixmy.set.name") == setNameA);
            Check("pixmy.task.stableKey matches ScheduleTask.StableKey",
                Value(metaA, "pixmy.task.stableKey") == task.StableKey, Value(metaA, "pixmy.task.stableKey"));
            Check("pixmy.task.displayName present", Value(metaA, "pixmy.task.displayName") == task.Name);
            Check("pixmy.task.plannedStart is ISO 8601 with no offset",
                Value(metaA, "pixmy.task.plannedStart") == "2026-01-15T00:00:00");
            Check("pixmy.task.plannedFinish is ISO 8601 with no offset",
                Value(metaA, "pixmy.task.plannedFinish") == "2026-02-01T00:00:00");
            Check("pixmy.task.type normalises to Construct", Value(metaA, "pixmy.task.type") == "Construct");
            Check("pixmy.name.sequenceCode present", Value(metaA, "pixmy.name.sequenceCode") == "00840");
            Check("pixmy.name.zone present", Value(metaA, "pixmy.name.zone") == "A");
            Check("pixmy.name.level present (canonical form)", Value(metaA, "pixmy.name.level") == "L05");
            Check("pixmy.name.discipline present", Value(metaA, "pixmy.name.discipline") == "ARCH");
            Check("pixmy.name.activity present", Value(metaA, "pixmy.name.activity") == "FRMG");

            // Unscheduled nodes carry no task fields at all -- absence is legal here,
            // not an error.
            Check("an unscheduled node emits no pixmy.task.stableKey", !Has(metaB, "pixmy.task.stableKey"));
            Check("an unscheduled node still carries pixmy.set.name",
                Value(metaB, "pixmy.set.name") == setNameB);

            // -- Identity rules --
            Check("node A (no GUID) is synthesised", Value(metaA, "pixmy.guidSynthetic") == "true");
            Check("node B's real instance GUID is carried through verbatim",
                Value(metaB, "pixmy.sourceGuid") == "3fa85f64-5717-4562-b3fc-2c963f66afa6");
            Check("node B is not marked synthetic", Value(metaB, "pixmy.guidSynthetic") == "false");
            Check("node C's all-zeroes GUID is replaced, not passed through",
                Value(metaC, "pixmy.sourceGuid") != "00000000-0000-0000-0000-000000000000");
            Check("node C is marked synthetic", Value(metaC, "pixmy.guidSynthetic") == "true");

            var guids = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                Value(metaA, "pixmy.sourceGuid"),
                Value(metaB, "pixmy.sourceGuid"),
                Value(metaC, "pixmy.sourceGuid")
            };
            Check("all pixmy.sourceGuid values are unique within the export", guids.Count == 3);

            // Stability across re-exports of an unchanged scene is the other half of
            // the identity rule.
            Check("synthetic sourceGuid is stable across re-exports of an unchanged scene",
                writer.BuildNodeMetaData(nodeA, 0, scene).Find(kv => kv.Key == "pixmy.sourceGuid").Value
                    == Value(metaA, "pixmy.sourceGuid"));

            // -- Provenance, carried verbatim from GltfWriter's convention --
            Check("navex:sourceDocument carried verbatim", Value(metaA, "navex:sourceDocument") == "site.nwd");
            Check("navex:sourceUnits carried verbatim", Value(metaA, "navex:sourceUnits") == "Feet");
            Check("navex:targetUnits carried verbatim", Value(metaA, "navex:targetUnits") == "Meters");
            Check("navex:upAxis carried verbatim", Has(metaA, "navex:upAxis"));
            Check("navex:originMode carried verbatim", Has(metaA, "navex:originMode"));
            Check("navex:appliedOffset carried verbatim", Value(metaA, "navex:appliedOffset") == "10,20,30");
            Check("navex:offsetNote carried verbatim, exact string",
                Value(metaA, "navex:offsetNote")
                    == "Add appliedOffset to exported coordinates to return to source world coordinates.");
            Check("navex:exportedUtc carried verbatim", Has(metaA, "navex:exportedUtc"));

            // -- Element naming: what Unreal names the imported StaticMesh asset. It
            // has to come from the selection set, not the node, or every set in a
            // file-per-set export lands on one asset name. --
            Check("StaticMesh element is named after the selection set",
                DatasmithWriter.ElementName(nodeA, 0) == "00840_A_L05_ARCH_FRMG_Interior_framing",
                DatasmithWriter.ElementName(nodeA, 0));
            Check("a second node in the same scene gets a unique element name",
                DatasmithWriter.ElementName(nodeB, 1) == "Miscellaneous_Set_1",
                DatasmithWriter.ElementName(nodeB, 1));
        }

        private static string Value(List<KeyValuePair<string, string>> meta, string key)
        {
            foreach (KeyValuePair<string, string> kv in meta)
                if (kv.Key == key) return kv.Value;
            return null;
        }

        private static bool Has(List<KeyValuePair<string, string>> meta, string key)
        {
            return Value(meta, key) != null;
        }

        /// <summary>
        /// The schedule sidecar a Datasmith export drops beside the .udatasmith files.
        /// Unrealistic4D reads scheduleStart / tasks[id,name,plannedStart,plannedEnd] /
        /// elements[taskId,name] literally, so those spellings are the contract; the
        /// rest of the document is additive.
        /// </summary>
        private static void ScheduleSidecarShape()
        {
            var options = new ExportOptions { Format = ExportFormat.Datasmith };

            var foundations = new ScheduleTask
            {
                TaskId = "A1000",
                Name = "Level 1 foundations",
                TaskType = "Construction",
                PlannedStart = new DateTime(2026, 3, 2),
                PlannedFinish = new DateTime(2026, 3, 9)
            };
            var columns = new ScheduleTask
            {
                TaskId = "A1010",
                Name = "Level 1 columns",
                TaskType = "Demolition",
                PlannedStart = new DateTime(2026, 3, 10),
                PlannedFinish = new DateTime(2026, 3, 20)
            };
            options.DatasmithTaskLinks["02120_L01_STRC_FOUN"] = foundations;
            options.DatasmithTaskLinks["02130_L01_STRC_COLS"] = columns;

            var results = new List<ExportResult>
            {
                new ExportResult { SetName = "02120_L01_STRC_FOUN", FilePath = "out/doc 02120_L01_STRC_FOUN.udatasmith" },
                new ExportResult { SetName = "02130_L01_STRC_COLS", FilePath = "out/doc 02130_L01_STRC_COLS.udatasmith" },
                new ExportResult { SetName = "Unscheduled Set", FilePath = "out/doc Unscheduled Set.udatasmith" },
                new ExportResult { SetName = "02120_L01_STRC_FOUN", FilePath = "out/doc failed.udatasmith",
                                   Failed = true, FailureReason = "no geometry found" }
            };

            string json = ScheduleJsonWriter.Build(options, "TEST.nwf", results);
            Check("a schedule sidecar is produced when sets are matched to tasks", json != null);
            if (json == null) return;

            // -- The names Unrealistic4D looks up literally --
            Check("scheduleStart is the earliest planned start, date-only",
                json.Contains("\"scheduleStart\":\"2026-03-02\""), json);
            Check("tasks[].id is the ScheduleTask stable key",
                json.Contains("\"id\":\"" + foundations.StableKey + "\""));
            Check("tasks[].name is the schedule task name",
                json.Contains("\"name\":\"Level 1 foundations\""));
            Check("tasks[].plannedStart is ISO 8601 with no offset",
                json.Contains("\"plannedStart\":\"2026-03-02T00:00:00\""));
            Check("tasks[].plannedEnd is ISO 8601 with no offset",
                json.Contains("\"plannedEnd\":\"2026-03-09T00:00:00\""));
            Check("elements[].taskId points back at a task id",
                json.Contains("\"taskId\":\"" + foundations.StableKey + "\""));

            // -- The join keys: the asset name for the stem path, pixmy.set.name for
            // the Datasmith-folder path. Same string, both spellings, one file. --
            Check("elements[].name is the selection set (= the imported asset name)",
                json.Contains("\"name\":\"02120_L01_STRC_FOUN\",\"set\":\"02120_L01_STRC_FOUN\""));
            Check("the metadata key to link on is stated in the document",
                json.Contains("\"joinKey\":\"pixmy.set.name\""));
            Check("elements[].file names the .udatasmith the set was written to",
                json.Contains("\"file\":\"doc 02120_L01_STRC_FOUN.udatasmith\""));

            // -- Coded set names carry their parsed identity through --
            Check("elements carry the parsed discipline", json.Contains("\"discipline\":\"STRC\""));
            Check("elements carry the parsed activity", json.Contains("\"activity\":\"FOUN\""));
            Check("elements carry the parsed level", json.Contains("\"level\":\"L01\""));

            // -- What must NOT be in there --
            Check("a set with no matched task is left out", !json.Contains("Unscheduled Set"));
            Check("a failed export does not emit a second element for its set",
                CountOccurrences(json, "\"taskId\":\"" + foundations.StableKey + "\"") == 1);
            Check("each task is emitted once", CountOccurrences(json, "\"durationDays\"") == 2);
            Check("scheduleEnd is the latest planned finish",
                json.Contains("\"scheduleEnd\":\"2026-03-20\""));
            Check("task type is normalised to what TimeLiner understands",
                json.Contains("\"type\":\"Demolish\""));

            // Nothing matched means nothing to say - an empty tasks array would only
            // be refused on the Unreal side, so no file is written at all.
            Check("no sidecar when the 4D tab has matched nothing",
                ScheduleJsonWriter.Build(new ExportOptions(), "TEST.nwf", results) == null);
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
