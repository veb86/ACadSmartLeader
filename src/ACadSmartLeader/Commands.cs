using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using AcadPaletteSetXData.AutoCAD;
using AcadPaletteSetXData.Core;
using ACadSmartLeader.Core;
using ACadSmartLeader.UI;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using Exception = System.Exception;

[assembly: CommandClass(typeof(ACadSmartLeader.Commands))]
namespace ACadSmartLeader;

public sealed class Commands
{
    internal static bool Dark => Convert.ToInt32(AcApplication.GetSystemVariable("COLORTHEME"), CultureInfo.InvariantCulture) == 0;
    private static Document Doc => AcApplication.DocumentManager.MdiActiveDocument;
    internal static void Execute(Action<Document> action)
    {
        var document = Doc; if (document == null) return;
        try { action(document); }
        catch (Exception e) when (e is Autodesk.AutoCAD.Runtime.Exception || e is InvalidOperationException || e is FormatException || e is IOException || e is UnauthorizedAccessException || e is ArgumentException)
        {
            document.Editor.WriteMessage("\nACadSmartLeader: " + e.Message);
            Plugin.For(document).Trace(e.ToString());
        }
    }
    [CommandMethod("SL_CREATE", CommandFlags.Modal)]
    public void Create() => CreateLeader(group: false);

    [CommandMethod("SL_CREATE_GROUP", CommandFlags.Modal)]
    public void CreateGroup() => CreateLeader(group: true);

    private static void CreateLeader(bool group) => Execute(document => {
        var ed = document.Editor; var db = document.Database;
        using var tr = db.TransactionManager.StartTransaction();
        var rules = NodLabelStore.Read(tr, db, out var stored); var schema = Plugin.For(document).Schema.Get();
        var ruleId = "";
        var options = new PromptPointOptions(group
            ? "\nТочка стрелки (привязка Ближайшая): "
            : "\nТочка стрелки (привязка Ближайшая) [Rule]: ");
        if (!group) options.Keywords.Add("Rule");
        PromptPointResult point;
        while ((point = ed.GetPoint(options)).Status == PromptStatus.Keyword)
        {
            ed.WriteMessage("\n" + string.Join("\n", rules.Rules.Select(r => r.Id + ": " + r.Caption)));
            var selected = ed.GetString(new PromptStringOptions("\nId правила (пусто — автоматически): ") { AllowSpaces = false });
            if (selected.Status == PromptStatus.Cancel) return;
            ruleId = selected.StringResult ?? "";
            if (ruleId.Length > 0 && !rules.Rules.Any(r => string.Equals(r.Id, ruleId, StringComparison.OrdinalIgnoreCase)))
            { ed.WriteMessage("\nПравило не найдено."); ruleId = ""; }
        }
        if (point.Status != PromptStatus.OK) return;
        var arrow = point.Value.TransformBy(ed.CurrentUserCoordinateSystem);
        var resolver = new TargetResolver(tr, db, schema);
        var style = (MLeaderStyle)tr.GetObject(db.MLeaderstyle, OpenMode.ForRead);
        var tolerance = TargetGeometry.Tolerance(style.TextHeight, Plugin.Settings.Tolerance);
        var delta = new Vector3d(tolerance, tolerance, 0);
        var crossing = ed.SelectCrossingWindow(arrow - delta, arrow + delta,
            new SelectionFilter(new[] { new TypedValue((int)DxfCode.ExtendedDataRegAppName, DrawingNames.RegApp) }));
        var target = crossing.Status == PromptStatus.OK ? resolver.At(arrow, db.CurrentSpaceId, tolerance, crossing.Value.GetObjectIds()) : null;
        target ??= resolver.At(arrow, db.CurrentSpaceId, tolerance); // Offscreen / failed graphics selection.
        if (target == null)
        {
            target = PickTarget(document, tr, db.CurrentSpaceId, true);
            if (target == null) return;
            arrow = resolver.Closest(target, arrow);
        }
        var values = resolver.Values(target);
        var entityType = target.GetType().Name;
        var rule = group ? rules.SelectGroup(values.Template, entityType) : rules.Select(ruleId, values.Template, entityType);
        if (rule == null)
        {
            ed.WriteMessage((group ? "\nНет группового правила с суффиксом _GROUP для шаблона " : "\nНет правила для шаблона ") + values.Template);
            return;
        }
        var label = Plugin.For(document).Evaluator(schema, rules.Rules, new[] { resolver.Snapshot(target) }).Evaluate(resolver.Snapshot(target), rule);
        foreach (var error in label.Errors) ed.WriteMessage("\n" + error);
        if (schema.Schema == null) ed.WriteMessage("\nВ чертеже нет схемы VELEC: значения без справочников и точности.");
        var data = new LeaderData { Target = target.Handle.ToString(), TargetId = values.DeviceId ?? "", Rule = group ? rule.Id : ruleId };
        var landing = arrow + new Vector3d(Math.Max(1, style.TextHeight) * 8, Math.Max(1, style.TextHeight) * 4, 0);
        using var leader = LeaderFactory.Create(tr, db, db.CurrentSpaceId, arrow, landing, rule, label, data, message => ed.WriteMessage("\n" + message));
        var drag = ed.Drag(new LandingJig(leader));
        if (drag.Status != PromptStatus.OK) return;
        if (!stored) NodLabelStore.Write(tr, db, rules);
        tr.Commit();
    });

    [CommandMethod("SL_UPDATE", CommandFlags.Modal | CommandFlags.UsePickSet)]
    public void Update() => Execute(document => {
        var ed = document.Editor;
        var implied = ed.SelectImplied();
        IEnumerable<ObjectId>? selected = implied.Status == PromptStatus.OK ? implied.Value.GetObjectIds() : null;
        ed.SetImpliedSelection(Array.Empty<ObjectId>());
        var force = false;
        while (true)
        {
            var options = new PromptKeywordOptions("\nОбновить [All/Select/Force] <" + (selected == null ? "All" : "Select") + ">: ") { AllowNone = true };
            options.Keywords.Add("All"); options.Keywords.Add("Select"); options.Keywords.Add("Force");
            var result = ed.GetKeywords(options);
            if (result.Status == PromptStatus.Cancel) return;
            if (result.StringResult == "Force") { force = true; continue; }
            if (result.StringResult == "All") selected = null;
            if (result.StringResult == "Select")
            { var pick = ed.GetSelection(LeaderFilter()); if (pick.Status != PromptStatus.OK) return; selected = pick.Value.GetObjectIds(); }
            break;
        }
        LeaderUpdater.Run(document, selected, force);
    });

    [CommandMethod("SL_SELECT_PROBLEMS", CommandFlags.Modal | CommandFlags.UsePickSet)]
    public void SelectProblems() => Execute(document => {
        using var tr = document.Database.TransactionManager.StartOpenCloseTransaction();
        var selected = LeaderUpdater.All(tr, document.Database).Where(id => {
            var leader = (MLeader)tr.GetObject(id, OpenMode.ForRead);
            if (!LeaderFactory.TryData(leader, out var data, out var error)) return error != null;
            return data!.State != LeaderState.Bound || !data.Matches(LeaderFactory.Text(leader));
        }).Where(id => ((Entity)tr.GetObject(id, OpenMode.ForRead)).OwnerId == document.Database.CurrentSpaceId).ToArray();
        document.Editor.SetImpliedSelection(selected); tr.Commit();
        document.Editor.WriteMessage("\nВыбрано проблемных выносок в текущем пространстве: " + selected.Length);
    });

    [CommandMethod("SL_SETTINGS", CommandFlags.Modal)]
    public void Settings() => Execute(_ => {
        var settings = Plugin.Settings;
        var window = new SettingsWindow(settings.UpdateOnRegen, settings.Tolerance, Dark);
        if (AcApplication.ShowModalWindow(window) == true)
        { settings.UpdateOnRegen = window.Mode; settings.Tolerance = window.Tolerance; settings.Save(); Plugin.ClearPending(); }
    });

    [CommandMethod("SL_RULES", CommandFlags.Modal)]
    public void Rules() => Execute(document => {
        using var tr = document.Database.TransactionManager.StartTransaction();
        var rules = NodLabelStore.Read(tr, document.Database, out _); var schema = Plugin.For(document).Schema.Get();
        var reader = new AutoCadDataReader(tr);
        EntityDataSnapshot? preview = null;
        while (true)
        {
            var window = new RulesWindow(rules, schema, preview, Dark);
            var saved = AcApplication.ShowModalWindow(window) == true;
            rules = window.Result;
            if (saved) { NodLabelStore.Write(tr, document.Database, rules); tr.Commit(); return; }
            if (!window.PickTarget) return;
            var target = PickTarget(document, tr, document.Database.CurrentSpaceId, false);
            if (target != null) preview = reader.ReadEntity(target);
        }
    });

    [CommandMethod("SL_EDIT", CommandFlags.Modal | CommandFlags.UsePickSet)]
    public void Edit() => Execute(document => {
        var selected = document.Editor.GetEntity("\nВыноска: "); if (selected.Status != PromptStatus.OK) return;
        using var tr = document.Database.TransactionManager.StartTransaction();
        if (!(tr.GetObject(selected.ObjectId, OpenMode.ForRead) is MLeader leader) || !LeaderFactory.TryData(leader, out var data, out _))
        { document.Editor.WriteMessage("\nВыберите выноску BobrovLeader."); return; }
        if (leader.ContentType != ContentType.MTextContent) throw new InvalidOperationException("Требуется содержимое MText.");
        if (((LayerTableRecord)tr.GetObject(leader.LayerId, OpenMode.ForRead)).IsLocked) throw new InvalidOperationException("Слой выноски заблокирован.");
        var schema = Plugin.For(document).Schema.Get(); var rules = NodLabelStore.Read(tr, document.Database, out _);
        var resolver = new TargetResolver(tr, document.Database, schema);
        if (!LeaderUpdater.Arrow(leader, out var arrow)) throw new InvalidOperationException("Требуется одна стрелка.");
        var target = resolver.Resolve(data!, arrow, leader.OwnerId, TargetGeometry.Tolerance(leader.TextHeight, Plugin.Settings.Tolerance), out _);
        var edit = data!;
        Point3d? rebindPoint = null;
        while (true)
        {
            var window = new EditWindow(edit, rules, schema, target == null ? null : resolver.Snapshot(target), Dark);
            if (AcApplication.ShowModalWindow(window) == true) { edit = window.Result; break; }
            if (!window.Rebind) return;
            edit = window.Result;
            var picked = PickTarget(document, tr, leader.OwnerId, true);
            if (picked == null) continue;
            target = picked;
            edit.Target = target.Handle.ToString(); edit.TargetId = resolver.Values(target).DeviceId ?? "";
            rebindPoint = resolver.Closest(target, arrow);
        }
        var rule = target == null ? null : rules.Select(edit.Rule, resolver.Values(target).Template, target.GetType().Name)?.WithFormats(edit.Top, edit.Bottom);
        var label = rule == null || target == null ? null : Plugin.For(document).Evaluator(schema, new[] { rule }, new[] { resolver.Snapshot(target) }).Evaluate(resolver.Snapshot(target), rule);
        var plan = new LeaderUpdatePlan(edit, LeaderFactory.Text(leader), target?.Handle.ToString(), target == null ? null : resolver.Values(target).DeviceId, label, true);
        if (target != null && rule == null) { plan.Data.State = LeaderState.FormatError; document.Editor.WriteMessage("\nПравило для цели не найдено; текст сохранён."); }
        tr.GetObject(leader.ObjectId, OpenMode.ForWrite);
        if (rebindPoint.HasValue) leader.SetFirstVertex(LeaderUpdater.Lines(leader)[0], rebindPoint.Value);
        if (label != null) { using var text = leader.MText; text.Contents = plan.Text; leader.MText = text; LeaderFactory.SetAttachment(leader, label); }
        LeaderFactory.WriteData(tr, document.Database, leader, plan.Data); tr.Commit();
    });

    [CommandMethod("SL_RULES_IMPORT", CommandFlags.Modal)]
    public void Import() => Execute(document => {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Правила JSON|*.json", Title = "Импорт правил" };
        if (dialog.ShowDialog() != true) return;
        var rules = LabelRules.Parse(File.ReadAllText(dialog.FileName), out var warnings);
        var evaluator = new LabelEvaluator(Plugin.For(document).Schema.Get(), rules.Rules);
        foreach (var warning in warnings.Concat(evaluator.Diagnostics)) document.Editor.WriteMessage("\nПредупреждение: " + warning);
        using var tr = document.Database.TransactionManager.StartTransaction(); NodLabelStore.Write(tr, document.Database, rules); tr.Commit();
    });
    [CommandMethod("SL_RULES_EXPORT", CommandFlags.Modal)]
    public void Export() => Execute(document => {
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "Правила JSON|*.json", FileName = "label-rules.json" };
        if (dialog.ShowDialog() != true) return;
        using var tr = document.Database.TransactionManager.StartOpenCloseTransaction();
        File.WriteAllText(dialog.FileName, NodLabelStore.Read(tr, document.Database, out _).ToJson()); tr.Commit();
    });
    internal static SelectionFilter LeaderFilter() => new SelectionFilter(new[] { new TypedValue((int)DxfCode.Start, "MULTILEADER"), new TypedValue((int)DxfCode.ExtendedDataRegAppName, LeaderData.RegApp) });
    private static Entity? PickTarget(Document document, Transaction tr, ObjectId owner, bool requireData)
    {
        var pick = document.Editor.GetEntity("\nВыберите объект цели (верхний уровень, то же пространство): ");
        if (pick.Status != PromptStatus.OK) return null;
        var entity = tr.GetObject(pick.ObjectId, OpenMode.ForRead) as Entity;
        if (entity == null || entity.OwnerId != owner) { document.Editor.WriteMessage("\nЦель должна быть в том же пространстве."); return null; }
        if (requireData)
        {
            using var data = entity.GetXDataForApplication(DrawingNames.RegApp);
            if (data == null) { document.Editor.WriteMessage("\nУ объекта нет BobrovXDATA."); return null; }
        }
        return entity;
    }
}

internal sealed class LandingJig : EntityJig
{
    private Point3d location;
    public LandingJig(MLeader leader) : base(leader) { location = leader.TextLocation; }
    protected override SamplerStatus Sampler(JigPrompts prompts)
    {
        var result = prompts.AcquirePoint(new JigPromptPointOptions("\nМесто полки: ") { UserInputControls = UserInputControls.Accept3dCoordinates });
        if (result.Status != PromptStatus.OK) return SamplerStatus.Cancel;
        var next = result.Value; // Jig acquisition already returns WCS coordinates.
        if (next.IsEqualTo(location)) return SamplerStatus.NoChange;
        location = next; return SamplerStatus.OK;
    }
    protected override bool Update()
    {
        var leader = (MLeader)Entity;
        leader.MoveMLeader(location - leader.TextLocation, MoveType.MoveContentAndDoglegPoints);
        return true;
    }
}
