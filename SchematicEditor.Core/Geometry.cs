namespace SchematicEditor.Core;

/// <summary>2D vector / point in world units. Grid pitch is 5 units.</summary>
public readonly record struct Vec2(double X, double Y)
{
    public static Vec2 Zero => new(0, 0);

    public static Vec2 operator +(Vec2 a, Vec2 b) => new(a.X + b.X, a.Y + b.Y);
    public static Vec2 operator -(Vec2 a, Vec2 b) => new(a.X - b.X, a.Y - b.Y);
    public static Vec2 operator *(Vec2 a, double k) => new(a.X * k, a.Y * k);

    public double Length => Math.Sqrt(X * X + Y * Y);

    public double DistanceTo(Vec2 other) => (this - other).Length;

    /// <summary>Snap to the nearest grid node.</summary>
    public Vec2 Snap(double grid) =>
        new(Math.Round(X / grid) * grid, Math.Round(Y / grid) * grid);

    /// <summary>Integer key for exact-coincidence tests (0.1 unit resolution).</summary>
    public (long, long) Key() => ((long)Math.Round(X * 10.0), (long)Math.Round(Y * 10.0));

    /// <summary>Distance from this point to segment [a, b].</summary>
    public double DistanceToSegment(Vec2 a, Vec2 b)
    {
        Vec2 ab = b - a;
        double len2 = ab.X * ab.X + ab.Y * ab.Y;
        if (len2 < 1e-12) return DistanceTo(a);
        double t = ((X - a.X) * ab.X + (Y - a.Y) * ab.Y) / len2;
        t = Math.Clamp(t, 0.0, 1.0);
        return DistanceTo(new Vec2(a.X + ab.X * t, a.Y + ab.Y * t));
    }

    /// <summary>True if the point lies on segment [a, b] within tolerance.</summary>
    public bool IsOnSegment(Vec2 a, Vec2 b, double tol = 0.05) =>
        DistanceToSegment(a, b) <= tol;
}

/// <summary>Axis-aligned rectangle.</summary>
public readonly record struct Rect2(double MinX, double MinY, double MaxX, double MaxY)
{
    public static Rect2 Empty => new(double.MaxValue, double.MaxValue, double.MinValue, double.MinValue);

    public double Width => MaxX - MinX;
    public double Height => MaxY - MinY;
    public Vec2 Center => new((MinX + MaxX) * 0.5, (MinY + MaxY) * 0.5);
    public bool IsEmpty => MaxX < MinX || MaxY < MinY;

    public Rect2 Include(Vec2 p) => new(
        Math.Min(MinX, p.X), Math.Min(MinY, p.Y),
        Math.Max(MaxX, p.X), Math.Max(MaxY, p.Y));

    public Rect2 Union(Rect2 r) => new(
        Math.Min(MinX, r.MinX), Math.Min(MinY, r.MinY),
        Math.Max(MaxX, r.MaxX), Math.Max(MaxY, r.MaxY));

    public Rect2 Inflate(double d) => new(MinX - d, MinY - d, MaxX + d, MaxY + d);

    public bool Contains(Vec2 p) => p.X >= MinX && p.X <= MaxX && p.Y >= MinY && p.Y <= MaxY;

    public bool Intersects(Rect2 r) =>
        MinX <= r.MaxX && MaxX >= r.MinX && MinY <= r.MaxY && MaxY >= r.MinY;

    public static Rect2 FromPoints(Vec2 a, Vec2 b) => new(
        Math.Min(a.X, b.X), Math.Min(a.Y, b.Y),
        Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
}

/// <summary>Rotation in 90-degree steps, clockwise.</summary>
public enum Rotation { R0 = 0, R90 = 1, R180 = 2, R270 = 3 }

public static class Transform2
{
    /// <summary>Apply mirror (X axis flip) then rotation, then translation.</summary>
    public static Vec2 Apply(Vec2 local, Rotation rot, bool mirror, Vec2 origin)
    {
        double x = mirror ? -local.X : local.X;
        double y = local.Y;
        (x, y) = rot switch
        {
            Rotation.R90 => (-y, x),
            Rotation.R180 => (-x, -y),
            Rotation.R270 => (y, -x),
            _ => (x, y)
        };
        return new Vec2(x + origin.X, y + origin.Y);
    }

    /// <summary>Rotate an angle (degrees) by the instance transform. Used for arcs/text.</summary>
    public static double ApplyAngle(double deg, Rotation rot, bool mirror)
    {
        if (mirror) deg = 180.0 - deg;
        return deg + (int)rot * 90.0;
    }
}
