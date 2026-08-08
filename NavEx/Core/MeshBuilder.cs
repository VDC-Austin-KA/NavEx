using System;
using System.Collections.Generic;

namespace NavEx.Core
{
    /// <summary>
    /// Accumulates triangles (and optionally lines) into flat float32 buffers ready
    /// for a glTF accessor, welding coincident vertices as it goes.
    ///
    /// Welding is the single biggest lever on output size for BIM geometry: the
    /// tessellator emits every triangle as three independent vertices, so a raw
    /// export carries 3 vertices per triangle where a welded one carries closer to
    /// 0.5–1. Positions are snapped to a grid of <see cref="_tolerance"/> for the
    /// hash key only — the stored coordinate is the first one seen, never the
    /// rounded value, so welding never moves geometry.
    /// </summary>
    internal class MeshBuilder
    {
        private readonly bool _weld;
        private readonly double _tolerance;
        private readonly bool _hasNormals;
        private readonly bool _hasColors;

        private readonly List<float> _positions = new List<float>();
        private readonly List<float> _normals = new List<float>();
        private readonly List<float> _colors = new List<float>();
        private readonly List<uint> _indices = new List<uint>();
        private readonly Dictionary<VertexKey, uint> _lookup;

        public readonly Bounds Bounds = new Bounds();

        public MeshBuilder(bool weld, double tolerance, bool hasNormals, bool hasColors)
        {
            _weld = weld && tolerance > 0.0;
            _tolerance = tolerance > 0.0 ? tolerance : 1e-6;
            _hasNormals = hasNormals;
            _hasColors = hasColors;
            _lookup = _weld ? new Dictionary<VertexKey, uint>(1024) : null;
        }

        public int VertexCount { get { return _positions.Count / 3; } }
        public int IndexCount { get { return _indices.Count; } }
        public bool IsEmpty { get { return _indices.Count == 0; } }
        public bool HasNormals { get { return _hasNormals; } }
        public bool HasColors { get { return _hasColors; } }

        public IList<float> Positions { get { return _positions; } }
        public IList<float> Normals { get { return _normals; } }
        public IList<float> Colors { get { return _colors; } }
        public IList<uint> Indices { get { return _indices; } }

        public uint AddVertex(Vec3 position, Vec3 normal, float r, float g, float b, float a)
        {
            if (_weld)
            {
                var key = new VertexKey(position, _hasNormals ? normal : new Vec3(0, 0, 0), _tolerance, _hasNormals);
                uint existing;
                if (_lookup.TryGetValue(key, out existing))
                    return existing;

                uint created = Append(position, normal, r, g, b, a);
                _lookup[key] = created;
                return created;
            }

            return Append(position, normal, r, g, b, a);
        }

        private uint Append(Vec3 position, Vec3 normal, float r, float g, float b, float a)
        {
            _positions.Add((float)position.X);
            _positions.Add((float)position.Y);
            _positions.Add((float)position.Z);
            Bounds.Add(position);

            if (_hasNormals)
            {
                _normals.Add((float)normal.X);
                _normals.Add((float)normal.Y);
                _normals.Add((float)normal.Z);
            }

            if (_hasColors)
            {
                _colors.Add(r);
                _colors.Add(g);
                _colors.Add(b);
                _colors.Add(a);
            }

            return (uint)(_positions.Count / 3 - 1);
        }

        public void AddIndex(uint index) { _indices.Add(index); }

        /// <summary>
        /// Position + normal hash key. Two vertices only merge when both the snapped
        /// position and (when normals are exported) the snapped normal agree, so
        /// hard edges keep their creases instead of being smoothed away.
        /// </summary>
        private struct VertexKey : IEquatable<VertexKey>
        {
            private readonly long _x, _y, _z;
            private readonly int _nx, _ny, _nz;

            public VertexKey(Vec3 p, Vec3 n, double tolerance, bool useNormal)
            {
                double inv = 1.0 / tolerance;
                _x = (long)Math.Round(p.X * inv);
                _y = (long)Math.Round(p.Y * inv);
                _z = (long)Math.Round(p.Z * inv);
                if (useNormal)
                {
                    // ~1 degree buckets is plenty to separate creases without
                    // splitting vertices that only differ by tessellation noise.
                    _nx = (int)Math.Round(n.X * 64.0);
                    _ny = (int)Math.Round(n.Y * 64.0);
                    _nz = (int)Math.Round(n.Z * 64.0);
                }
                else
                {
                    _nx = _ny = _nz = 0;
                }
            }

            public bool Equals(VertexKey other)
            {
                return _x == other._x && _y == other._y && _z == other._z
                    && _nx == other._nx && _ny == other._ny && _nz == other._nz;
            }

            public override bool Equals(object obj)
            {
                return obj is VertexKey && Equals((VertexKey)obj);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = 17;
                    hash = hash * 31 + _x.GetHashCode();
                    hash = hash * 31 + _y.GetHashCode();
                    hash = hash * 31 + _z.GetHashCode();
                    hash = hash * 31 + _nx;
                    hash = hash * 31 + _ny;
                    hash = hash * 31 + _nz;
                    return hash;
                }
            }
        }
    }

    /// <summary>glTF primitive modes we emit.</summary>
    public enum PrimitiveMode { Triangles = 4, Lines = 1 }

    /// <summary>
    /// One glTF primitive under construction: a material plus its buffers. A bucket
    /// rolls over into a fresh <see cref="MeshBuilder"/> once
    /// <see cref="ExportOptions.MaxVerticesPerPrimitive"/> is hit, so a
    /// merge-everything export can't produce an unloadable single primitive.
    /// </summary>
    internal class PrimitiveBucket
    {
        public readonly int MaterialIndex;
        public readonly PrimitiveMode Mode;
        public readonly List<MeshBuilder> Builders = new List<MeshBuilder>();

        private readonly ExportOptions _options;

        public PrimitiveBucket(int materialIndex, PrimitiveMode mode, ExportOptions options)
        {
            MaterialIndex = materialIndex;
            Mode = mode;
            _options = options;
            Builders.Add(NewBuilder());
        }

        private MeshBuilder NewBuilder()
        {
            return new MeshBuilder(
                _options.WeldVertices,
                _options.WeldTolerance,
                _options.IncludeNormals && Mode == PrimitiveMode.Triangles,
                _options.IncludeVertexColors);
        }

        /// <summary>
        /// The builder new geometry should go into. Rolls over before a primitive
        /// grows past the configured cap; the caller is expected to add a whole
        /// triangle (or line) into whatever this returns, so a rollover never
        /// splits a face across two buffers.
        /// </summary>
        public MeshBuilder Current(int incomingVertices)
        {
            MeshBuilder current = Builders[Builders.Count - 1];
            if (_options.MaxVerticesPerPrimitive > 0 &&
                current.VertexCount + incomingVertices > _options.MaxVerticesPerPrimitive &&
                current.VertexCount > 0)
            {
                current = NewBuilder();
                Builders.Add(current);
            }
            return current;
        }

        public int TotalVertices
        {
            get
            {
                int total = 0;
                foreach (MeshBuilder b in Builders) total += b.VertexCount;
                return total;
            }
        }

        public int TotalIndices
        {
            get
            {
                int total = 0;
                foreach (MeshBuilder b in Builders) total += b.IndexCount;
                return total;
            }
        }
    }

    /// <summary>
    /// A glTF node under construction: a name, some identity metadata, and the
    /// primitives that hang off it keyed by (material, mode).
    /// </summary>
    internal class NodeBuilder
    {
        public string Name;
        public readonly Dictionary<string, string> Extras = new Dictionary<string, string>(StringComparer.Ordinal);
        public readonly Dictionary<long, PrimitiveBucket> Buckets = new Dictionary<long, PrimitiveBucket>();

        private readonly ExportOptions _options;

        public NodeBuilder(string name, ExportOptions options)
        {
            Name = name;
            _options = options;
        }

        public PrimitiveBucket Bucket(int materialIndex, PrimitiveMode mode)
        {
            long key = ((long)materialIndex << 8) | (long)mode;
            PrimitiveBucket bucket;
            if (!Buckets.TryGetValue(key, out bucket))
            {
                bucket = new PrimitiveBucket(materialIndex, mode, _options);
                Buckets[key] = bucket;
            }
            return bucket;
        }

        public bool IsEmpty
        {
            get
            {
                foreach (PrimitiveBucket bucket in Buckets.Values)
                    if (bucket.TotalIndices > 0) return false;
                return true;
            }
        }

        public int TriangleCount
        {
            get
            {
                int total = 0;
                foreach (PrimitiveBucket bucket in Buckets.Values)
                    if (bucket.Mode == PrimitiveMode.Triangles) total += bucket.TotalIndices / 3;
                return total;
            }
        }

        public int VertexCount
        {
            get
            {
                int total = 0;
                foreach (PrimitiveBucket bucket in Buckets.Values) total += bucket.TotalVertices;
                return total;
            }
        }
    }

    /// <summary>Everything one export run produced, ready to hand to a writer.</summary>
    internal class SceneData
    {
        public string Name = "NavEx Export";
        public readonly List<NodeBuilder> Nodes = new List<NodeBuilder>();
        public IList<MaterialDef> Materials;
        public readonly Bounds Bounds = new Bounds();

        /// <summary>World-space offset that was subtracted, in target units, for georeferencing back.</summary>
        public Vec3 AppliedOffset;
        public string SourceDocument = "";
        public string SourceUnits = "";
        public string TargetUnits = "";
        public bool YUpApplied;

        public int TriangleCount
        {
            get
            {
                int total = 0;
                foreach (NodeBuilder node in Nodes) total += node.TriangleCount;
                return total;
            }
        }

        public int VertexCount
        {
            get
            {
                int total = 0;
                foreach (NodeBuilder node in Nodes) total += node.VertexCount;
                return total;
            }
        }

        public IEnumerable<NodeBuilder> NonEmptyNodes
        {
            get
            {
                foreach (NodeBuilder node in Nodes)
                    if (!node.IsEmpty) yield return node;
            }
        }
    }
}
