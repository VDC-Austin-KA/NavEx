using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Autodesk.Navisworks.Api;
using NavEx.Core.Exporters;
using NavEx.FourD;

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
        /// <summary>The 4D schedule sidecar a Datasmith export wrote, or null.</summary>
        public string SchedulePath;
        /// <summary>Why no sidecar was written, when one was expected. Null when there is nothing to say.</summary>
        public string ScheduleNote;

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

            // A .udatasmith carries geometry, materials and the per-node pixmy.* linkage
            // but no tasks and no dates. The schedule therefore rides alongside as JSON:
            // that sidecar is what lets Unrealistic4D date the tasks and assign the
            // imported geometry to them without a separate TimeLiner export.
            if (_options.Format == ExportFormat.Datasmith && !summary.Cancelled)
                WriteScheduleSidecar(document, summary);

            summary.Elapsed = DateTime.UtcNow - started;
            return summary;
        }

        private void WriteScheduleSidecar(Document document, ExportSummary summary)
        {
            try
            {
                summary.SchedulePath = ScheduleJsonWriter.Write(
                    _options,
                    document == null ? "" : document.Title,
                    summary.Results,
                    _options.OutputFolder);

                if (summary.SchedulePath != null)
                {
                    Log.Success("Schedule -> " + Path.GetFileName(summary.SchedulePath));
                    return;
                }

                // Nothing written is the common outcome for anyone who has not been to
                // the 4D tab, and a silently absent file looks exactly like a broken
                // exporter. Say which of the two reasons it was, with the names, so the
                // difference between "no schedule loaded" and "the schedule is loaded
                // but names different sets" is visible without guessing.
                summary.ScheduleNote = ExplainMissingSchedule(summary);
                Log.Warning(summary.ScheduleNote);
            }
            catch (Exception ex)
            {
                // The geometry is already on disk and still usable; losing the sidecar
                // must not turn a finished export into a failed one.
                summary.ScheduleNote = "Could not write " + ScheduleJsonWriter.FileName + ": " + ex.Message;
                Log.Error("Could not write " + ScheduleJsonWriter.FileName, ex);
            }
        }

        /// <summary>
        /// Why no schedule sidecar came out of a Datasmith export. The two causes need
        /// different fixes, so they get different sentences.
        /// </summary>
        private string ExplainMissingSchedule(ExportSummary summary)
        {
            int links = _options.DatasmithTaskLinks == null ? 0 : _options.DatasmithTaskLinks.Count;

            if (links == 0)
            {
                return "No " + ScheduleJsonWriter.FileName + " was written: no schedule task is "
                     + "matched to any selection set. Go to the 4D tab, read TimeLiner or import a "
                     + "schedule, check the matches, then export again.";
            }

            if (_options.Batch == BatchMode.SingleCombinedFile && summary.Results.Count == 1)
            {
                return "No " + ScheduleJsonWriter.FileName + " was written: a single combined file "
                     + "is one scene with no per-set identity to hang tasks on. Switch Batch to "
                     + "\"One file per set\" so each set can carry its own task.";
            }

            var exported = new List<string>();
            foreach (ExportResult result in summary.Results)
                if (result != null && !result.Failed && !string.IsNullOrEmpty(result.SetName))
                    exported.Add(result.SetName);

            var matched = new List<string>();
            foreach (KeyValuePair<string, ScheduleTask> entry in _options.DatasmithTaskLinks)
                matched.Add(entry.Key);

            return string.Format(CultureInfo.InvariantCulture,
                "No {0} was written: {1:N0} set(s) are matched to schedule tasks, but none of them is "
                + "a set this export wrote. Exported: {2}. Matched on the 4D tab: {3}.",
                ScheduleJsonWriter.FileName, links, Sample(exported), Sample(matched));
        }

        /// <summary>First few names, so a name mismatch is obvious without a full dump.</summary>
        private static string Sample(List<string> names)
        {
            if (names.Count == 0) return "(none)";
            var sb = new StringBuilder();
            int shown = Math.Min(3, names.Count);
            for (int i = 0; i < shown; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(names[i]);
            }
            if (names.Count > shown)
                sb.Append(string.Format(CultureInfo.InvariantCulture, " (+{0:N0} more)", names.Count - shown));
            return sb.ToString();
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
                    case ExportFormat.Datasmith:
                        written = new DatasmithWriter(_options).Write(scene, path);
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
