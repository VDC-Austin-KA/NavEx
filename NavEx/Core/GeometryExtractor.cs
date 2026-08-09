using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Navisworks.Api;
using ComApi = Autodesk.Navisworks.Api.Interop.ComApi;
using ComBridge = Autodesk.Navisworks.Api.ComApi.ComApiBridge;

namespace NavEx.Core
{
    /// <summary>One named chunk of geometry going into a scene (a search set, or the current selection).</summary>
    public class ExportPart
    {
        public string Name;
        public ModelItemCollection Items;

        public ExportPart(string name, ModelItemCollection items)
        {
            Name = name;
            Items = items;
        }
    }

    /// <summary>
    /// Pulls tessellated triangles out of Navisworks.
    ///
    /// There is no managed API for mesh data — the .NET API exposes geometry only as
    /// bounding boxes and primitive counts. The only route to actual vertices is the
    /// COM API: convert a <see cref="ModelItemCollection"/> to an
    /// <c>InwOpSelection</c>, walk its paths, and ask each fragment to replay its
    /// primitives into a callback.
    ///
    /// Two details that are easy to get wrong and expensive to debug:
    ///
    ///  * <c>path.Fragments()</c> returns the fragments of the path's *node*, and an
    ///    instanced node is shared by several paths. Without comparing
    ///    <c>fragment.path.ArrayData</c> against <c>path.ArrayData</c> you export the
    ///    same instance's geometry once per instance, at the wrong locations.
    ///
    ///  * COM RCWs handed back by <c>Paths()</c> / <c>Fragments()</c> can be collected
    ///    while still being enumerated, which surfaces as a hard crash rather than an
    ///    exception. <c>GC.KeepAlive</c> on the collections pins them for the loop.
    /// </summary>
    internal class GeometryExtractor
    {
        private readonly ExportOptions _options;
        private readonly MaterialResolver _materials;

        public GeometryExtractor(ExportOptions options, MaterialResolver materials)
        {
            _options = options;
            _materials = materials;
        }

        public SceneData Extract(Document document, string sceneName, IList<ExportPart> parts, ProgressContext progress)
        {
            var scene = new SceneData
            {
                Name = sceneName,
                Materials = _materials.Materials,
                SourceDocument = document.Title ?? "",
                SourceUnits = document.Units.ToString(),
                TargetUnits = _options.TargetUnits.ToString(),
                YUpApplied = _options.ConvertToYUp && IsZUp(document)
            };

            double unitScale = UnitConversion.ScaleFactor(document.Units, _options.TargetUnits);
            bool yUp = scene.YUpApplied;

            // Everything gathered up front: the offset must be known before the first
            // vertex is written, and the item list drives the progress bar.
            List<ModelItem> allItems = new List<ModelItem>();
            var itemsByPart = new List<KeyValuePair<ExportPart, List<ModelItem>>>();
            foreach (ExportPart part in parts)
            {
                List<ModelItem> geometryItems = CollectGeometryItems(part.Items);
                itemsByPart.Add(new KeyValuePair<ExportPart, List<ModelItem>>(part, geometryItems));
                allItems.AddRange(geometryItems);
            }

            if (allItems.Count == 0)
            {
                Log.Warning("'" + sceneName + "' contains no geometry items — nothing to export.");
                return scene;
            }

            Vec3 offset = ComputeOffset(document, parts);
            scene.AppliedOffset = ScaleAndOrient(offset, unitScale, yUp);

            var callback = new PrimitiveCallback(_options, offset, unitScale, yUp);

            int processed = 0;
            int total = allItems.Count;

            foreach (KeyValuePair<ExportPart, List<ModelItem>> entry in itemsByPart)
            {
                progress.ThrowIfCancelled();
                ExtractPart(scene, entry.Key, entry.Value, callback, progress, ref processed, total);
            }

            foreach (NodeBuilder node in scene.Nodes)
                foreach (PrimitiveBucket bucket in node.Buckets.Values)
                    foreach (MeshBuilder builder in bucket.Builders)
                        scene.Bounds.Add(builder.Bounds);

            Log.Info(string.Format(CultureInfo.InvariantCulture,
                "'{0}': {1:N0} items, {2:N0} triangles, {3:N0} vertices, {4:N0} materials{5}.",
                sceneName, total, callback.TrianglesAdded, scene.VertexCount, _materials.Materials.Count,
                callback.TrianglesSkipped > 0
                    ? string.Format(CultureInfo.InvariantCulture, " ({0:N0} degenerate triangles dropped)", callback.TrianglesSkipped)
                    : ""));

            return scene;
        }

        private void ExtractPart(
            SceneData scene,
            ExportPart part,
            List<ModelItem> items,
            PrimitiveCallback callback,
            ProgressContext progress,
            ref int processed,
            int total)
        {
            NodeBuilder mergedNode = null;
            var nodesByModel = new Dictionary<string, NodeBuilder>(StringComparer.OrdinalIgnoreCase);

            if (_options.Grouping == GroupingMode.MergeByMaterial)
            {
                mergedNode = new NodeBuilder(part.Name, _options);
                mergedNode.Extras["navex:set"] = part.Name;
                scene.Nodes.Add(mergedNode);
            }

            int batchSize = Math.Max(1, _options.ComBatchSize);

            for (int start = 0; start < items.Count; start += batchSize)
            {
                progress.ThrowIfCancelled();

                int count = Math.Min(batchSize, items.Count - start);
                var batch = new ModelItemCollection();
                for (int i = 0; i < count; i++)
                    batch.Add(items[start + i]);

                ComApi.InwOpSelection selection = ComBridge.ToInwOpSelection(batch);
                ComApi.InwSelectionPathsColl paths = selection.Paths();

                foreach (ComApi.InwOaPath path in paths)
                {
                    ModelItem item = null;
                    try { item = ComBridge.ToModelItem(path); }
                    catch (Exception) { }

                    NodeBuilder node = ResolveNode(scene, part, item, mergedNode, nodesByModel);
                    EmitPath(path, item, node, callback);
                }

                GC.KeepAlive(paths);
                GC.KeepAlive(selection);

                processed += count;
                progress.Update(
                    string.Format(CultureInfo.InvariantCulture, "{0}: {1:N0} / {2:N0} items", part.Name, processed, total),
                    total == 0 ? -1 : (double)processed / total);
                progress.Tick();
            }
        }

        private void EmitPath(ComApi.InwOaPath path, ModelItem item, NodeBuilder node, PrimitiveCallback callback)
        {
            ComApi.InwNodeFragsColl fragments;
            try { fragments = path.Fragments(); }
            catch (Exception ex)
            {
                Log.Debug("Could not read fragments for an item: " + ex.Message);
                return;
            }

            int[] pathData = ToIntArray(path.ArrayData);
            bool firstFragment = true;
            int materialIndex = _materials.DefaultMaterialIndex;

            foreach (ComApi.InwOaFragment3 fragment in fragments)
            {
                try
                {
                    if (!IsSamePath(pathData, ToIntArray(fragment.path.ArrayData)))
                        continue;

                    if (firstFragment)
                    {
                        object appearance = null;
                        if (_options.UseComMaterials)
                        {
                            try { appearance = fragment.Appearance; }
                            catch (Exception) { appearance = null; }
                        }
                        materialIndex = _materials.Resolve(item, appearance);
                        firstFragment = false;
                    }

                    var transform = (ComApi.InwLTransform3f3)fragment.GetLocalToWorldMatrix();
                    Matrix4 matrix = new Matrix4(ToDoubleArray((Array)transform.Matrix));

                    PrimitiveBucket triangles = node.Bucket(materialIndex, PrimitiveMode.Triangles);
                    PrimitiveBucket lines = _options.IncludeLines ? node.Bucket(materialIndex, PrimitiveMode.Lines) : null;

                    callback.BeginFragment(matrix, triangles, lines);
                    fragment.GenerateSimplePrimitives(VertexProperties(), callback);
                }
                catch (Exception ex)
                {
                    Log.Debug("Skipped a fragment: " + ex.Message);
                }
            }

            GC.KeepAlive(fragments);
        }

        private ComApi.nwEVertexProperty VertexProperties()
        {
            int flags = 0;
            if (_options.IncludeNormals) flags |= (int)ComApi.nwEVertexProperty.eNORMAL;
            if (_options.IncludeVertexColors) flags |= (int)ComApi.nwEVertexProperty.eCOLOR;
            if (flags == 0) flags = (int)ComApi.nwEVertexProperty.eNORMAL;
            return (ComApi.nwEVertexProperty)flags;
        }

        private NodeBuilder ResolveNode(
            SceneData scene,
            ExportPart part,
            ModelItem item,
            NodeBuilder mergedNode,
            Dictionary<string, NodeBuilder> nodesByModel)
        {
            switch (_options.Grouping)
            {
                case GroupingMode.PerModelFile:
                {
                    string modelName = SafeModelName(item);
                    NodeBuilder node;
                    if (!nodesByModel.TryGetValue(modelName, out node))
                    {
                        node = new NodeBuilder(modelName, _options);
                        node.Extras["navex:set"] = part.Name;
                        node.Extras["navex:sourceFile"] = modelName;
                        nodesByModel[modelName] = node;
                        scene.Nodes.Add(node);
                    }
                    return node;
                }

                case GroupingMode.PerItem:
                {
                    var node = new NodeBuilder(SafeItemName(item), _options);
                    if (_options.EmbedItemExtras) PopulateItemExtras(node, part, item);
                    scene.Nodes.Add(node);
                    return node;
                }

                default:
                    return mergedNode;
            }
        }

        private static void PopulateItemExtras(NodeBuilder node, ExportPart part, ModelItem item)
        {
            node.Extras["navex:set"] = part.Name;
            if (item == null) return;

            try
            {
                node.Extras["navex:guid"] = item.InstanceGuid.ToString();
                node.Extras["navex:class"] = item.ClassDisplayName ?? "";
                if (item.HasModel && item.Model != null)
                    node.Extras["navex:sourceFile"] = System.IO.Path.GetFileName(
                        item.Model.SourceFileName ?? item.Model.FileName ?? "");

                ModelItem parent = item.Parent;
                if (parent != null && !string.IsNullOrEmpty(parent.DisplayName))
                    node.Extras["navex:parent"] = parent.DisplayName;
            }
            catch (Exception) { }
        }

        private static string SafeItemName(ModelItem item)
        {
            if (item == null) return "item";
            try
            {
                if (!string.IsNullOrWhiteSpace(item.DisplayName)) return item.DisplayName;
                if (!string.IsNullOrWhiteSpace(item.ClassDisplayName)) return item.ClassDisplayName;
            }
            catch (Exception) { }
            return "item";
        }

        private static string SafeModelName(ModelItem item)
        {
            try
            {
                if (item != null && item.HasModel && item.Model != null)
                {
                    string source = item.Model.SourceFileName;
                    if (string.IsNullOrEmpty(source)) source = item.Model.FileName;
                    if (!string.IsNullOrEmpty(source))
                        return System.IO.Path.GetFileNameWithoutExtension(source);
                }
            }
            catch (Exception) { }
            return "model";
        }

        /// <summary>
        /// Flattens a selection down to the leaf items that actually carry geometry.
        /// A search set typically holds composite items whose triangles live on
        /// descendants, so exporting the selection verbatim would silently produce an
        /// empty file.
        /// </summary>
        private List<ModelItem> CollectGeometryItems(ModelItemCollection selection)
        {
            var result = new List<ModelItem>();
            // Keyed on the item itself, not on InstanceHashCode: that hash is shared
            // by every placement of an instanced node, so using it would export one
            // bolt out of five hundred. ModelItem equality is per-path, which is
            // exactly the duplicate we do want to collapse — a selection holding
            // both a parent and its child yields the child twice.
            var seen = new HashSet<ModelItem>();

            foreach (ModelItem item in selection.DescendantsAndSelf)
            {
                bool hasGeometry;
                try { hasGeometry = item.HasGeometry; }
                catch (Exception) { continue; }

                if (!hasGeometry) continue;

                if (_options.SkipHidden)
                {
                    try { if (item.IsHidden) continue; }
                    catch (Exception) { }
                }

                if (seen.Add(item))
                    result.Add(item);
            }

            return result;
        }

        private Vec3 ComputeOffset(Document document, IList<ExportPart> parts)
        {
            switch (_options.Origin)
            {
                case OriginMode.WorldOrigin:
                    return new Vec3(0, 0, 0);

                case OriginMode.Custom:
                    return _options.CustomOrigin;

                case OriginMode.ModelCenter:
                {
                    BoundingBox3D box = document.GetBoundingBox(false);
                    return box == null || box.IsEmpty
                        ? new Vec3(0, 0, 0)
                        : new Vec3(box.Center.X, box.Center.Y, box.Center.Z);
                }

                default:
                {
                    var bounds = new Bounds();
                    foreach (ExportPart part in parts)
                    {
                        try
                        {
                            BoundingBox3D box = part.Items.BoundingBox();
                            if (box == null || box.IsEmpty) continue;
                            bounds.Add(new Vec3(box.Min.X, box.Min.Y, box.Min.Z));
                            bounds.Add(new Vec3(box.Max.X, box.Max.Y, box.Max.Z));
                        }
                        catch (Exception) { }
                    }

                    if (bounds.IsEmpty) return new Vec3(0, 0, 0);
                    Vec3 center = bounds.Center;
                    return _options.Origin == OriginMode.SelectionCenterKeepElevation
                        ? new Vec3(center.X, center.Y, 0)
                        : center;
                }
            }
        }

        private static Vec3 ScaleAndOrient(Vec3 offsetInDocumentUnits, double unitScale, bool yUp)
        {
            Vec3 scaled = offsetInDocumentUnits * unitScale;
            return yUp ? new Vec3(scaled.X, scaled.Z, -scaled.Y) : scaled;
        }

        /// <summary>
        /// Navisworks scenes are conventionally Z-up, but a document assembled from
        /// Y-up sources reports a different up vector — converting those again would
        /// lay the model on its side.
        /// </summary>
        private static bool IsZUp(Document document)
        {
            try
            {
                Vector3D up = document.UpVector;
                return Math.Abs(up.Z) > 0.9;
            }
            catch (Exception)
            {
                return true;
            }
        }

        private static bool IsSamePath(int[] a, int[] b)
        {
            if (a == null || b == null) return false;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
            return true;
        }

        private static int[] ToIntArray(object arrayData)
        {
            Array source = arrayData as Array;
            if (source == null) return new int[0];

            var result = new int[source.Length];
            int index = 0;
            foreach (object value in source)
                result[index++] = Convert.ToInt32(value);
            return result;
        }

        private static double[] ToDoubleArray(Array source)
        {
            if (source == null) return Matrix4.Identity().M;

            var result = new double[Math.Max(16, source.Length)];
            int index = 0;
            foreach (object value in source)
            {
                if (index >= result.Length) break;
                result[index++] = Convert.ToDouble(value);
            }

            // A short or empty matrix would silently collapse the model onto the
            // origin; identity at least keeps the geometry where the source put it.
            if (index < 16) return Matrix4.Identity().M;
            return result;
        }
    }
}
