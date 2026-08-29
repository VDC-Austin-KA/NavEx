using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using NavEx.FourD;

namespace NavEx.Core.Exporters
{
    /// <summary>
    /// Writes a <see cref="SceneData"/> as a .udatasmith file — XML plus a payload
    /// folder, written directly with no Datasmith SDK, same reasoning as
    /// <see cref="GltfWriter"/> and <see cref="FbxWriter"/>.
    ///
    /// This is the seam that replaces the FBX + JSON-sidecar Unreal workflow. Every
    /// exported node carries its `pixmy.*` schedule-linkage metadata inline, per the
    /// contract at Convergence/docs/contracts/schedule-link.md — Unrealistic4D reads
    /// this file directly, field names and all. Do not rename or invent fields here
    /// without updating that document; it is the single source of truth for both
    /// repos.
    ///
    /// Geometry payload is delegated to <see cref="FbxWriter"/> (one combined FBX
    /// sitting in a sibling "_Assets" folder, Datasmith's usual convention) — the
    /// scene graph is not duplicated, only referenced by the same node names.
    /// </summary>
    internal class DatasmithWriter
    {
        private const string ContractVersion = "1.0";

        private readonly ExportOptions _options;

        public DatasmithWriter(ExportOptions options)
        {
            _options = options;
        }

        public ExportResult Write(SceneData scene, string outputPath)
        {
            var result = new ExportResult
            {
                FilePath = outputPath,
                TriangleCount = scene.TriangleCount,
                VertexCount = scene.VertexCount,
                MaterialCount = 0
            };

            string assetsDirName = Path.GetFileNameWithoutExtension(outputPath) + "_Assets";
            string assetsDir = Path.Combine(Path.GetDirectoryName(outputPath) ?? "", assetsDirName);
            Directory.CreateDirectory(assetsDir);
            string fbxPath = Path.Combine(assetsDir, "geometry.fbx");
            ExportResult payload = new FbxWriter(_options).Write(scene, fbxPath);
            result.SidecarFiles.Add(fbxPath);

            var invariant = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();

            sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n");
            sb.Append("<DatasmithUnrealScene>\n");
            sb.Append("\t<Version>1.0</Version>\n");
            sb.Append("\t<SDKVersion>0.0</SDKVersion>\n");
            sb.Append("\t<Host>NavEx</Host>\n");
            sb.Append("\t<Application Name=\"NavEx\" Vendor=\"NavEx\" ProductName=\"NavEx Navisworks Exporter\" ProductVersion=\"")
              .Append(Xml(PluginInfo.Version)).Append("\"/>\n");

            WriteProvenance(sb, scene, invariant);

            int index = 0;
            foreach (NodeBuilder node in scene.NonEmptyNodes)
            {
                // Two different names, deliberately. The payload reference has to be
                // the name FbxWriter actually emitted; the Datasmith element name is
                // what Unreal names the imported StaticMesh asset after, so it is
                // derived from the selection set instead. Without that, every set in a
                // file-per-set export imports as an asset with the same name (the real
                // exports are 179 of "model") and nothing downstream can tell them
                // apart by name.
                string payloadName = FbxWriter.SanitizeName(node.Name, "node_" + index);
                string meshName = ElementName(node, index);
                string actorName = "actor_" + meshName;

                sb.Append("\t<StaticMesh name=\"").Append(Xml(meshName)).Append("\">\n");
                sb.Append("\t\t<Label value=\"").Append(Xml(meshName)).Append("\"/>\n");
                sb.Append("\t\t<File path=\"").Append(Xml(assetsDirName + "/geometry.fbx")).Append("\" object=\"")
                  .Append(Xml("Model::" + payloadName)).Append("\"/>\n");
                sb.Append("\t</StaticMesh>\n");

                sb.Append("\t<Actor name=\"").Append(Xml(actorName)).Append("\" type=\"StaticMeshActor\" layer=\"NavEx\">\n");
                sb.Append("\t\t<Label value=\"").Append(Xml(meshName)).Append("\"/>\n");
                sb.Append("\t\t<mesh name=\"").Append(Xml(meshName)).Append("\"/>\n");
                WriteNodeMetaData(sb, actorName, node, index, scene);
                sb.Append("\t</Actor>\n");

                index++;
                result.NodeCount++;
            }

            sb.Append("</DatasmithUnrealScene>\n");

            File.WriteAllText(outputPath, sb.ToString(), new UTF8Encoding(false));

            result.FileSizeBytes = new FileInfo(outputPath).Length;
            result.FileSizeBytes += payload.FileSizeBytes;
            return result;
        }

        /// <summary>
        /// The name Unreal will end up calling this node's StaticMesh asset. Prefers
        /// the selection set: that is the schedule's granularity, the value of
        /// pixmy.set.name, and the folder the Datasmith import drops the asset into, so
        /// an asset named after it resolves by exact path on the Unreal side. Falls
        /// back to the node name when the node came from no named set, and suffixes the
        /// index when one file holds several nodes, since Datasmith element names have
        /// to be unique within a scene.
        /// </summary>
        private static string ElementName(NodeBuilder node, int index)
        {
            string setName;
            node.Extras.TryGetValue("navex:set", out setName);
            string basis = string.IsNullOrEmpty(setName) ? node.Name : setName;
            string safe = FbxWriter.SanitizeName(basis, "node_" + index);
            return index == 0 ? safe : safe + "_" + index.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// The provenance block GltfWriter.BuildProvenance already established, carried
        /// across verbatim (same navex: keys) per the schedule-link contract — this is a
        /// NavEx-authored top-level element, not part of the official Datasmith schema,
        /// since the schema has no scene-wide free-form metadata container.
        /// </summary>
        private void WriteProvenance(StringBuilder sb, SceneData scene, CultureInfo invariant)
        {
            sb.Append("\t<NavExProvenance>\n");
            AppendProperty(sb, "navex:sourceDocument", scene.SourceDocument ?? "");
            AppendProperty(sb, "navex:sourceUnits", scene.SourceUnits ?? "");
            AppendProperty(sb, "navex:targetUnits", scene.TargetUnits ?? "");
            AppendProperty(sb, "navex:upAxis", scene.YUpApplied ? "Y (converted from Z-up)" : "Z (unchanged)");
            AppendProperty(sb, "navex:originMode", _options.Origin.ToString());
            AppendProperty(sb, "navex:appliedOffset", string.Format(invariant, "{0},{1},{2}",
                scene.AppliedOffset.X, scene.AppliedOffset.Y, scene.AppliedOffset.Z));
            AppendProperty(sb, "navex:offsetNote",
                "Add appliedOffset to exported coordinates to return to source world coordinates.");
            AppendProperty(sb, "navex:exportedUtc", DateTime.UtcNow.ToString("o", invariant));
            sb.Append("\t</NavExProvenance>\n");
        }

        private void WriteNodeMetaData(StringBuilder sb, string actorName, NodeBuilder node, int index, SceneData scene)
        {
            string setName;
            node.Extras.TryGetValue("navex:set", out setName);
            setName = setName ?? "";

            sb.Append("\t\t<MetaData element=\"").Append(Xml(actorName)).Append("\">\n");

            AppendProperty(sb, "pixmy.contractVersion", ContractVersion);

            string guid;
            bool synthetic;
            ResolveSourceGuid(node, index, scene, out guid, out synthetic);
            AppendProperty(sb, "pixmy.sourceGuid", guid);
            AppendProperty(sb, "pixmy.guidSynthetic", synthetic ? "true" : "false");

            AppendProperty(sb, "pixmy.set.name", setName);

            ScheduleTask task = null;
            if (_options.DatasmithTaskLinks != null && setName.Length > 0)
                _options.DatasmithTaskLinks.TryGetValue(setName, out task);

            if (task != null)
            {
                AppendProperty(sb, "pixmy.task.stableKey", task.StableKey);
                AppendPropertyIfPresent(sb, "pixmy.task.displayName", task.Name);
                AppendDateIfPresent(sb, "pixmy.task.plannedStart", task.PlannedStart);
                AppendDateIfPresent(sb, "pixmy.task.plannedFinish", task.PlannedFinish);
                AppendDateIfPresent(sb, "pixmy.task.actualStart", task.ActualStart);
                AppendDateIfPresent(sb, "pixmy.task.actualFinish", task.ActualFinish);
                AppendProperty(sb, "pixmy.task.type", task.NormalizedTaskType());
            }

            FourDName parsed = FourDName.TryParse(setName);
            if (parsed != null)
            {
                AppendPropertyIfPresent(sb, "pixmy.name.sequenceCode", parsed.SequenceCode);
                AppendPropertyIfPresent(sb, "pixmy.name.zone", parsed.Zone);
                AppendPropertyIfPresent(sb, "pixmy.name.level", parsed.LevelTag);
                AppendPropertyIfPresent(sb, "pixmy.name.discipline", parsed.DisciplineCode);
                AppendPropertyIfPresent(sb, "pixmy.name.activity", parsed.ActivityCode);
            }

            sb.Append("\t\t</MetaData>\n");
        }

        /// <summary>
        /// The whole point of this contract: a stable, non-zero, per-export-unique GUID.
        /// Prefers the real Navisworks instance GUID NavEx already stamps into
        /// node.Extras["navex:guid"] for per-item exports (see GeometryExtractor.
        /// PopulateItemExtras) — but that field is all-zeroes in the real sample data
        /// (the bug this contract exists to replace) and is entirely absent for
        /// merge-by-material/per-model-file exports, so both cases fall back to a
        /// deterministic hash of the source document plus the node's identity within
        /// this export. That hash is stable across re-exports of an unchanged model
        /// (same document name, same node name/index) and unique within one export
        /// (the index is folded in).
        /// </summary>
        private static void ResolveSourceGuid(NodeBuilder node, int index, SceneData scene, out string guid, out bool synthetic)
        {
            string raw;
            Guid parsed;
            if (node.Extras.TryGetValue("navex:guid", out raw) &&
                Guid.TryParse(raw, out parsed) && parsed != Guid.Empty)
            {
                guid = parsed.ToString();
                synthetic = false;
                return;
            }

            guid = SynthesizeGuid(scene.SourceDocument, index + "/" + node.Name).ToString();
            synthetic = true;
        }

        private static Guid SynthesizeGuid(string modelFileName, string itemPath)
        {
            using (MD5 md5 = MD5.Create())
            {
                byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes((modelFileName ?? "") + "|" + (itemPath ?? "")));

                // Stamp RFC 4122 version/variant bits so the result reads as an ordinary
                // (version 4, name-derived) GUID rather than raw hash bytes.
                hash[7] = (byte)((hash[7] & 0x0F) | 0x40);
                hash[8] = (byte)((hash[8] & 0x3F) | 0x80);

                bool allZero = true;
                for (int i = 0; i < hash.Length; i++)
                {
                    if (hash[i] != 0) { allZero = false; break; }
                }
                // MD5 of a non-empty string being all-zero is not realistically reachable,
                // but the contract is explicit that this must never happen — guard it anyway.
                if (allZero) hash[0] = 0x01;

                return new Guid(hash);
            }
        }

        private static void AppendProperty(StringBuilder sb, string name, string value)
        {
            sb.Append("\t\t\t<KeyValueProperty name=\"").Append(Xml(name)).Append("\" type=\"String\" val=\"")
              .Append(Xml(value ?? "")).Append("\"/>\n");
        }

        private static void AppendPropertyIfPresent(StringBuilder sb, string name, string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            AppendProperty(sb, name, value);
        }

        private static void AppendDateIfPresent(StringBuilder sb, string name, DateTime? value)
        {
            if (!value.HasValue) return;
            // ISO 8601, no time zone offset — the schedule-link contract is explicit about this.
            AppendProperty(sb, name, value.Value.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture));
        }

        private static string Xml(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            var sb = new StringBuilder(value.Length);
            foreach (char c in value)
            {
                switch (c)
                {
                    case '&': sb.Append("&amp;"); break;
                    case '<': sb.Append("&lt;"); break;
                    case '>': sb.Append("&gt;"); break;
                    case '"': sb.Append("&quot;"); break;
                    case '\'': sb.Append("&apos;"); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }
    }
}
