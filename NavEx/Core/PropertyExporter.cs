using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Autodesk.Navisworks.Api;
using NavEx.Core.Exporters;

namespace NavEx.Core
{
    /// <summary>
    /// Writes a sidecar JSON file mapping every exported item back to its
    /// Navisworks identity and properties.
    ///
    /// glTF carries geometry beautifully and metadata barely at all — `extras` on a
    /// merged node cannot describe ten thousand source elements. Rather than bloat
    /// the model, NavEx keeps the identity data next to it: downstream tooling joins
    /// on the item GUID, and the GLB stays lean.
    /// </summary>
    internal class PropertyExporter
    {
        private readonly ExportOptions _options;

        public PropertyExporter(ExportOptions options)
        {
            _options = options;
        }

        public string Write(Document document, IList<ExportPart> parts, string modelPath, ProgressContext progress)
        {
            string path = Path.Combine(
                Path.GetDirectoryName(modelPath) ?? "",
                Path.GetFileNameWithoutExtension(modelPath) + ".properties.json");

            var invariant = CultureInfo.InvariantCulture;
            var filter = BuildFilter();
            int written = 0;

            using (var writer = new StreamWriter(path, false, new UTF8Encoding(false)))
            {
                writer.Write("{\"document\":");
                writer.Write(new JStr(document.Title ?? "").ToString());
                writer.Write(",\"exportedUtc\":");
                writer.Write(new JStr(DateTime.UtcNow.ToString("o", invariant)).ToString());
                writer.Write(",\"items\":[");

                bool first = true;
                foreach (ExportPart part in parts)
                {
                    foreach (ModelItem item in part.Items.DescendantsAndSelf)
                    {
                        progress.ThrowIfCancelled();

                        bool hasGeometry;
                        try { hasGeometry = item.HasGeometry; }
                        catch (Exception) { continue; }
                        if (!hasGeometry) continue;

                        string json = SerializeItem(item, part.Name, filter);
                        if (json == null) continue;

                        if (!first) writer.Write(',');
                        writer.Write(json);
                        first = false;

                        if (++written % 500 == 0)
                        {
                            progress.Update("Writing properties: " + written.ToString("N0", invariant) + " items", -1);
                            progress.Tick();
                        }
                    }
                }

                writer.Write("]}");
            }

            Log.Info(string.Format(invariant, "Properties sidecar: {0:N0} items -> {1}", written, Path.GetFileName(path)));
            return path;
        }

        private HashSet<string> BuildFilter()
        {
            if (_options.PropertyCategoryFilter == null || _options.PropertyCategoryFilter.Count == 0)
                return null;

            var filter = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string category in _options.PropertyCategoryFilter)
                if (!string.IsNullOrWhiteSpace(category)) filter.Add(category.Trim());
            return filter.Count == 0 ? null : filter;
        }

        private static string SerializeItem(ModelItem item, string setName, HashSet<string> categoryFilter)
        {
            try
            {
                var json = new JObj();
                json.Set("guid", item.InstanceGuid.ToString());
                json.Set("name", item.DisplayName ?? "");
                json.Set("class", item.ClassDisplayName ?? "");
                json.Set("set", setName ?? "");

                if (item.HasModel && item.Model != null)
                {
                    string source = item.Model.SourceFileName;
                    if (string.IsNullOrEmpty(source)) source = item.Model.FileName;
                    json.Set("sourceFile", Path.GetFileName(source ?? ""));
                }

                var categories = new JObj();
                foreach (PropertyCategory category in item.PropertyCategories)
                {
                    string categoryName = category.DisplayName;
                    if (string.IsNullOrEmpty(categoryName)) continue;
                    if (categoryFilter != null && !categoryFilter.Contains(categoryName)) continue;

                    var properties = new JObj();
                    foreach (DataProperty property in category.Properties)
                    {
                        if (string.IsNullOrEmpty(property.DisplayName)) continue;
                        string value = ReadValue(property);
                        if (value != null) properties.Set(property.DisplayName, value);
                    }

                    if (properties.Count > 0) categories.Set(categoryName, properties);
                }

                if (categories.Count > 0) json.Set("properties", categories);
                return json.ToString();
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string ReadValue(DataProperty property)
        {
            try
            {
                VariantData value = property.Value;
                if (value == null || value.IsNone) return null;
                return value.ToString();
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
