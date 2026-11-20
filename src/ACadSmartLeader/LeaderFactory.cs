using System;
using System.Linq;
using ACadSmartLeader.Core;
using AcadPaletteSetXData.Core;
using AcadPaletteSetXData.AutoCAD;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace ACadSmartLeader;

internal static class LeaderFactory
{
    public static ObjectId Style(Transaction tr, Database db, LabelRule rule, Action<string> warn)
    {
        if (rule.Style.Length == 0) return db.MLeaderstyle;
        var styles = (DBDictionary)tr.GetObject(db.MLeaderStyleDictionaryId, OpenMode.ForRead);
        if (styles.Contains(rule.Style)) return styles.GetAt(rule.Style);
        warn("Стиль «" + rule.Style + "» не найден; используется текущий."); return db.MLeaderstyle;
    }
    public static MLeader Create(Transaction tr, Database db, ObjectId owner, Point3d arrow, Point3d landing,
        LabelRule rule, LabelText text, LeaderData data, Action<string> warn)
    {
        var leader = new MLeader();
        try
        {
            leader.SetDatabaseDefaults(db);
            leader.MLeaderStyle = Style(tr, db, rule, warn);
            leader.ContentType = ContentType.MTextContent;
            leader.EnableLanding = true; leader.EnableDogleg = true;
            if (rule.Layer.Length != 0)
            {
                var layers = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                if (!layers.Has(rule.Layer))
                {
                    tr.GetObject(db.LayerTableId, OpenMode.ForWrite);
                    var layer = new LayerTableRecord { Name = rule.Layer }; layers.Add(layer); tr.AddNewlyCreatedDBObject(layer, true);
                }
                leader.LayerId = layers[rule.Layer];
            }
            var layerRecord = (LayerTableRecord)tr.GetObject(leader.LayerId, OpenMode.ForRead);
            if (layerRecord.IsLocked) throw new InvalidOperationException("Слой выноски заблокирован: " + layerRecord.Name);
            using var mtext = new MText();
            mtext.SetDatabaseDefaults(db); mtext.Contents = text.MText;
            mtext.TextHeight = leader.TextHeight; mtext.TextStyleId = leader.TextStyleId;
            leader.MText = mtext; leader.TextLocation = landing;
            SetAttachment(leader, text);
            // AutoCAD connects this arrow to the landing of the positioned MText.
            leader.AddLeaderLine(arrow);
            var space = (BlockTableRecord)tr.GetObject(owner, OpenMode.ForWrite);
            space.AppendEntity(leader); tr.AddNewlyCreatedDBObject(leader, true);
            leader.SetFromStyle(); // Explicitly inherit the style's annotative status.
            if (leader.Annotative == AnnotativeStates.True)
            {
                var context = db.ObjectContextManager.GetContextCollection("ACDB_ANNOTATIONSCALES").CurrentContext;
                if (context != null && !leader.HasContext(context)) leader.AddContext(context);
            }
            data.Text = LeaderData.TextStamp(text.MText); data.State = text.Errors.Count > 0 ? LeaderState.FormatError : LeaderState.Bound;
            WriteData(tr, db, leader, data);
            return leader;
        }
        catch { if (leader.ObjectId.IsNull) leader.Dispose(); throw; }
    }
    public static void SetAttachment(MLeader leader, LabelText text)
    {
        var attachment = text.HasTop && text.HasBottom ? TextAttachmentType.AttachmentBottomOfTopLine : TextAttachmentType.AttachmentBottomLine;
        leader.SetTextAttachmentType(attachment, LeaderDirectionType.LeftLeader);
        leader.SetTextAttachmentType(attachment, LeaderDirectionType.RightLeader);
    }
    public static string Text(MLeader leader) { using var text = leader.MText; return text?.Contents ?? ""; }
    public static bool TryData(Entity leader, out LeaderData? data, out string? error)
    {
        using var buffer = leader.GetXDataForApplication(LeaderData.RegApp);
        return LeaderData.TryRead(AutoCadDataReader.Copy(buffer, new System.Collections.Generic.List<string>()), out data, out error);
    }
    public static void WriteData(Transaction tr, Database db, Entity leader, LeaderData data)
    {
        var app = (RegAppTable)tr.GetObject(db.RegAppTableId, OpenMode.ForRead);
        if (!app.Has(LeaderData.RegApp))
        {
            tr.GetObject(db.RegAppTableId, OpenMode.ForWrite);
            var record = new RegAppTableRecord { Name = LeaderData.RegApp }; app.Add(record); tr.AddNewlyCreatedDBObject(record, true);
        }
        var section = data.Write();
        using var current = leader.XData;
        var others = AutoCadDataReader.Copy(current, new System.Collections.Generic.List<string>());
        var skip = false;
        var combined = new System.Collections.Generic.List<DataValue>();
        foreach (var value in others)
        {
            if (value.TypeCode == 1001) skip = string.Equals(value.Value as string, LeaderData.RegApp, StringComparison.OrdinalIgnoreCase);
            if (!skip) combined.Add(value);
        }
        combined.AddRange(section);
        if (XDataV2.EstimateSize(combined) > XDataV2.MaxXDataBytes) throw new FormatException("Общий размер XDATA выноски больше 16 КБ.");
        using var buffer = new ResultBuffer(section.Select(v => new TypedValue(v.TypeCode, v.Value)).ToArray());
        leader.XData = buffer;
    }
}
