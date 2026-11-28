using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AcadPaletteSetXData.Core;

namespace ACadSmartLeader.Core;

public sealed class LabelText
{
    public string Top { get; }
    public string Bottom { get; }
    public bool HasTop { get; }
    public bool HasBottom { get; }
    public IReadOnlyList<string> Errors { get; }
    public string MText => HasTop && HasBottom ? Escape(Top) + "\\P" + Escape(Bottom) : Escape(HasTop ? Top : Bottom);
    public LabelText(string top, string bottom, bool hasTop, bool hasBottom, IReadOnlyList<string> errors)
    { Top = top; Bottom = bottom; HasTop = hasTop; HasBottom = hasBottom; Errors = errors; }
    public static string Escape(string text) => text.Replace("\\", "\\\\").Replace("{", "\\{").Replace("}", "\\}")
        .Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\\P");
}

/// <summary>One cloned schema and parsed format set per schema/rule revision, shared by all targets.</summary>
public sealed class LabelEvaluator
{
    private readonly Dictionary<string, string> ids = new Dictionary<string, string>(StringComparer.Ordinal);
    private readonly CultureInfo culture;
    public SchemaSnapshot Schema { get; }

    public LabelEvaluator(SchemaSnapshot source, IEnumerable<LabelRule> rules, CultureInfo? culture = null,
        IEnumerable<EntityDataSnapshot>? rawTargets = null)
    {
        this.culture = culture ?? CultureInfo.CurrentCulture;
        var schema = source.Schema == null ? new ParameterSchema() : ParameterSchemaJson.Clone(source.Schema);
        // No VELEC schema: infer only scalar storage types; no precision, defaults or enum captions are invented.
        if (source.Schema == null && rawTargets != null)
            foreach (var target in rawTargets)
                foreach (var value in XDataV2.Read(target, source).Values)
                    if (schema.FindParameter(value.Id) == null)
                        schema.Parameters.Add(new SchemaParameter { Id = value.Id, Type = value.Stored.Count == 1 ?
                            value.Stored[0].TypeCode switch { 1040 => "Double", 1070 => "Boolean", 1071 => "Integer", _ => "String" } : "String" });
        foreach (var format in rules.SelectMany(r => new[] { r.Top, r.Bottom }).Where(f => !string.IsNullOrEmpty(f)).Distinct(StringComparer.Ordinal))
        {
            var id = "__LEADER_" + ids.Count.ToString(CultureInfo.InvariantCulture);
            while (schema.FindParameter(id) != null) id += "_";
            ids.Add(format, id);
            schema.Parameters.Add(new SchemaParameter { Id = id, Type = "CalculatedString", Format = format });
        }
        Schema = new SchemaSnapshot(new SchemaNodReadResult(schema, null, Array.Empty<SchemaIssue>()));
    }

    public ObjectCalculation Calculate(EntityDataSnapshot target) => new ObjectCalculation(XDataV2.Read(target, Schema), culture);

    public LabelText Evaluate(EntityDataSnapshot target, LabelRule rule) => Evaluate(Calculate(target), rule);

    public LabelText Evaluate(ObjectCalculation calculation, LabelRule rule)
    {
        var errors = new List<string>();
        var top = Shelf(rule.Top); var bottom = Shelf(rule.Bottom);
        return new LabelText(top, bottom, rule.Top.Length != 0, rule.Bottom.Length != 0, errors);

        string Shelf(string format)
        {
            if (string.IsNullOrEmpty(format)) return "";
            if (calculation.Source.Format == XDataFormat.None) return rule.Empty;
            if (!ids.TryGetValue(format, out var id)) throw new InvalidOperationException("Формат не включён в снимок вычисления.");
            var result = calculation.Find(id)!;
            if (result.IsError) { errors.Add(result.Error ?? "Ошибка формата."); return rule.Error; }
            var references = result.Formula.Fragments.Where(fragment => fragment.Reference != null);
            if (references.Any() && references.All(reference => IsEmpty(ReferenceValue(calculation, reference)))) return rule.Empty;
            return result.Text;
        }
    }

    private static object? ReferenceValue(ObjectCalculation calculation, FormatFragment reference)
    {
        var id = reference.Reference!;
        if (!reference.IsExplicitEntityProperty && calculation.Schema.Parameters.ContainsKey(id)) return calculation.Value(id);

        var properties = calculation.Source.EntityProperties;
        if (!reference.IsAttribute && EntityProperties.IsGeometryId(id)) return properties.Geometry(id);
        return properties.Attributes.TryGetValue(id, out var attribute) ? attribute : null;
    }

    private static bool IsEmpty(object? value) => value is null || value is string { Length: 0 };

    public IReadOnlyList<string> Diagnostics => Schema.Formulas.Diagnostics.Concat(Schema.Formulas.Formulas.SelectMany(formula =>
        formula.Unresolved.Select(fragment => formula.Id + ": " + FormulaSet.Reference(fragment) +
            ": параметр не найден в схеме; будет прочитан как атрибут блока"))).ToArray();
}
