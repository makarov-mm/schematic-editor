namespace SchematicEditor.Core;

/// <summary>A point or vector in world units, Y down. The grid pitch is <see cref="SchematicDocument.Grid"/>.</summary>
public readonly record struct Vec2(double X, double Y)
{
    public static Vec2 Zero => default;

    public static Vec2 operator +(Vec2 a, Vec2 b) => new(a.X + b.X, a.Y + b.Y);
    public static Vec2 operator -(Vec2 a, Vec2 b) => new(a.X - b.X, a.Y - b.Y);
    public static Vec2 operator -(Vec2 v) => new(-v.X, -v.Y);
    public static Vec2 operator *(Vec2 v, double k) => new(v.X * k, v.Y * k);

    public double Length => Math.Sqrt(X * X + Y * Y);

    /// <summary>The point quantized to 0.1 units: points with equal keys coincide.</summary>
    public PointKey Key => new((long)Math.Round(X * 10.0), (long)Math.Round(Y * 10.0));

    public double DistanceTo(Vec2 other) => (this - other).Length;

    /// <summary>The nearest grid node.</summary>
    public Vec2 Snap(double grid) => new(Math.Round(X / grid) * grid, Math.Round(Y / grid) * grid);

    public double DistanceToSegment(Vec2 a, Vec2 b)
    {
        Vec2 ab = b - a;
        double length2 = ab.X * ab.X + ab.Y * ab.Y;
        if (length2 < 1e-12)
            return DistanceTo(a);

        double t = Math.Clamp(((X - a.X) * ab.X + (Y - a.Y) * ab.Y) / length2, 0.0, 1.0);
        return DistanceTo(new Vec2(a.X + ab.X * t, a.Y + ab.Y * t));
    }

    public bool IsOnSegment(Vec2 a, Vec2 b, double tolerance = 0.05) => DistanceToSegment(a, b) <= tolerance;
}

/// <summary>A point quantized to 0.1 units, for coincidence tests and hashing.</summary>
public readonly record struct PointKey(long X, long Y);

/// <summary>An axis-aligned rectangle.</summary>
public readonly record struct Rect2(double MinX, double MinY, double MaxX, double MaxY)
{
    /// <summary>Contains nothing; including a point or a rectangle gives exactly that.</summary>
    public static Rect2 Empty => new(double.MaxValue, double.MaxValue, double.MinValue, double.MinValue);

    public double Width => MaxX - MinX;
    public double Height => MaxY - MinY;
    public Vec2 Center => new((MinX + MaxX) * 0.5, (MinY + MaxY) * 0.5);
    public bool IsEmpty => MaxX < MinX || MaxY < MinY;

    public static Rect2 FromPoints(Vec2 a, Vec2 b) =>
        new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));

    public Rect2 Include(Vec2 p) =>
        new(Math.Min(MinX, p.X), Math.Min(MinY, p.Y), Math.Max(MaxX, p.X), Math.Max(MaxY, p.Y));

    public Rect2 Union(Rect2 r) =>
        new(Math.Min(MinX, r.MinX), Math.Min(MinY, r.MinY), Math.Max(MaxX, r.MaxX), Math.Max(MaxY, r.MaxY));

    public Rect2 Inflate(double d) => new(MinX - d, MinY - d, MaxX + d, MaxY + d);

    public bool Contains(Vec2 p) => p.X >= MinX && p.X <= MaxX && p.Y >= MinY && p.Y <= MaxY;

    public bool Intersects(Rect2 r) => MinX <= r.MaxX && MaxX >= r.MinX && MinY <= r.MaxY && MaxY >= r.MinY;
}

/// <summary>Rotation in quarter turns, clockwise on screen.</summary>
public enum Rotation { R0 = 0, R90 = 1, R180 = 2, R270 = 3 }

public static class RotationExtensions
{
    extension(Rotation rotation)
    {
        /// <summary>A quarter turn further clockwise.</summary>
        public Rotation Next => (Rotation)(((int)rotation + 1) & 3);

        /// <summary>A quarter turn back, counterclockwise.</summary>
        public Rotation Previous => (Rotation)(((int)rotation + 3) & 3);
    }
}
