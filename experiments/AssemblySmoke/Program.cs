using System.Globalization;
using System.Reflection;

if (args.Length != 4) throw new ArgumentException("Usage: AssemblySmoke <plugin.dll> <schema.json> <rules.json> <AutoCAD API major.minor>");
// An isolated process has no project reference to the plugin, Core or NCalc. Load from bytes
// to prevent the dependency resolver from finding adjacent files by the plugin's location.
var assembly = Assembly.Load(File.ReadAllBytes(args[0]));
var api = new Version(args[3]);
var references = assembly.GetReferencedAssemblies();
// SDK filenames and metadata identities use different casing (AcMgd/acmgd).
var acadNames = new HashSet<string>(new[] { "AcMgd", "AcDbMgd", "AcCoreMgd" }, StringComparer.OrdinalIgnoreCase);
var acadReferences = references.Where(a => acadNames.Contains(a.Name!)).ToArray();
var referencedAcadNames = new HashSet<string>(acadReferences.Select(a => a.Name!), StringComparer.OrdinalIgnoreCase);
if (!referencedAcadNames.IsSupersetOf(new[] { "AcDbMgd", "AcCoreMgd" }) ||
    acadReferences.Any(a => a.Version!.Major != api.Major || a.Version.Minor != api.Minor))
    throw new InvalidOperationException("Expected AutoCAD API " + api + "; assembly references: " + string.Join(", ", references.Select(a => a.FullName)));
var resources = assembly.GetManifestResourceNames();
if (!resources.Any(n => n.StartsWith("Embedded.NCalc", StringComparison.Ordinal)) ||
    resources.Any(n => n.StartsWith("Embedded.Ac", StringComparison.OrdinalIgnoreCase) || n.StartsWith("Embedded.Ad", StringComparison.OrdinalIgnoreCase)))
    throw new InvalidOperationException("Expected embedded NCalc, without AutoCAD SDK assemblies. Resources: " + string.Join(", ", resources));
var palette = "AcadPaletteSetXData.Core.";
var leader = "ACadSmartLeader.Core.";
Type Type(string name) => assembly.GetType(name, throwOnError: true)!;
object Make(string name, params object?[] values) => Activator.CreateInstance(Type(name), values)!;
Array Empty(string name) => Array.CreateInstance(Type(name), 0);
dynamic schemaResult = Type(palette + "ParameterSchemaJson").GetMethod("LoadFile")!.Invoke(null, new object?[] { args[1], null })!;
dynamic snapshot = Make(palette + "SchemaSnapshot", Make(palette + "SchemaNodReadResult", (object)schemaResult.Schema, null, Empty(palette + "SchemaIssue")));
var parseArgs = new object?[] { File.ReadAllText(args[2]), null };
dynamic rules = Type(leader + "LabelRules").GetMethod("Parse")!.Invoke(null, parseArgs)!;
var pairs = new (int Code, object Value)[] { (1001, "BobrovXDATA"), (1070, (short)2), (1000, "TEMPLATE"), (1000, "MOTOR"),
    (1000, "Power"), (1040, 7.5), (1000, "Current"), (1040, 15.2), (1000, "GC_HeadDevice"), (1000, "ЩУ1"), (1000, "GC_HDGroup"), (1000, "3") };
var values = Array.CreateInstance(Type(palette + "DataValue"), pairs.Length);
for (var i = 0; i < pairs.Length; i++) values.SetValue(Make(palette + "DataValue", pairs[i].Code, pairs[i].Value), i);
dynamic target = Make(palette + "EntityDataSnapshot", "1F", "BlockReference", values, Empty(palette + "DataRecord"), null, null);
dynamic evaluator = Make(leader + "LabelEvaluator", (object)snapshot, (object)rules.Rules, CultureInfo.GetCultureInfo("ru-RU"), null);
dynamic text = evaluator.Evaluate(target, rules.Rules[1]); // MOTOR_FULL exercises NCalc formula mode.
if (text.Bottom != "7,50 кВт, 15,2 А" || text.Errors.Count != 0) throw new InvalidOperationException("Embedded formula failed: " + text.Bottom);
Console.WriteLine("ASSEMBLY_SMOKE_OK: " + Path.GetFileName(args[0]) + "; API=" + api + "; " + text.Bottom + "; embedded assemblies=" + resources.Length);
