using System;

namespace NavEx.Core
{
    /// <summary>
    /// Double-precision 3-vector. Everything upstream of the final float32 write is
    /// done in doubles: Navisworks world coordinates are frequently survey-grid
    /// values (millions of feet from origin) where float32 has metre-scale error.
    /// The recentring step in <see cref="ExportOptions"/> is what makes float32
    /// output safe; until then we stay in doubles.
    /// </summary>
    public struct Vec3
    {
        public double X;
        public double Y;
        public double Z;

        public Vec3(double x, double y, double z) { X = x; Y = y; Z = z; }

        public double Length { get { return Math.Sqrt(X * X + Y * Y + Z * Z); } }

        public static Vec3 operator +(Vec3 a, Vec3 b) { return new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z); }
        public static Vec3 operator -(Vec3 a, Vec3 b) { return new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z); }
        public static Vec3 operator *(Vec3 a, double s) { return new Vec3(a.X * s, a.Y * s, a.Z * s); }

        public static Vec3 Cross(Vec3 a, Vec3 b)
        {
            return new Vec3(
                a.Y * b.Z - a.Z * b.Y,
                a.Z * b.X - a.X * b.Z,
                a.X * b.Y - a.Y * b.X);
        }

        public Vec3 Normalized()
        {
            double len = Length;
            if (len < 1e-12) return new Vec3(0, 0, 0);
            return new Vec3(X / len, Y / len, Z / len);
        }

        public override string ToString()
        {
            return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "({0:0.###}, {1:0.###}, {2:0.###})", X, Y, Z);
        }
    }

    /// <summary>
    /// Running axis-aligned bounds in double precision.
    /// </summary>
    internal class Bounds
    {
        public Vec3 Min = new Vec3(double.MaxValue, double.MaxValue, double.MaxValue);
        public Vec3 Max = new Vec3(double.MinValue, double.MinValue, double.MinValue);

        public bool IsEmpty { get { return Min.X > Max.X; } }

        public Vec3 Center
        {
            get
            {
                if (IsEmpty) return new Vec3(0, 0, 0);
                return new Vec3((Min.X + Max.X) * 0.5, (Min.Y + Max.Y) * 0.5, (Min.Z + Max.Z) * 0.5);
            }
        }

        public Vec3 Size
        {
            get { return IsEmpty ? new Vec3(0, 0, 0) : Max - Min; }
        }

        public void Add(Vec3 p)
        {
            if (p.X < Min.X) Min.X = p.X;
            if (p.Y < Min.Y) Min.Y = p.Y;
            if (p.Z < Min.Z) Min.Z = p.Z;
            if (p.X > Max.X) Max.X = p.X;
            if (p.Y > Max.Y) Max.Y = p.Y;
            if (p.Z > Max.Z) Max.Z = p.Z;
        }

        public void Add(Bounds other)
        {
            if (other == null || other.IsEmpty) return;
            Add(other.Min);
            Add(other.Max);
        }
    }

    /// <summary>
    /// 4x4 transform stored the way the Navisworks COM API hands it to us: a flat
    /// 16-element array in column-major (OpenGL) order, so element [c*4+r] is
    /// row r, column c.
    /// </summary>
    internal class Matrix4
    {
        public readonly double[] M;

        public Matrix4(double[] m) { M = m; }

        public static Matrix4 Identity()
        {
            return new Matrix4(new double[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 });
        }

        /// <summary>Full projective transform of a point.</summary>
        public Vec3 TransformPoint(double x, double y, double z)
        {
            double w = M[3] * x + M[7] * y + M[11] * z + M[15];
            if (w == 0.0 || double.IsNaN(w)) w = 1.0;
            return new Vec3(
                (M[0] * x + M[4] * y + M[8] * z + M[12]) / w,
                (M[1] * x + M[5] * y + M[9] * z + M[13]) / w,
                (M[2] * x + M[6] * y + M[10] * z + M[14]) / w);
        }

        /// <summary>Determinant of the upper-left 3x3 block; negative means the transform mirrors.</summary>
        public double Linear3x3Determinant()
        {
            double a = M[0], b = M[4], c = M[8];
            double d = M[1], e = M[5], f = M[9];
            double g = M[2], h = M[6], i = M[10];
            return a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);
        }

        /// <summary>
        /// Inverse-transpose of the upper-left 3x3, which is the correct matrix for
        /// transforming normals under non-uniform scale. Returns the plain linear
        /// block when the matrix is singular.
        /// </summary>
        public double[] NormalMatrix()
        {
            double a = M[0], b = M[4], c = M[8];
            double d = M[1], e = M[5], f = M[9];
            double g = M[2], h = M[6], i = M[10];

            double det = a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);
            if (Math.Abs(det) < 1e-20)
                return new double[] { a, b, c, d, e, f, g, h, i };

            double inv = 1.0 / det;
            // adjugate(M)^T / det == (M^-1)^T
            return new double[]
            {
                (e * i - f * h) * inv, (f * g - d * i) * inv, (d * h - e * g) * inv,
                (c * h - b * i) * inv, (a * i - c * g) * inv, (b * g - a * h) * inv,
                (b * f - c * e) * inv, (c * d - a * f) * inv, (a * e - b * d) * inv
            };
        }

        public static Vec3 ApplyNormalMatrix(double[] n, double x, double y, double z)
        {
            return new Vec3(
                n[0] * x + n[1] * y + n[2] * z,
                n[3] * x + n[4] * y + n[5] * z,
                n[6] * x + n[7] * y + n[8] * z);
        }
    }
}
