using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AcadPaletteSetXData.AutoCAD;
using AcadPaletteSetXData.Core;
using ACadSmartLeader.Core;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace ACadSmartLeader;

/// <summary>Run-local snapshots and spatial candidates. Searches layout records, including offscreen entities.</summary>
internal sealed class TargetResolver
{
    private readonly Transaction tr;
    private readonly Database db;
    private readonly SchemaSnapshot schema;
    private readonly AutoCadDataReader reader;
    private readonly Dictionary<ObjectId, EntityDataSnapshot> snapshots = new Dictionary<ObjectId, EntityDataSnapshot>();
    private readonly Dictionary<ObjectId, ObjectValues> values = new Dictionary<ObjectId, ObjectValues>();
    private readonly Dictionary<ObjectId, ObjectId[]> spaces = new Dictionary<ObjectId, ObjectId[]>();
    public TargetResolver(Transaction tr, Database db, SchemaSnapshot schema)
    {
        this.tr = tr; this.db = db; this.schema = schema;
        reader = new AutoCadDataReader(tr);
    }
    public EntityDataSnapshot Snapshot(Entity target)
    {
        if (!snapshots.TryGetValue(target.ObjectId, out var snapshot)) snapshots.Add(target.ObjectId, snapshot = reader.ReadEntity(target));
        return snapshot;
    }
    public ObjectValues Values(Entity target)
    {
        if (!values.TryGetValue(target.ObjectId, out var result)) values.Add(target.ObjectId, result = XDataV2.Read(Snapshot(target), schema));
        return result;
    }
    public IEnumerable<EntityDataSnapshot> Snapshots => snapshots.Values;
    public Entity? ByHandle(string handle, ObjectId owner)
    {
        if (!long.TryParse(handle, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var number) || number == 0 ||
            !db.TryGetObjectId(new Handle(number), out var id) || id.IsNull || id.IsErased) return null;
        var entity = tr.GetObject(id, OpenMode.ForRead) as Entity;
        return entity != null && entity.OwnerId == owner ? entity : null;
    }
    public Entity? Resolve(LeaderData data, Point3d arrow, ObjectId owner, double tolerance, out bool arrowOffTarget)
    {
        return TargetBinding.Resolve(ByHandle(data.Target, owner), saved => Distance(saved, arrow) <= tolerance,
            () => At(arrow, owner, tolerance), () => string.IsNullOrEmpty(data.TargetId) ? Enumerable.Empty<Entity>() :
                Objects(owner).Select(id => (Entity)tr.GetObject(id, OpenMode.ForRead))
                    .Where(e => string.Equals(Values(e).DeviceId, data.TargetId, StringComparison.OrdinalIgnoreCase)), out arrowOffTarget);
    }
    public Entity? At(Point3d arrow, ObjectId owner, double tolerance, IEnumerable<ObjectId>? visible = null)
    {
        var found = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<TargetCandidate>();
        var objects = Objects(owner);
        var filter = visible == null ? null : new HashSet<ObjectId>(visible);
        for (var order = 0; order < objects.Length; order++)
        {
            if (filter != null && !filter.Contains(objects[order])) continue;
            var entity = (Entity)tr.GetObject(objects[order], OpenMode.ForRead);
            if (!NearExtents(entity, arrow, tolerance) || Values(entity).Format != XDataFormat.V2) continue;
            var handle = entity.Handle.ToString(); found[handle] = entity;
            candidates.Add(new TargetCandidate(handle, Distance(entity, arrow), order));
        }
        var nearest = TargetGeometry.Nearest(candidates, tolerance);
        return nearest == null ? null : found[nearest.Handle];
    }
    private ObjectId[] Objects(ObjectId owner)
    {
        if (spaces.TryGetValue(owner, out var result)) return result;
        var space = (BlockTableRecord)tr.GetObject(owner, OpenMode.ForRead);
        var ids = space.Cast<ObjectId>().Where(id => !id.IsErased).ToArray();
        if (!space.DrawOrderTableId.IsNull)
        {
            var drawOrder = (DrawOrderTable)tr.GetObject(space.DrawOrderTableId, OpenMode.ForRead);
            ids = drawOrder.GetFullDrawOrder(0).Cast<ObjectId>().Where(id => !id.IsErased).ToArray();
        }
        spaces.Add(owner, ids); return ids;
    }
    private static bool NearExtents(Entity entity, Point3d point, double tolerance)
    {
        try { return BoxPoint(entity.GeometricExtents, point).DistanceTo(point) <= tolerance; }
        catch (Autodesk.AutoCAD.Runtime.Exception) { return true; }
    }
    public double Distance(Entity entity, Point3d point) => Closest(entity, point).DistanceTo(point);
    public Point3d Closest(Entity entity, Point3d point) => Closest(entity, point, Matrix3d.Identity, 0, new HashSet<ObjectId>());
    private Point3d Closest(Entity entity, Point3d point, Matrix3d transform, int depth, HashSet<ObjectId> path)
    {
        try
        {
            if (entity is Curve curve)
            {
                // Transform the actual curve, rather than transforming a local closest point. This also
                // handles nonuniformly scaled block inserts without choosing the wrong world-space point.
                using var world = curve.GetTransformedCopy(transform);
                if (world is Curve worldCurve) return worldCurve.GetClosestPointTo(point, false);
            }
            if (entity is BlockReference block && depth < 4 && path.Add(block.BlockTableRecord))
            {
                try
                {
                    var definition = (BlockTableRecord)tr.GetObject(block.BlockTableRecord, OpenMode.ForRead);
                    var points = new List<Point3d>();
                    foreach (ObjectId child in definition)
                    {
                        if (child.IsErased) continue;
                        var item = tr.GetObject(child, OpenMode.ForRead) as Entity;
                        if (item is Curve || item is BlockReference)
                            points.Add(Closest(item, point, transform * block.BlockTransform, depth + 1, path));
                    }
                    if (points.Count > 0) return points.OrderBy(p => p.DistanceTo(point)).First();
                }
                finally { path.Remove(block.BlockTableRecord); }
            }
            var extents = entity.GeometricExtents; extents.TransformBy(transform);
            return BoxPoint(extents, point);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception) { return new Point3d(double.MaxValue / 4, double.MaxValue / 4, double.MaxValue / 4); }
    }
    private static Point3d BoxPoint(Extents3d box, Point3d p)
    {
        var result = TargetGeometry.ClosestOnBox(new Point3(p.X, p.Y, p.Z),
            new Point3(box.MinPoint.X, box.MinPoint.Y, box.MinPoint.Z), new Point3(box.MaxPoint.X, box.MaxPoint.Y, box.MaxPoint.Z));
        return new Point3d(result.X, result.Y, result.Z);
    }
}
