using System;
using System.Collections.Generic;
using NavEx.Core;
using NavEx.Core.Exporters;

namespace NavEx
{
    internal static class Program
    {
        // A unit cube at a known location, 12 triangles, two materials.
        private static readonly double[][] CubeCorners =
        {
            new double[]{0,0,0}, new double[]{1,0,0}, new double[]{1,1,0}, new double[]{0,1,0},
            new double[]{0,0,1}, new double[]{1,0,1}, new double[]{1,1,1}, new double[]{0,1,1}
        };

        private static readonly int[][] CubeFaces =
        {
            new[]{0,2,1}, new[]{0,3,2},   // bottom
            new[]{4,5,6}, new[]{4,6,7},   // top
            new[]{0,1,5}, new[]{0,5,4},
            new[]{1,2,6}, new[]{1,6,5},
            new[]{2,3,7}, new[]{2,7,6},
            new[]{3,0,4}, new[]{3,4,7}
        };

        private static int Main()
        {
            var options = new ExportOptions
            {
                OutputFolder = ".",
                Format = ExportFormat.Glb,
                IncludeNormals = true,
                WeldVertices = true,
                WeldTolerance = 0.0001
            };

            var materials = new MaterialResolver(options);
            int matA = materials.DefaultMaterialIndex;

            var scene = new SceneData
            {
                Name = "TestScene",
                Materials = materials.Materials,
                SourceDocument = "test.nwd",
                SourceUnits = "Feet",
                TargetUnits = "Meters",
                YUpApplied = true,
                AppliedOffset = new Vec3(1000.5, 2000.25, 0)
            };

            var node = new NodeBuilder("cube", options);
            node.Extras["navex:set"] = "TEST";
            PrimitiveBucket bucket = node.Bucket(matA, PrimitiveMode.Triangles);

            foreach (int[] face in CubeFaces)
            {
                Vec3 p1 = Corner(face[0]), p2 = Corner(face[1]), p3 = Corner(face[2]);
                Vec3 n = Vec3.Cross(p2 - p1, p3 - p1).Normalized();
                MeshBuilder builder = bucket.Current(3);
                builder.AddIndex(builder.AddVertex(p1, n, 1, 1, 1, 1));
                builder.AddIndex(builder.AddVertex(p2, n, 1, 1, 1, 1));
                builder.AddIndex(builder.AddVertex(p3, n, 1, 1, 1, 1));
            }

            scene.Nodes.Add(node);
            foreach (MeshBuilder b in bucket.Builders) scene.Bounds.Add(b.Bounds);

            ExportResult glb = new GltfWriter(options).Write(scene, "out.glb");
            Console.WriteLine("GLB   " + glb.FileSizeBytes + " bytes, " + glb.TriangleCount + " tris, " +
                              glb.VertexCount + " verts, " + glb.NodeCount + " nodes");

            options.Format = ExportFormat.GltfSeparate;
            ExportResult gltf = new GltfWriter(options).Write(scene, "out.gltf");
            Console.WriteLine("glTF  " + gltf.FileSizeBytes + " bytes");

            options.Format = ExportFormat.Obj;
            ExportResult obj = new ObjWriter(options).Write(scene, "out.obj");
            Console.WriteLine("OBJ   " + obj.FileSizeBytes + " bytes");

            // Welding check: 12 triangles over 8 distinct corners, but each corner
            // carries three different face normals, so 24 vertices is correct and
            // 36 (unwelded) or 8 (over-welded, creases lost) would both be wrong.
            int vertexCount = 0;
            foreach (MeshBuilder b in bucket.Builders) vertexCount += b.VertexCount;
            Console.WriteLine("welded vertex count: " + vertexCount + " (expected 24)");

            int failures = vertexCount == 24 ? 0 : 1;
            failures += FourDTests.Run();
            return failures == 0 ? 0 : 1;
        }

        private static Vec3 Corner(int index)
        {
            double[] c = CubeCorners[index];
            return new Vec3(c[0], c[1], c[2]);
        }
    }
}
