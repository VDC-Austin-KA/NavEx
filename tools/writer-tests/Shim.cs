// Minimal stand-ins for the Navisworks types the format writers touch, so the
// writer layer can be exercised on .NET 8. No Navisworks code paths run here.
namespace Autodesk.Navisworks.Api
{
    public enum Units { Meters, Centimeters, Millimeters, Feet, Inches, Yards, Kilometers, Miles, Micrometers, Mils, Microinches }
    public class Color { public double R, G, B; }
    public class ModelGeometry { public Color ActiveColor; public double ActiveTransparency; }
    public class ModelItem
    {
        public bool HasGeometry;
        public ModelGeometry Geometry;
        public ModelItem Parent;
        public ModelGeometry FindFirstGeometry() { return null; }
    }
}
namespace Autodesk.Navisworks.Api.Interop.ComApi
{
    public interface InwLVec3f { double data1 { get; } double data2 { get; } double data3 { get; } }
    public interface InwOaMaterial
    {
        InwLVec3f DiffuseColor { get; }
        InwLVec3f EmissiveColor { get; }
        double Shininess { get; }
        double transparency { get; }
    }
}
namespace NavEx.Core { public static class PluginInfo { public const string Name = "NavEx"; public const string Version = "1.0.0-test"; } }
