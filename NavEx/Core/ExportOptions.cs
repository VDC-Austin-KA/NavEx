using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Autodesk.Navisworks.Api;

namespace NavEx.Core
{
    public enum ExportFormat
    {
        /// <summary>Single binary .glb — geometry, materials and metadata in one file.</summary>
        Glb,
        /// <summary>.gltf JSON plus a sibling .bin buffer.</summary>
        GltfSeparate,
        /// <summary>.gltf JSON with the buffer inlined as a base64 data URI.</summary>
        GltfEmbedded,
        /// <summary>Wavefront .obj plus .mtl.</summary>
        Obj,
        /// <summary>FBX ASCII 7.x, hand-written — geometry only.</summary>
        Fbx
    }

    /// <summary>How extracted triangles get grouped into glTF nodes.</summary>
    public enum GroupingMode
    {
        /// <summary>One node, one mesh, one primitive per material. Smallest file, fewest draw calls.</summary>
        MergeByMaterial,
        /// <summary>One node per source model file (per loaded .rvt/.nwc/…).</summary>
        PerModelFile,
        /// <summary>One node per Navisworks item — keeps names and per-object selectability.</summary>
        PerItem
    }

    /// <summary>Where the exported model's origin lands.</summary>
    public enum OriginMode
    {
        /// <summary>Keep Navisworks world coordinates. Accurate but float32-hostile on survey grids.</summary>
        WorldOrigin,
        /// <summary>Shift the centre of the exported geometry's bounding box to (0,0,0).</summary>
        SelectionCenter,
        /// <summary>Shift the XY centre to (0,0) but keep the true elevation.</summary>
        SelectionCenterKeepElevation,
        /// <summary>Shift the whole loaded model's bounding-box centre — keeps separately exported sets aligned.</summary>
        ModelCenter,
        /// <summary>Subtract an explicit point, entered in the document's units.</summary>
        Custom
    }

    /// <summary>How multiple selected sets turn into files.</summary>
    public enum BatchMode
    {
        /// <summary>One output file per selected set.</summary>
        FilePerSet,
        /// <summary>Everything in one file, each set becoming its own node.</summary>
        SingleCombinedFile
    }

    public class ExportOptions
    {
        // ── Output ────────────────────────────────────────────────────────────
        public string OutputFolder = "";
        public ExportFormat Format = ExportFormat.Glb;
        public BatchMode Batch = BatchMode.FilePerSet;
        /// <summary>Tokens: {set} {doc} {date} {time} {count}.</summary>
        public string FileNameTemplate = "{set}";
        public bool OverwriteExisting = true;

        // ── Geometry ──────────────────────────────────────────────────────────
        public Units TargetUnits = Units.Meters;
        public GroupingMode Grouping = GroupingMode.MergeByMaterial;
        public OriginMode Origin = OriginMode.SelectionCenter;
        public Vec3 CustomOrigin = new Vec3(0, 0, 0);
        /// <summary>Rotate Z-up Navisworks coordinates into glTF's Y-up convention.</summary>
        public bool ConvertToYUp = true;
        public bool IncludeNormals = true;
        public bool IncludeVertexColors = false;
        /// <summary>Merge coincident vertices. Roughly halves file size on typical BIM geometry.</summary>
        public bool WeldVertices = true;
        /// <summary>Weld tolerance in target units. 0.1 mm by default.</summary>
        public double WeldTolerance = 0.0001;
        /// <summary>Drop triangles whose longest edge is below this (degenerate slivers). Target units.</summary>
        public double MinTriangleEdge = 0.0;
        /// <summary>Split a primitive once it exceeds this many vertices. 0 disables splitting.</summary>
        public int MaxVerticesPerPrimitive = 2000000;
        /// <summary>Skip items hidden in the Navisworks scene.</summary>
        public bool SkipHidden = true;
        /// <summary>Also emit line primitives (2D linework, wireframe-only geometry).</summary>
        public bool IncludeLines = false;

        // ── Materials ─────────────────────────────────────────────────────────
        public bool EmitMaterials = true;
        /// <summary>Treat items with transparency as glTF BLEND, otherwise force opaque.</summary>
        public bool PreserveTransparency = true;
        public bool DoubleSided = true;
        /// <summary>Default roughness when the source exposes no shininess.</summary>
        public double DefaultRoughness = 0.85;
        public double DefaultMetallic = 0.0;
        /// <summary>Try the COM appearance (specular/shininess) before falling back to the resolved item colour.</summary>
        public bool UseComMaterials = false;

        // ── Metadata ──────────────────────────────────────────────────────────
        /// <summary>Write per-node Navisworks identity into glTF `extras`.</summary>
        public bool EmbedItemExtras = true;
        /// <summary>Write a sidecar .properties.json with the full property set per item.</summary>
        public bool ExportPropertiesSidecar = false;
        /// <summary>Property categories to include in the sidecar; empty means all.</summary>
        public List<string> PropertyCategoryFilter = new List<string>();

        // ── Engine ────────────────────────────────────────────────────────────
        /// <summary>Items per COM selection round-trip. Larger is faster but less responsive.</summary>
        public int ComBatchSize = 500;

        public string Describe()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Format:        " + Format);
            sb.AppendLine("Batch:         " + Batch);
            sb.AppendLine("Units:         " + TargetUnits);
            sb.AppendLine("Grouping:      " + Grouping);
            sb.AppendLine("Origin:        " + Origin + (Origin == OriginMode.Custom ? " " + CustomOrigin : ""));
            sb.AppendLine("Y-up:          " + ConvertToYUp);
            sb.AppendLine("Weld:          " + (WeldVertices ? WeldTolerance.ToString("0.######", CultureInfo.InvariantCulture) : "off"));
            sb.AppendLine("Normals:       " + IncludeNormals);
            sb.AppendLine("Vertex colors: " + IncludeVertexColors);
            sb.AppendLine("Materials:     " + EmitMaterials);
            return sb.ToString();
        }

        public string BuildFileName(string setName, int index, string documentTitle)
        {
            string name = string.IsNullOrEmpty(FileNameTemplate) ? "{set}" : FileNameTemplate;
            DateTime now = DateTime.Now;
            name = name.Replace("{set}", setName ?? "export");
            name = name.Replace("{doc}", string.IsNullOrEmpty(documentTitle) ? "model" : documentTitle);
            name = name.Replace("{date}", now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            name = name.Replace("{time}", now.ToString("HHmmss", CultureInfo.InvariantCulture));
            name = name.Replace("{count}", index.ToString(CultureInfo.InvariantCulture));
            return SanitizeFileName(name);
        }

        public static string SanitizeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "export";
            var sb = new StringBuilder(name.Length);
            char[] invalid = Path.GetInvalidFileNameChars();
            foreach (char c in name)
                sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
            string cleaned = sb.ToString().Trim().TrimEnd('.');
            return cleaned.Length == 0 ? "export" : cleaned;
        }

        public string ExtensionForFormat()
        {
            switch (Format)
            {
                case ExportFormat.Glb: return ".glb";
                case ExportFormat.Obj: return ".obj";
                case ExportFormat.Fbx: return ".fbx";
                default: return ".gltf";
            }
        }
    }
}
