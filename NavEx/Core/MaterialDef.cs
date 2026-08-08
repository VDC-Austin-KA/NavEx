using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.Navisworks.Api;
using ComApi = Autodesk.Navisworks.Api.Interop.ComApi;

namespace NavEx.Core
{
    /// <summary>
    /// A resolved appearance, already in glTF metallic-roughness terms.
    /// </summary>
    public class MaterialDef
    {
        public string Name;
        public double R = 0.75, G = 0.75, B = 0.75;
        public double Alpha = 1.0;
        public double Metallic;
        public double Roughness = 0.85;
        public double EmissiveR, EmissiveG, EmissiveB;
        public bool DoubleSided = true;

        public bool IsTransparent { get { return Alpha < 0.999; } }

        public string Key
        {
            get
            {
                // Quantise so near-identical appearances collapse into one material.
                return string.Format(CultureInfo.InvariantCulture,
                    "{0:0.###}|{1:0.###}|{2:0.###}|{3:0.###}|{4:0.##}|{5:0.##}|{6:0.##}|{7:0.##}|{8:0.##}|{9}",
                    R, G, B, Alpha, Metallic, Roughness, EmissiveR, EmissiveG, EmissiveB, DoubleSided ? 1 : 0);
            }
        }

        public string SuggestName()
        {
            int r = (int)Math.Round(R * 255), g = (int)Math.Round(G * 255), b = (int)Math.Round(B * 255);
            string baseName = string.Format(CultureInfo.InvariantCulture, "mat_{0:X2}{1:X2}{2:X2}", r, g, b);
            if (IsTransparent)
                baseName += "_a" + ((int)Math.Round(Alpha * 100)).ToString(CultureInfo.InvariantCulture);
            return baseName;
        }
    }

    /// <summary>
    /// Turns Navisworks appearances into a de-duplicated material palette.
    ///
    /// The primary source is <c>ModelItem.Geometry.ActiveColor</c> / <c>ActiveTransparency</c>,
    /// which is what Navisworks actually renders — it already folds in the source
    /// file's material, any appearance profile, and any user colour override. When
    /// the leaf item carries no geometry colour we walk up its ancestors, which is
    /// how layer- and block-level colours reach their children.
    ///
    /// The COM path (<c>InwOaFragment3.Appearance</c>) additionally exposes specular
    /// and shininess, so it produces slightly better roughness values, but it is not
    /// present on every fragment and throws on some geometry types — hence opt-in
    /// via <see cref="ExportOptions.UseComMaterials"/> and fully guarded.
    /// </summary>
    public class MaterialResolver
    {
        private readonly ExportOptions _options;
        private readonly Dictionary<string, int> _byKey = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly List<MaterialDef> _materials = new List<MaterialDef>();
        private readonly Dictionary<int, int> _byItemHash = new Dictionary<int, int>();

        public MaterialResolver(ExportOptions options)
        {
            _options = options;
        }

        public IList<MaterialDef> Materials { get { return _materials; } }

        private int _defaultIndex = -1;

        /// <summary>Index of the fallback material used when nothing resolves.</summary>
        public int DefaultMaterialIndex
        {
            get
            {
                if (_defaultIndex >= 0) return _defaultIndex;
                var def = new MaterialDef
                {
                    R = 0.75,
                    G = 0.75,
                    B = 0.75,
                    Alpha = 1.0,
                    Metallic = _options.DefaultMetallic,
                    Roughness = _options.DefaultRoughness,
                    DoubleSided = _options.DoubleSided
                };
                _defaultIndex = Intern(def);
                return _defaultIndex;
            }
        }

        public int Resolve(ModelItem item, object comAppearance)
        {
            if (!_options.EmitMaterials)
                return DefaultMaterialIndex;

            if (item != null)
            {
                int hash = item.InstanceHashCode;
                int cached;
                if (_byItemHash.TryGetValue(hash, out cached))
                    return cached;

                MaterialDef def = null;
                if (_options.UseComMaterials)
                    def = FromComAppearance(comAppearance);
                if (def == null)
                    def = FromModelItem(item);

                int index = def == null ? DefaultMaterialIndex : Intern(def);
                _byItemHash[hash] = index;
                return index;
            }

            return DefaultMaterialIndex;
        }

        private int Intern(MaterialDef def)
        {
            string key = def.Key;
            int existing;
            if (_byKey.TryGetValue(key, out existing))
                return existing;

            def.Name = def.SuggestName();
            // Names must be unique enough to be useful in a DCC outliner.
            string candidate = def.Name;
            int suffix = 1;
            while (_materials.Exists(m => string.Equals(m.Name, candidate, StringComparison.Ordinal)))
                candidate = def.Name + "_" + (++suffix).ToString(CultureInfo.InvariantCulture);
            def.Name = candidate;

            _materials.Add(def);
            int index = _materials.Count - 1;
            _byKey[key] = index;
            return index;
        }

        private MaterialDef FromModelItem(ModelItem item)
        {
            ModelGeometry geometry = null;
            ModelItem cursor = item;
            int guard = 0;

            while (cursor != null && guard++ < 64)
            {
                try
                {
                    if (cursor.HasGeometry && cursor.Geometry != null)
                    {
                        geometry = cursor.Geometry;
                        break;
                    }
                }
                catch (Exception)
                {
                    // Some proxy items throw when Geometry is touched; keep walking.
                }
                cursor = cursor.Parent;
            }

            if (geometry == null)
            {
                try { geometry = item.FindFirstGeometry(); }
                catch (Exception) { geometry = null; }
            }

            if (geometry == null) return null;

            var def = new MaterialDef
            {
                Metallic = _options.DefaultMetallic,
                Roughness = _options.DefaultRoughness,
                DoubleSided = _options.DoubleSided
            };

            try
            {
                Color color = geometry.ActiveColor;
                if (color != null)
                {
                    def.R = Clamp01(color.R);
                    def.G = Clamp01(color.G);
                    def.B = Clamp01(color.B);
                }
            }
            catch (Exception) { }

            try
            {
                double transparency = geometry.ActiveTransparency;
                def.Alpha = _options.PreserveTransparency ? Clamp01(1.0 - transparency) : 1.0;
            }
            catch (Exception) { }

            return def;
        }

        private MaterialDef FromComAppearance(object appearance)
        {
            var material = appearance as ComApi.InwOaMaterial;
            if (material == null) return null;

            try
            {
                var def = new MaterialDef
                {
                    Metallic = _options.DefaultMetallic,
                    DoubleSided = _options.DoubleSided
                };

                ComApi.InwLVec3f diffuse = material.DiffuseColor;
                if (diffuse != null)
                {
                    def.R = Clamp01(diffuse.data1);
                    def.G = Clamp01(diffuse.data2);
                    def.B = Clamp01(diffuse.data3);
                }

                ComApi.InwLVec3f emissive = material.EmissiveColor;
                if (emissive != null)
                {
                    def.EmissiveR = Clamp01(emissive.data1);
                    def.EmissiveG = Clamp01(emissive.data2);
                    def.EmissiveB = Clamp01(emissive.data3);
                }

                def.Alpha = _options.PreserveTransparency ? Clamp01(1.0 - material.transparency) : 1.0;

                // Navisworks shininess is a 0..1 Phong-ish gloss; map it onto a
                // perceptual roughness the same way glTF's spec-gloss conversion does.
                double shininess = Clamp01(material.Shininess);
                def.Roughness = Clamp(1.0 - shininess, 0.02, 1.0);

                return def;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static double Clamp01(double v) { return Clamp(v, 0.0, 1.0); }

        private static double Clamp(double v, double lo, double hi)
        {
            if (double.IsNaN(v)) return lo;
            return v < lo ? lo : (v > hi ? hi : v);
        }
    }
}
