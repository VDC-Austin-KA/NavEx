using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Autodesk.Navisworks.Api;
using NavEx.Core.Exporters;

namespace NavEx.Core
{
    public static class PluginInfo
    {
        public const string Name = "NavEx";
        public const string Version = "1.0.0";
        public const string Description = "Navisworks geometry exporter — GLB / glTF / OBJ";
    }

    /// <summary>Aggregate outcome of one Export button press.</summary>
    public class ExportSummary
    {
        public readonly List<ExportResult> Results = new List<ExportResult>();
        public bool Cancelled;
        public TimeSpan Elapsed;

        public int SucceededCount
        {
            get
            {
                int count = 0;
                foreach (ExportResult result in Results) if (!result.Failed) count++;
                return count;
            }
        }

        public int FailedCount { get { return Results.Count - SucceededCount; } }

        public long TotalBytes
        {
            get
            {
                long total = 0;
                foreach (ExportResult result in Results) if (!result.Failed) total += result.FileSizeBytes;
                return total;
            }
        }

        public int TotalTriangles
        {
            get
            {
                int total = 0;
                foreach (ExportResult result in Results) if (!result.Failed) total += result.TriangleCount;
                return total;
            }
        }
    }

    /// <summary>
    /// Drives a whole export: parts in, files out.
    ///
    /// Everything here runs on the Navisworks main thread — the API is not
    /// thread-safe and the COM bridge is apartment-bound — so responsiveness comes
    /// from <see cref="ProgressContext.Pump"/> being called between batches rather
    /// than from a worker thread.
    /// </summary>
    public class ExportRunner
    {
        private readonly ExportOptions _options;

        public ExportRunner(ExportOptions options)
        {
            _options = options;
        }

        public ExportSummary Run(Document document, IList<ExportPart> parts, ProgressContext progress)
        {
            var summary = new ExportSummary();
            DateTime started = DateTime.UtcNow;

            if (parts == null || parts.Count == 0)
            {
                Log.Warning("Nothing selected to export.");
                return summary;
            }

            if (string.IsNullOrWhiteSpace(_options.OutputFolder))
            {
                Log.Error("No output folder set.");
                return summary;
            }

            try
            {
                Directory.CreateDirectory(_options.OutputFolder);
            }
            catch (Exception ex)
            {
                Log.Error("Could not create the output folder", ex);
                return summary;
            }

            try
            {
                if (_options.Batch == BatchMode.SingleCombinedFile)
                {
                    string name = _options.BuildFileName(
                        parts.Count == 1 ? parts[0].Name : "combined", 1, document.Title);
                    summary.Results.Add(ExportScene(document, name, name, parts, progress));
                }
                else
                {
                    for (int i = 0; i < parts.Count; i++)
                    {
                        progress.ThrowIfCancelled();
                        ExportPart part = parts[i];
                        string name = _options.BuildFileName(part.Name, i + 1, document.Title);
                        summary.Results.Add(ExportScene(document, part.Name, name,
                            new List<ExportPart> { part }, progress));
                    }
                }
            }
            catch (OperationCanceledException)
            {
                summary.Cancelled = true;
                Log.Warning("Export cancelled.");
            }
            catch (Exception ex)
            {
                Log.Error("Export failed", ex);
            }

            summary.Elapsed = DateTime.UtcNow - started;
            return summary;
        }

        private ExportResult ExportScene(
            Document document,
            string sceneName,
            string fileBaseName,
            IList<ExportPart> parts,
            ProgressContext progress)
        {
            string path = Path.Combine(_options.OutputFolder, fileBaseName + _options.ExtensionForFormat());

            if (File.Exists(path) && !_options.OverwriteExisting)
                path = MakeUniquePath(path);

            var result = new ExportResult { SetName = sceneName, FilePath = path };

            try
            {
                var materials = new MaterialResolver(_options);
                var extractor = new GeometryExtractor(_options, materials);

                progress.Update("Reading geometry for '" + sceneName + "'…", -1);
                SceneData scene = extractor.Extract(document, sceneName, parts, progress);

                if (scene.TriangleCount == 0 && scene.VertexCount == 0)
                {
                    result.Failed = true;
                    result.FailureReason = "no geometry found";
                    Log.Warning("'" + sceneName + "' produced no geometry — no file written.");
                    return result;
                }

                progress.Update("Writing " + Path.GetFileName(path) + "…", -1);
                progress.Tick();

                ExportResult written;
                switch (_options.Format)
                {
                    case ExportFormat.Obj:
                        written = new ObjWriter(_options).Write(scene, path);
                        break;
                    case ExportFormat.Fbx:
                        written = new FbxWriter(_options).Write(scene, path);
                        break;
                    default:
                        written = new GltfWriter(_options).Write(scene, path);
                        break;
                }

                written.SetName = sceneName;

                if (_options.ExportPropertiesSidecar)
                {
                    progress.Update("Writing properties for '" + sceneName + "'…", -1);
                    written.SidecarFiles.Add(new PropertyExporter(_options).Write(document, parts, path, progress));
                }

                Log.Success(string.Format(CultureInfo.InvariantCulture,
                    "{0} → {1}  ({2}, {3:N0} triangles, {4:N0} vertices, {5} materials)",
                    sceneName, Path.GetFileName(path), written.SizeDisplay,
                    written.TriangleCount, written.VertexCount, written.MaterialCount));

                return written;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                result.Failed = true;
                result.FailureReason = ex.Message;
                Log.Error("Failed to export '" + sceneName + "'", ex);
                return result;
            }
        }

        /// <summary>
        /// Estimates the cost of an export without writing anything, using the
        /// primitive counts Navisworks already tracks per geometry item. Cheap enough
        /// to run on every selection change, and it catches the "this set is 40
        /// million triangles" case before someone waits ten minutes for it.
        /// </summary>
        public static string Estimate(IList<ExportPart> parts, ProgressContext progress)
        {
            long triangles = 0;
            long items = 0;

            foreach (ExportPart part in parts)
            {
                foreach (ModelItem item in part.Items.DescendantsAndSelf)
                {
                    if (progress != null && progress.IsCancelled) return "cancelled";

                    try
                    {
                        if (!item.HasGeometry || item.Geometry == null) continue;
                        items++;
                        triangles += item.Geometry.PrimitiveCount;
                    }
                    catch (Exception) { }
                }
            }

            // Welded float32 positions + normals + 16/32-bit indices lands close to
            // 40 bytes per triangle in practice; unwelded is roughly double.
            double bytesPerTriangle = 40;
            double estimated = triangles * bytesPerTriangle;
            string size = FormatBytes(estimated);

            return string.Format(CultureInfo.InvariantCulture,
                "{0:N0} geometry items · ~{1:N0} triangles · roughly {2} of GLB",
                items, triangles, size);
        }

        private static string FormatBytes(double bytes)
        {
            string[] units = { "B", "KB", "MB", "GB" };
            int unit = 0;
            while (bytes >= 1024 && unit < units.Length - 1) { bytes /= 1024; unit++; }
            return string.Format(CultureInfo.InvariantCulture, "{0:0.#} {1}", bytes, units[unit]);
        }

        private static string MakeUniquePath(string path)
        {
            string directory = Path.GetDirectoryName(path) ?? "";
            string name = Path.GetFileNameWithoutExtension(path);
            string extension = Path.GetExtension(path);

            for (int i = 2; i < 1000; i++)
            {
                string candidate = Path.Combine(directory, name + "_" + i.ToString(CultureInfo.InvariantCulture) + extension);
                if (!File.Exists(candidate)) return candidate;
            }

            return path;
        }
    }
}
