using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Autodesk.Navisworks.Api;

namespace NavEx.Core
{
    /// <summary>
    /// Persists <see cref="ExportOptions"/> between sessions, and lets a set of
    /// settings be saved as a named preset.
    ///
    /// Deliberately a flat key=value text file rather than JSON: it round-trips
    /// without a parser dependency, survives hand-editing, and a corrupt or
    /// half-written file degrades to defaults instead of throwing on load.
    /// </summary>
    public static class SettingsStore
    {
        private static string RootFolder
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "NavEx");
            }
        }

        public static string PresetFolder { get { return Path.Combine(RootFolder, "Presets"); } }

        private static string DefaultPath { get { return Path.Combine(RootFolder, "settings.txt"); } }

        public static void Save(ExportOptions options) { Save(options, DefaultPath); }

        public static ExportOptions Load() { return Load(DefaultPath); }

        public static void Save(ExportOptions options, string path)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path) ?? RootFolder);

                var sb = new StringBuilder();
                var invariant = CultureInfo.InvariantCulture;

                sb.AppendLine("OutputFolder=" + options.OutputFolder);
                sb.AppendLine("Format=" + options.Format);
                sb.AppendLine("Batch=" + options.Batch);
                sb.AppendLine("FileNameTemplate=" + options.FileNameTemplate);
                sb.AppendLine("OverwriteExisting=" + options.OverwriteExisting);
                sb.AppendLine("TargetUnits=" + options.TargetUnits);
                sb.AppendLine("Grouping=" + options.Grouping);
                sb.AppendLine("Origin=" + options.Origin);
                sb.AppendLine("CustomOriginX=" + options.CustomOrigin.X.ToString("R", invariant));
                sb.AppendLine("CustomOriginY=" + options.CustomOrigin.Y.ToString("R", invariant));
                sb.AppendLine("CustomOriginZ=" + options.CustomOrigin.Z.ToString("R", invariant));
                sb.AppendLine("ConvertToYUp=" + options.ConvertToYUp);
                sb.AppendLine("IncludeNormals=" + options.IncludeNormals);
                sb.AppendLine("IncludeVertexColors=" + options.IncludeVertexColors);
                sb.AppendLine("WeldVertices=" + options.WeldVertices);
                sb.AppendLine("WeldTolerance=" + options.WeldTolerance.ToString("R", invariant));
                sb.AppendLine("MinTriangleEdge=" + options.MinTriangleEdge.ToString("R", invariant));
                sb.AppendLine("MaxVerticesPerPrimitive=" + options.MaxVerticesPerPrimitive.ToString(invariant));
                sb.AppendLine("SkipHidden=" + options.SkipHidden);
                sb.AppendLine("IncludeLines=" + options.IncludeLines);
                sb.AppendLine("EmitMaterials=" + options.EmitMaterials);
                sb.AppendLine("PreserveTransparency=" + options.PreserveTransparency);
                sb.AppendLine("DoubleSided=" + options.DoubleSided);
                sb.AppendLine("DefaultRoughness=" + options.DefaultRoughness.ToString("R", invariant));
                sb.AppendLine("DefaultMetallic=" + options.DefaultMetallic.ToString("R", invariant));
                sb.AppendLine("UseComMaterials=" + options.UseComMaterials);
                sb.AppendLine("EmbedItemExtras=" + options.EmbedItemExtras);
                sb.AppendLine("ExportPropertiesSidecar=" + options.ExportPropertiesSidecar);
                sb.AppendLine("PropertyCategoryFilter=" + string.Join(";", options.PropertyCategoryFilter.ToArray()));
                sb.AppendLine("ComBatchSize=" + options.ComBatchSize.ToString(invariant));

                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Log.Debug("Could not save settings: " + ex.Message);
            }
        }

        public static ExportOptions Load(string path)
        {
            var options = new ExportOptions();

            try
            {
                if (!File.Exists(path)) return options;

                var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string line in File.ReadAllLines(path))
                {
                    int separator = line.IndexOf('=');
                    if (separator <= 0) continue;
                    values[line.Substring(0, separator).Trim()] = line.Substring(separator + 1).Trim();
                }

                options.OutputFolder = Str(values, "OutputFolder", options.OutputFolder);
                options.Format = Enum(values, "Format", options.Format);
                options.Batch = Enum(values, "Batch", options.Batch);
                options.FileNameTemplate = Str(values, "FileNameTemplate", options.FileNameTemplate);
                options.OverwriteExisting = Bool(values, "OverwriteExisting", options.OverwriteExisting);
                options.TargetUnits = Enum(values, "TargetUnits", options.TargetUnits);
                options.Grouping = Enum(values, "Grouping", options.Grouping);
                options.Origin = Enum(values, "Origin", options.Origin);
                options.CustomOrigin = new Vec3(
                    Num(values, "CustomOriginX", 0),
                    Num(values, "CustomOriginY", 0),
                    Num(values, "CustomOriginZ", 0));
                options.ConvertToYUp = Bool(values, "ConvertToYUp", options.ConvertToYUp);
                options.IncludeNormals = Bool(values, "IncludeNormals", options.IncludeNormals);
                options.IncludeVertexColors = Bool(values, "IncludeVertexColors", options.IncludeVertexColors);
                options.WeldVertices = Bool(values, "WeldVertices", options.WeldVertices);
                options.WeldTolerance = Num(values, "WeldTolerance", options.WeldTolerance);
                options.MinTriangleEdge = Num(values, "MinTriangleEdge", options.MinTriangleEdge);
                options.MaxVerticesPerPrimitive = (int)Num(values, "MaxVerticesPerPrimitive", options.MaxVerticesPerPrimitive);
                options.SkipHidden = Bool(values, "SkipHidden", options.SkipHidden);
                options.IncludeLines = Bool(values, "IncludeLines", options.IncludeLines);
                options.EmitMaterials = Bool(values, "EmitMaterials", options.EmitMaterials);
                options.PreserveTransparency = Bool(values, "PreserveTransparency", options.PreserveTransparency);
                options.DoubleSided = Bool(values, "DoubleSided", options.DoubleSided);
                options.DefaultRoughness = Num(values, "DefaultRoughness", options.DefaultRoughness);
                options.DefaultMetallic = Num(values, "DefaultMetallic", options.DefaultMetallic);
                options.UseComMaterials = Bool(values, "UseComMaterials", options.UseComMaterials);
                options.EmbedItemExtras = Bool(values, "EmbedItemExtras", options.EmbedItemExtras);
                options.ExportPropertiesSidecar = Bool(values, "ExportPropertiesSidecar", options.ExportPropertiesSidecar);
                options.ComBatchSize = (int)Num(values, "ComBatchSize", options.ComBatchSize);

                string filter = Str(values, "PropertyCategoryFilter", "");
                options.PropertyCategoryFilter.Clear();
                foreach (string category in filter.Split(';'))
                    if (!string.IsNullOrWhiteSpace(category)) options.PropertyCategoryFilter.Add(category.Trim());
            }
            catch (Exception ex)
            {
                Log.Debug("Could not load settings, using defaults: " + ex.Message);
                return new ExportOptions();
            }

            return options;
        }

        public static List<string> ListPresets()
        {
            var names = new List<string>();
            try
            {
                if (!Directory.Exists(PresetFolder)) return names;
                foreach (string file in Directory.GetFiles(PresetFolder, "*.txt"))
                    names.Add(Path.GetFileNameWithoutExtension(file));
            }
            catch (Exception) { }
            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }

        public static string PresetPath(string name)
        {
            return Path.Combine(PresetFolder, ExportOptions.SanitizeFileName(name) + ".txt");
        }

        private static string Str(Dictionary<string, string> values, string key, string fallback)
        {
            string value;
            return values.TryGetValue(key, out value) ? value : fallback;
        }

        private static bool Bool(Dictionary<string, string> values, string key, bool fallback)
        {
            string value;
            bool parsed;
            if (values.TryGetValue(key, out value) && bool.TryParse(value, out parsed)) return parsed;
            return fallback;
        }

        private static double Num(Dictionary<string, string> values, string key, double fallback)
        {
            string value;
            double parsed;
            if (values.TryGetValue(key, out value) &&
                double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
                return parsed;
            return fallback;
        }

        private static T Enum<T>(Dictionary<string, string> values, string key, T fallback) where T : struct
        {
            string value;
            if (!values.TryGetValue(key, out value) || string.IsNullOrWhiteSpace(value)) return fallback;

            try
            {
                return (T)System.Enum.Parse(typeof(T), value, true);
            }
            catch (Exception)
            {
                return fallback;
            }
        }
    }
}
