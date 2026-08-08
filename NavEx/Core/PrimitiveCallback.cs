using System;
using ComApi = Autodesk.Navisworks.Api.Interop.ComApi;

namespace NavEx.Core
{
    /// <summary>
    /// The COM callback Navisworks drives while tessellating a fragment. One
    /// instance is reused for the whole export; <see cref="BeginFragment"/> swaps in
    /// the per-fragment transform and the destination buckets, so a model with a
    /// million fragments doesn't allocate a million callbacks.
    ///
    /// Vertices arrive in the fragment's local space. The pipeline applied here is:
    ///
    ///     local --(fragment local-to-world)--> world (document units)
    ///           --(subtract origin offset)--> recentred
    ///           --(unit scale)--> target units
    ///           --(Z-up to Y-up)--> glTF space
    ///
    /// All of it stays in double precision; only the final store into the mesh
    /// buffers narrows to float32, by which point the coordinates are small.
    /// </summary>
    internal class PrimitiveCallback : ComApi.InwSimplePrimitivesCB
    {
        private readonly ExportOptions _options;

        private Matrix4 _localToWorld = Matrix4.Identity();
        private double[] _normalMatrix = new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 };
        private bool _flipWinding;
        private PrimitiveBucket _triangles;
        private PrimitiveBucket _lines;

        private readonly Vec3 _offset;
        private readonly double _scale;
        private readonly bool _yUp;
        private readonly double _minEdge;
        private readonly bool _wantNormals;
        private readonly bool _wantColors;

        public long TrianglesAdded;
        public long TrianglesSkipped;
        public long LinesAdded;

        public PrimitiveCallback(ExportOptions options, Vec3 offsetInDocumentUnits, double unitScale, bool yUp)
        {
            _options = options;
            _offset = offsetInDocumentUnits;
            _scale = unitScale;
            _yUp = yUp;
            _minEdge = options.MinTriangleEdge;
            _wantNormals = options.IncludeNormals;
            _wantColors = options.IncludeVertexColors;
        }

        public void BeginFragment(Matrix4 localToWorld, PrimitiveBucket triangles, PrimitiveBucket lines)
        {
            _localToWorld = localToWorld ?? Matrix4.Identity();
            _normalMatrix = _localToWorld.NormalMatrix();
            // A mirroring transform reverses triangle winding in world space; glTF
            // wants counter-clockwise front faces, so undo it here rather than
            // shipping inside-out normals.
            _flipWinding = _localToWorld.Linear3x3Determinant() < 0.0;
            _triangles = triangles;
            _lines = lines;
        }

        public void Triangle(ComApi.InwSimpleVertex v1, ComApi.InwSimpleVertex v2, ComApi.InwSimpleVertex v3)
        {
            if (v1 == null || v2 == null || v3 == null || _triangles == null) return;

            try
            {
                if (_flipWinding)
                {
                    ComApi.InwSimpleVertex swap = v2;
                    v2 = v3;
                    v3 = swap;
                }

                Vec3 p1 = ToTargetSpace(ReadCoord(v1));
                Vec3 p2 = ToTargetSpace(ReadCoord(v2));
                Vec3 p3 = ToTargetSpace(ReadCoord(v3));

                if (IsDegenerate(p1, p2, p3))
                {
                    TrianglesSkipped++;
                    return;
                }

                Vec3 faceNormal = Vec3.Cross(p2 - p1, p3 - p1).Normalized();

                Vec3 n1 = ResolveNormal(v1, faceNormal);
                Vec3 n2 = ResolveNormal(v2, faceNormal);
                Vec3 n3 = ResolveNormal(v3, faceNormal);

                MeshBuilder builder = _triangles.Current(3);
                float r1 = 1, g1 = 1, b1 = 1, a1 = 1;
                float r2 = 1, g2 = 1, b2 = 1, a2 = 1;
                float r3 = 1, g3 = 1, b3 = 1, a3 = 1;
                if (_wantColors)
                {
                    ReadColor(v1, out r1, out g1, out b1, out a1);
                    ReadColor(v2, out r2, out g2, out b2, out a2);
                    ReadColor(v3, out r3, out g3, out b3, out a3);
                }

                builder.AddIndex(builder.AddVertex(p1, n1, r1, g1, b1, a1));
                builder.AddIndex(builder.AddVertex(p2, n2, r2, g2, b2, a2));
                builder.AddIndex(builder.AddVertex(p3, n3, r3, g3, b3, a3));
                TrianglesAdded++;
            }
            catch (Exception)
            {
                // A single malformed primitive must never abort a whole export.
                TrianglesSkipped++;
            }
        }

        public void Line(ComApi.InwSimpleVertex v1, ComApi.InwSimpleVertex v2)
        {
            if (!_options.IncludeLines || _lines == null || v1 == null || v2 == null) return;

            try
            {
                Vec3 p1 = ToTargetSpace(ReadCoord(v1));
                Vec3 p2 = ToTargetSpace(ReadCoord(v2));
                if ((p2 - p1).Length < 1e-9) return;

                MeshBuilder builder = _lines.Current(2);
                Vec3 zero = new Vec3(0, 0, 0);
                builder.AddIndex(builder.AddVertex(p1, zero, 1, 1, 1, 1));
                builder.AddIndex(builder.AddVertex(p2, zero, 1, 1, 1, 1));
                LinesAdded++;
            }
            catch (Exception) { }
        }

        public void Point(ComApi.InwSimpleVertex v1)
        {
            // Point clouds are out of scope for a lightweight surface export.
        }

        public void SnapPoint(ComApi.InwSimpleVertex v1)
        {
            // Snap points are navigation aids, not geometry.
        }

        private bool IsDegenerate(Vec3 p1, Vec3 p2, Vec3 p3)
        {
            Vec3 e1 = p2 - p1;
            Vec3 e2 = p3 - p1;
            Vec3 cross = Vec3.Cross(e1, e2);
            if (cross.Length < 1e-18) return true;

            if (_minEdge > 0.0)
            {
                double longest = Math.Max((p2 - p1).Length, Math.Max((p3 - p2).Length, (p1 - p3).Length));
                if (longest < _minEdge) return true;
            }

            return false;
        }

        private Vec3 ResolveNormal(ComApi.InwSimpleVertex vertex, Vec3 faceNormal)
        {
            if (!_wantNormals) return faceNormal;

            Vec3 raw;
            if (!TryReadNormal(vertex, out raw)) return faceNormal;

            Vec3 transformed = Matrix4.ApplyNormalMatrix(_normalMatrix, raw.X, raw.Y, raw.Z);
            if (_yUp) transformed = new Vec3(transformed.X, transformed.Z, -transformed.Y);

            Vec3 normalized = transformed.Normalized();
            return normalized.Length < 0.5 ? faceNormal : normalized;
        }

        private Vec3 ToTargetSpace(Vec3 local)
        {
            Vec3 world = _localToWorld.TransformPoint(local.X, local.Y, local.Z);
            Vec3 recentred = world - _offset;
            Vec3 scaled = recentred * _scale;
            return _yUp ? new Vec3(scaled.X, scaled.Z, -scaled.Y) : scaled;
        }

        // Navisworks hands these back as COM SAFEARRAYs, which are 1-based rather
        // than 0-based. Reading from GetLowerBound instead of hard-coding index 1
        // keeps the readers correct either way.
        private static Vec3 ReadCoord(ComApi.InwSimpleVertex vertex)
        {
            Array coord = (Array)vertex.coord;
            int lb = coord.GetLowerBound(0);
            return new Vec3(
                Convert.ToDouble(coord.GetValue(lb)),
                Convert.ToDouble(coord.GetValue(lb + 1)),
                Convert.ToDouble(coord.GetValue(lb + 2)));
        }

        private static bool TryReadNormal(ComApi.InwSimpleVertex vertex, out Vec3 normal)
        {
            normal = new Vec3(0, 0, 0);
            try
            {
                Array values = vertex.normal as Array;
                if (values == null || values.Length < 3) return false;
                int lb = values.GetLowerBound(0);
                normal = new Vec3(
                    Convert.ToDouble(values.GetValue(lb)),
                    Convert.ToDouble(values.GetValue(lb + 1)),
                    Convert.ToDouble(values.GetValue(lb + 2)));
                return normal.Length > 1e-9;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static void ReadColor(ComApi.InwSimpleVertex vertex, out float r, out float g, out float b, out float a)
        {
            r = g = b = a = 1f;
            try
            {
                Array values = vertex.color as Array;
                if (values == null || values.Length < 3) return;
                int lb = values.GetLowerBound(0);
                r = (float)Convert.ToDouble(values.GetValue(lb));
                g = (float)Convert.ToDouble(values.GetValue(lb + 1));
                b = (float)Convert.ToDouble(values.GetValue(lb + 2));
                if (values.Length >= 4) a = (float)Convert.ToDouble(values.GetValue(lb + 3));
            }
            catch (Exception) { }
        }
    }
}
