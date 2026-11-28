using System.Globalization;
using ACadSmartLeader.Core;
using AcadPaletteSetXData.Core;
using Xunit;

namespace ACadSmartLeader.Core.Tests;

public sealed class CoreTests
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    private static SchemaSnapshot Schema() => new(new SchemaNodReadResult(ParameterSchemaJson.LoadFile(Path.Combine(AppContext.BaseDirectory, "schema.json")).Schema, null, Array.Empty<SchemaIssue>()));
    private static LabelRules Rules() => LabelRules.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "label-rules.json")), out _);
    private static EntityDataSnapshot Target(string template, params (string Id, object Value)[] pairs) => new("1F", template == "CABLE" ? "Polyline" : "BlockReference",
        new[] { new DataValue(1001, "BobrovXDATA"), new DataValue(1070, (short)2), new DataValue(1000, "TEMPLATE"), new DataValue(1000, template) }
            .Concat(pairs.SelectMany(p => new[] { new DataValue(1000, p.Id), new DataValue(p.Value switch { double => 1040, int => 1071, bool => 1070, _ => 1000 }, p.Value is bool b ? (short)(b ? 1 : 0) : p.Value) })).ToArray(), Array.Empty<DataRecord>());

    [Fact]
    public void RealPaletteCoreEvaluatesAcceptanceExamplesAndChangedPower()
    {
        var rules = Rules(); var engine = new LabelEvaluator(Schema(), rules.Rules, Ru);
        var motor = Target("MOTOR", ("Device_Number", "М1"), ("Power", 7.5), ("Current", 15.2), ("GC_HeadDevice", "ЩУ1"), ("GC_HDGroup", "3"));
        var label = engine.Evaluate(motor, rules.Rules[0]);
        Assert.Equal("М1\\P7,50 кВт", label.MText); Assert.Empty(label.Errors);
        Assert.Equal("7,50 кВт, 15,2 А", engine.Evaluate(motor, rules.Rules[1]).Bottom);
        Assert.Equal("11,00 кВт", engine.Evaluate(Target("MOTOR", ("Power", 11.0)), rules.Rules[0]).Bottom);
        var cable = Target("CABLE", ("Cable_Mark", "ВВГнг-LS"), ("Cable_Cores", 5), ("Cable_Section", 2.5), ("GC_HeadDevice", "ЩУ1"), ("GC_HDGroup", "3"));
        Assert.Equal("ЩУ1.3\\PВВГнг-LS 5x2,5", engine.Evaluate(cable, rules.Rules[2]).MText);
        Assert.Equal("ЩУ1.3", engine.Evaluate(cable, rules.Rules[3]).MText);
        Assert.False(engine.Evaluate(cable, rules.Rules[3]).HasBottom);
    }

    [Fact]
    public void UpdateUsesChangedCalculatedStringEvenWhenItsOptionalInputIsBlank()
    {
        var target = Target("MOTOR", ("Device_Number", "М1"), ("GC_HDGroup", ""));
        var rule = new LabelRule { Id = "R", Top = "@@[Device_Number]", Bottom = "@@[GC_Name]", Empty = "—" };

        LabelText Evaluate(string group)
        {
            var schema = Schema();
            schema.Schema!.FindParameter("GC_Name")!.Format = "='" + group + "' & @@[GC_HDGroup]";
            var evaluator = new LabelEvaluator(schema, new[] { rule }, Ru);
            var calculation = evaluator.Calculate(target);
            Assert.Equal(group, calculation.Text("GC_Name"));
            return evaluator.Evaluate(calculation, rule);
        }

        var before = Evaluate("ЩУ1");
        var stored = new LeaderData { Target = target.Handle, Text = LeaderData.TextStamp(before.MText) };
        var plan = new LeaderUpdatePlan(stored, before.MText, target.Handle, null, Evaluate("ЩУ2"));

        Assert.Equal("М1\\PЩУ2", plan.Text);
        Assert.Equal("М1\\PЩУ1", before.MText);
        Assert.True(plan.Changed);
        Assert.Equal(LeaderState.Bound, plan.Data.State);
    }

    [Theory]
    [InlineData("@@[PART_CODE]", false)]
    [InlineData("@@[\"PART_CODE\"]", true)]
    public void UpdateRecalculatesBlockTextFromFreshXDataAndAttributes(string reference, bool persist)
    {
        var schema = Schema();
        var parameter = schema.Schema!.FindParameter("GC_Name")!;
        parameter.Format = "='П' & @@[GC_HDGroup] & ':' & " + reference;
        parameter.Persist = persist;
        var rule = new LabelRule { Id = "R", Top = "@@[GC_HDGroup]" + reference, Bottom = "@@[GC_Name]", Empty = "—" };
        var evaluator = new LabelEvaluator(schema, new[] { rule }, Ru);
        var initial = Target("MOTOR", ("GC_HDGroup", ""), ("GC_Name", "Старое значение"));

        EntityDataSnapshot WithAttribute(EntityDataSnapshot target, string code) => new(target.Handle, target.EntityType, target.XData,
            target.Records, entityProperties: new EntityProperties(new[] { KeyValuePair.Create("PART_CODE", code) }));

        var before = evaluator.Evaluate(WithAttribute(initial, "A-17"), rule);
        var data = new LeaderData { Target = initial.Handle, Text = LeaderData.TextStamp(before.MText) };

        var attributeEdited = WithAttribute(initial, "A-18");
        var updatedAttribute = new LeaderUpdatePlan(data, before.MText, initial.Handle, null, evaluator.Evaluate(attributeEdited, rule));
        Assert.Equal("A-18\\PП:A-18", updatedAttribute.Text);
        Assert.Equal("A-17\\PП:A-17", before.MText);

        var paletteEdited = new XDataPatch(schema, culture: Ru).Apply(attributeEdited, SelectionEdit.SetParameter("GC_HDGroup", "42"));
        Assert.Equal(persist ? "П42:A-18" : "Старое значение", XDataV2.Read(paletteEdited, schema).Find("GC_Name")!.Value);
        var result = evaluator.Evaluate(paletteEdited, rule);
        var updated = new LeaderUpdatePlan(updatedAttribute.Data, updatedAttribute.Text, initial.Handle, null, result);
        Assert.Equal("42A-18\\PП42:A-18", updated.Text);
        Assert.Equal(LeaderState.Bound, updated.Data.State);
        Assert.False(new LeaderUpdatePlan(updated.Data, updated.Text, initial.Handle, null, result).Changed);
    }

    [Fact]
    public void ShelfFallbackUsesEntityMeasurementsAlongsideOptionalXData()
    {
        var rule = new LabelRule { Id = "R", Top = "@@[Device_Number]@@[geomLength:geom] м", Empty = "—" };
        var evaluator = new LabelEvaluator(Schema(), new[] { rule }, Ru);
        var target = Target("CABLE");
        Assert.Equal("—", evaluator.Evaluate(target, rule).Top);

        var measured = new EntityDataSnapshot(target.Handle, target.EntityType, target.XData, target.Records,
            entityProperties: new EntityProperties(length: 3.5));
        Assert.Equal("3,5 м", evaluator.Evaluate(measured, rule).Top);
    }

    [Fact]
    public void ErrorsEmptyDefaultsAndEscapingAreExplicit()
    {
        var rule = new LabelRule { Id = "BROKEN", Top = "@@[Unknown_Id]", Bottom = "@@[Cable_Mark", Empty = "—" };
        var engine = new LabelEvaluator(Schema(), new[] { rule }, Ru);
        var result = engine.Evaluate(Target("CABLE"), rule);
        Assert.Equal("#ОШИБКА\\P#ОШИБКА", result.MText); Assert.Equal(2, result.Errors.Count);
        var empty = new LabelRule { Id = "E", Top = "@@[Cable_Mark] @@[Device_Number]", Empty = "—" };
        Assert.Equal("—", new LabelEvaluator(Schema(), new[] { empty }).Evaluate(Target("CABLE"), empty).Top);
        var defaults = Rules().Rules[4];
        Assert.Equal("Электродвигатель", new LabelEvaluator(Schema(), new[] { defaults }).Evaluate(Target("MOTOR"), defaults).Top);
        Assert.Equal(@"a\\b\{c\}", LabelText.Escape(@"a\b{c}"));
        var text = new LabelRule { Id = "T", Top = "@@[Device_Number]" };
        Assert.Equal(@"\{\\LInjected\\l\}", new LabelEvaluator(Schema(), new[] { text }).Evaluate(Target("MOTOR", ("Device_Number", @"{\LInjected\l}")), text).MText);
    }

    [Fact]
    public void MissingSchemaUsesScalarValuesAndNoInventedPrecision()
    {
        var schema = new SchemaSnapshot(new SchemaNodReadResult(null, null, Array.Empty<SchemaIssue>()));
        var target = Target("MOTOR", ("Device_Number", "М1"), ("Power", 7.5)); var rule = Rules().Rules[0];
        var result = new LabelEvaluator(schema, new[] { rule }, Ru, new[] { target }).Evaluate(target, rule);
        Assert.Equal("М1\\P7,5 кВт", result.MText); Assert.Empty(result.Errors);
    }

    [Fact]
    public void EnumBooleanCultureAndShelfOverridesUsePaletteSemantics()
    {
        var rule = new LabelRule { Id = "T", Top = "@@[Voltage] / @@[Voltage:key] / @@[Tray_Covered] / @@@@", Bottom = "@@[Power]" };
        var target = Target("MOTOR", ("Voltage", "_AC_380V_50Hz"), ("Tray_Covered", true), ("Power", 7.5));
        var text = new LabelEvaluator(Schema(), new[] { rule }, Ru).Evaluate(target, rule);
        Assert.Equal("380 В / _AC_380V_50Hz / Да / @@", text.Top); Assert.Equal("7,50", text.Bottom); Assert.Empty(text.Errors);
        var overridden = rule.WithFormats(null, "");
        var engine = new LabelEvaluator(Schema(), new[] { overridden }, CultureInfo.GetCultureInfo("en-US"));
        Assert.False(engine.Evaluate(target, overridden).HasBottom); Assert.Equal("@@[Power]", rule.Bottom);
        Assert.Equal("7.50", new LabelEvaluator(Schema(), new[] { rule }, CultureInfo.GetCultureInfo("en-US")).Evaluate(target, rule).Bottom);
    }

    [Fact]
    public void RemovedTargetDataUsesEmptyEvenWithSchemaDefaults()
    {
        var rule = new LabelRule { Id = "R", Top = "@@[Device_Type]", Empty = "—" };
        var snapshot = new EntityDataSnapshot("1", "Line", Array.Empty<DataValue>(), Array.Empty<DataRecord>());
        Assert.Equal("—", new LabelEvaluator(Schema(), new[] { rule }).Evaluate(snapshot, rule).Top);
        var absentSchema = new SchemaSnapshot(new SchemaNodReadResult(null, null, Array.Empty<SchemaIssue>()));
        Assert.Equal("—", new LabelEvaluator(absentSchema, new[] { rule }).Evaluate(snapshot, rule).Top);
    }

    [Fact]
    public void OrderedRulesHonorExplicitTemplateAndWildcardFilters()
    {
        var rules = Rules();
        Assert.Equal("MOTOR", rules.Select(null, "MOTOR", "BlockReference")!.Id);
        Assert.Equal("MOTOR_FULL", rules.Select(null, "MOTOR", "Line")!.Id);
        Assert.Equal("ANY", rules.Select(null, "OTHER", "Circle")!.Id);
        Assert.Equal("CABLE_GROUP", rules.Select("CABLE_GROUP", "MOTOR", "BlockReference")!.Id);
        Assert.Equal("MOTOR", rules.Select("MISSING", "MOTOR", "BlockReference")!.Id);
        Assert.Null(new LabelRules().Select(null, "MOTOR", "Line"));
    }

    [Fact]
    public void AutomaticGroupSelectionHonorsSuffixFiltersAndPriority()
    {
        var rules = new LabelRules();
        rules.Rules.AddRange(new[] {
            new LabelRule { Id = "ANY_GROUP" },
            new LabelRule { Id = "LINE_GROUP", Templates = new() { "MOTOR" }, EntityTypes = new() { "Line" } },
            new LabelRule { Id = "DEVICE_Group", Templates = new() { "MOTOR" }, EntityTypes = new() { "BlockReference" } },
            new LabelRule { Id = "OTHER_GROUP", Templates = new() { "MOTOR" } },
            new LabelRule { Id = "DEVICE_GROUP_DETAIL", Templates = new() { "MOTOR" } },
            new LabelRule { Id = "DEVICE", Templates = new() { "MOTOR" } }
        });

        Assert.Equal("DEVICE_Group", rules.SelectGroup("motor", "blockreference")?.Id);
        Assert.Equal("LINE_GROUP", rules.SelectGroup("MOTOR", "Line")?.Id);
        Assert.Equal("ANY_GROUP", rules.SelectGroup(null, "Circle")?.Id);
        Assert.Equal("DEVICE_GROUP_DETAIL", rules.Select(null, "MOTOR", "BlockReference")?.Id);
        Assert.Equal("DEVICE_Group", rules.Select("device_group", "OTHER", "Line")?.Id);

        rules.Rules.RemoveAll(rule => rule.Id != "LINE_GROUP" && rule.Id != "DEVICE");
        Assert.Null(rules.SelectGroup("MOTOR", "BlockReference"));
        Assert.Null(rules.SelectGroup("OTHER", "Line"));
        Assert.Null(rules.Select(null, "OTHER", "Line"));
    }

    [Fact]
    public void GroupLeaderRetainsItsOwnTextAndRuleAcrossUpdates()
    {
        var group = new LabelRule { Id = "DEVICE_GROUP", Templates = new() { "MOTOR" }, Top = "@@[Group]", Bottom = "@@[Mount_Height] мм" };
        var regular = new LabelRule { Id = "DEVICE", Templates = new() { "MOTOR" }, Top = "@@[Device_Number]", Bottom = "@@[Power] кВт" };
        var rules = new LabelRules();
        rules.Rules.AddRange(new[] { group, regular });
        var target = Target("MOTOR", ("Device_Number", "М1"), ("Power", 7.5), ("Group", "ЩУ1.3"), ("Mount_Height", 1200));
        var schema = new SchemaSnapshot(new SchemaNodReadResult(null, null, Array.Empty<SchemaIssue>()));
        var engine = new LabelEvaluator(schema, rules.Rules, Ru, new[] { target });
        var regularText = engine.Evaluate(target, rules.Select(null, "MOTOR", target.EntityType)!);
        var chosenGroup = rules.SelectGroup("MOTOR", target.EntityType)!;
        var groupText = engine.Evaluate(target, chosenGroup);
        Assert.Equal("М1\\P7,5 кВт", regularText.MText);
        Assert.Equal("ЩУ1.3\\P1200 мм", groupText.MText);
        Assert.Empty(regularText.Errors);
        Assert.Empty(groupText.Errors);

        var stored = new LeaderData { Target = target.Handle, Rule = chosenGroup.Id, Text = LeaderData.TextStamp(groupText.MText) };
        Assert.True(LeaderData.TryRead(stored.Write(), out var saved, out _));
        rules.Rules.Insert(0, new LabelRule { Id = "PREFERRED_GROUP", Templates = new() { "MOTOR" }, Top = "Другой формат" });
        Assert.Equal("PREFERRED_GROUP", rules.SelectGroup("MOTOR", target.EntityType)?.Id);
        var updatedTarget = Target("MOTOR", ("Group", "ЩУ1.4"), ("Mount_Height", 1500));
        var updatedRule = rules.Select(saved!.Rule, "MOTOR", updatedTarget.EntityType)!;
        var updatedText = engine.Evaluate(updatedTarget, updatedRule);
        var update = new LeaderUpdatePlan(saved, groupText.MText, saved.Target, null, updatedText);
        Assert.Equal("DEVICE_GROUP", update.Data.Rule);
        Assert.Equal("ЩУ1.4\\P1500 мм", update.Text);

        rules.Rules.Remove(group);
        Assert.Null(rules.Select(saved.Rule, "MOTOR", updatedTarget.EntityType));
    }

    [Fact]
    public void JsonExtensionsRoundTripAndInvalidRulesAreRejected()
    {
        const string json = "{\"format\":\"ACadSmartLeader.labels\",\"version\":1,\"future\":{\"x\":2},\"rules\":[{\"id\":\"R\",\"caption\":\"Р\",\"top\":\"@@[Later]\",\"unknown\":[1,true]}]}";
        var parsed = LabelRules.Parse(json, out var warnings); Assert.Equal(2, warnings.Count);
        var again = LabelRules.Parse(parsed.ToJson(), out _);
        Assert.True(JsonValue.DeepEquals(parsed.Extra, again.Extra)); Assert.True(JsonValue.DeepEquals(parsed.Rules[0].Extra, again.Rules[0].Extra));
        parsed.Rules[0].Extra.Add("order", new JsonString("future order"));
        parsed.Rules[0].Extra.Add("extensions", new JsonNumber(42L));
        Assert.True(JsonValue.DeepEquals(LabelRules.RuleJson(parsed.Rules[0]), LabelRules.RuleFromRecord(LabelRules.RecordJson(parsed.Rules[0], 3))));
        Assert.NotEmpty(new LabelEvaluator(Schema(), parsed.Rules).Diagnostics); // syntax/reference warnings do not prevent saving
        parsed.Rules.Add(new LabelRule { Id = "r", Caption = "duplicate", Top = "text" }); Assert.NotEmpty(parsed.Validate());
        Assert.Throws<FormatException>(() => LabelRules.Parse(parsed.ToJson(), out _));
        Assert.Throws<FormatException>(() => LabelRules.Parse(json.Replace("\"version\":1", "\"version\":2"), out _));
        Assert.Throws<FormatException>(() => LabelRules.Parse(json.Replace("\"top\":\"@@[Later]\"", "\"top\":3"), out _));
    }

    [Fact]
    public void LeaderCodecHandlesHashOverridesAndForeignApps()
    {
        var data = new LeaderData { Target = "AB12", TargetId = Guid.NewGuid().ToString(), Rule = "MOTOR", Top = "", Bottom = "@@[Power]", Text = LeaderData.TextStamp(new string('я', 300)), State = LeaderState.ManualText };
        var values = new[] { new DataValue(1001, "OTHER"), new DataValue(1000, "keep") }.Concat(data.Write()).ToArray();
        Assert.True(LeaderData.TryRead(values, out var read, out var error)); Assert.Null(error); Assert.True(data.SameAs(read!));
        Assert.True(read!.Matches(new string('я', 300))); Assert.False(read.Matches("manual")); Assert.Equal(41, read.Text.Length);
        Assert.StartsWith("#", LeaderData.TextStamp("#literal"));
        Assert.True(new LeaderData { Text = LeaderData.TextStamp("#literal") }.Matches("#literal"));
        Assert.True(LeaderData.TryRead(new LeaderData { State = LeaderState.Detached }.Write(), out _, out _));
    }

    [Fact]
    public void LeaderCodecRejectsDamagedFutureAndOversizedData()
    {
        var data = new LeaderData { Top = new string('я', 128) }; Assert.Throws<FormatException>(() => data.Write());
        var values = new LeaderData().Write().ToArray(); values[1] = new DataValue(1070, (short)2);
        Assert.False(LeaderData.TryRead(values, out _, out var error)); Assert.NotNull(error);
        Assert.False(LeaderData.TryRead(new LeaderData().Write().Take(5).ToArray(), out _, out _));
        Assert.False(LeaderData.TryRead(new[] { new DataValue(1001, "OTHER") }, out _, out error)); Assert.Null(error);
    }

    [Fact]
    public void UpdatePlansPreserveManualDetachedAndLockedLeaders()
    {
        var text = new LabelText("М1", "11,00 кВт", true, true, Array.Empty<string>());
        var original = new LeaderData { Target = "A", Text = "М1\\P7,50 кВт" };
        var changed = new LeaderUpdatePlan(original, original.Text, "A", "", text);
        Assert.True(changed.Changed); Assert.Equal(text.MText, changed.Text);
        Assert.False(new LeaderUpdatePlan(changed.Data, text.MText, "A", "", text).Changed);
        var manual = new LeaderUpdatePlan(original, "ручной", "B", "", text);
        Assert.Equal(LeaderState.ManualText, manual.Data.State); Assert.Equal("ручной", manual.Text); Assert.True(manual.Rebound);
        Assert.Equal(text.MText, new LeaderUpdatePlan(original, "ручной", "A", "", text, force: true).Text);
        var detached = new LeaderUpdatePlan(original, original.Text, null, null, null);
        Assert.Equal(LeaderState.Detached, detached.Data.State); Assert.Equal(original.Text, detached.Text); Assert.Equal("A", detached.Data.Target);
        var locked = new LeaderUpdatePlan(original, original.Text, "B", "", text, locked: true);
        Assert.True(locked.Skipped); Assert.False(locked.Changed); Assert.True(original.SameAs(locked.Data));
    }

    [Fact]
    public void GeometryHonorsToleranceBoundsAndDrawOrder()
    {
        var p = TargetGeometry.ClosestOnSegment(new Point3(2, 1), new Point3(0, 0), new Point3(1, 0)); Assert.Equal(1, p.X); Assert.Equal(0, p.Y);
        Assert.Equal(0, TargetGeometry.ClosestOnBox(new Point3(1, 1), new Point3(0, 0), new Point3(2, 2)).DistanceTo(new Point3(1, 1)));
        Assert.Equal(1e-6, TargetGeometry.Tolerance(0)); Assert.Equal(0.025, TargetGeometry.Tolerance(2.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => TargetGeometry.Tolerance(1, double.NaN));
        var candidates = new[] { new TargetCandidate("A", .1, 1), new TargetCandidate("B", .1, 2), new TargetCandidate("C", double.NaN, 3) };
        Assert.Equal("B", TargetGeometry.Nearest(candidates, .1)!.Handle); Assert.Null(TargetGeometry.Nearest(candidates, .09));
    }

    [Fact]
    public void BindingHonorsHandleArrowUniqueGuidAndDetachedPrecedence()
    {
        var probes = 0;
        Assert.Equal("saved", TargetBinding.Resolve("saved", _ => true, () => { probes++; return "copy"; }, () => new[] { "guid" }, out var off));
        Assert.False(off); Assert.Equal(0, probes);
        Assert.Equal("copy", TargetBinding.Resolve("saved", _ => false, () => "copy", () => new[] { "guid" }, out off)); Assert.False(off);
        Assert.Equal("guid", TargetBinding.Resolve<string>(null, _ => false, () => null, () => new[] { "guid" }, out off)); Assert.True(off);
        Assert.Null(TargetBinding.Resolve<string>(null, _ => false, () => null, () => new[] { "copy1", "copy2" }, out off)); Assert.False(off);
        Assert.Null(TargetBinding.Resolve<string>(null, _ => false, () => null, () => Array.Empty<string>(), out _));
    }

    [Fact]
    public void DeferredRegenRunsOnceAndDoesNotReenter()
    {
        var schedule = new RegenSchedule(); schedule.CommandEnded("REGENALL", RegenMode.Off); Assert.False(schedule.Pending);
        schedule.CommandEnded("REGEN", RegenMode.RegenAll); Assert.False(schedule.Pending);
        schedule.CommandEnded("REGENALL", RegenMode.RegenAll); schedule.CommandEnded("REGENALL", RegenMode.RegenAll);
        Assert.False(schedule.Dispatch(false)); Assert.True(schedule.Dispatch(true)); Assert.False(schedule.Dispatch(true));
        schedule.CommandEnded("REGENALL", RegenMode.Regen); Assert.False(schedule.Pending);
        schedule.Complete(); schedule.CommandEnded("REGEN", RegenMode.Regen); Assert.True(schedule.Dispatch(true));
        schedule.Clear(); Assert.False(schedule.Running); Assert.False(schedule.Pending);
    }
}
