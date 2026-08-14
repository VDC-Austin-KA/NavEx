using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using NavEx.Core;
using NavEx.Core.Exporters;

namespace NavEx
{
    /// <summary>
    /// Structural checks for the hand-rolled FBX writer (Task N2). No Navisworks
    /// types, no FBX SDK — same offline-testable shape as the GLB/OBJ checks in
    /// Program.cs. This cannot confirm a real DCC import succeeds; it confirms the
    /// file is structurally sound (one geometry/model pair per node, correct
    /// polygon/vertex counts).
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
            Console.WriteLine("Writer format tests (FBX)");
            Console.WriteLine("--------------------------------------");

            FbxStructuralInvariants();

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

        // ── Small text-scraping helpers — deliberately simple, this is a hand-rolled
        // FBX writer being checked against exact strings it is expected to emit. ──

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
