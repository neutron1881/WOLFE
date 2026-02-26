using System.Reflection;
using System.Runtime.InteropServices;

var runtimeDir = RuntimeEnvironment.GetRuntimeDirectory();
var dllDir = Path.GetFullPath(".");

// Collect all DLLs from runtime, local, and WPF directories
var allPaths = new List<string>();
allPaths.AddRange(Directory.GetFiles(runtimeDir, "*.dll"));
allPaths.AddRange(Directory.GetFiles(dllDir, "*.dll"));

// Add WPF assemblies
var wpfDir = Path.Combine(runtimeDir, "..", "Microsoft.WindowsDesktop.App");
if (Directory.Exists(wpfDir))
{
    var latestWpf = Directory.GetDirectories(wpfDir).OrderByDescending(d => d).FirstOrDefault();
    if (latestWpf != null)
        allPaths.AddRange(Directory.GetFiles(latestWpf, "*.dll"));
}

var resolver = new PathAssemblyResolver(allPaths.Distinct());
using var mlc = new MetadataLoadContext(resolver);

var asm1 = mlc.LoadFromAssemblyPath(Path.Combine(dllDir, "OFT.Rendering.dll"));
var asm2 = mlc.LoadFromAssemblyPath(Path.Combine(dllDir, "ATAS.Indicators.dll"));

string SafeParams(MethodBase m)
{
    try { return string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}")); }
    catch { return "?"; }
}
string SafeReturn(MethodInfo m)
{
    try { return m.ReturnType.Name; }
    catch { return "?"; }
}
string SafePropType(PropertyInfo p)
{
    try { return p.PropertyType.Name; }
    catch { return "?"; }
}

// Only print rendering types and DrawingLayouts
// DrawingLayouts
var dlType = asm2.GetType("ATAS.Indicators.DrawingLayouts");
if (dlType != null)
{
    Console.WriteLine("--- DrawingLayouts ---");
    foreach (var n in Enum.GetNames(dlType))
        Console.WriteLine($"  {n}");
}
else Console.WriteLine("DrawingLayouts type not found");

// RenderStringFormat
var rsfType = asm1.GetType("OFT.Rendering.Tools.RenderStringFormat");
if (rsfType != null)
{
    Console.WriteLine("\n--- RenderStringFormat ---");
    foreach (var c in rsfType.GetConstructors())
        try { Console.WriteLine($"  Constructor({SafeParams(c)})"); } catch { }
    foreach (var p in rsfType.GetProperties(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance))
        try { Console.WriteLine($"  Property: {p.Name} ({SafePropType(p)})"); } catch { }
    foreach (var f in rsfType.GetFields(BindingFlags.Public | BindingFlags.Static))
        try { Console.WriteLine($"  Static Field: {f.Name}"); } catch { }
}
else Console.WriteLine("RenderStringFormat not found");

// Indicator class - look for alert methods and other key methods
var indType = asm2.GetType("ATAS.Indicators.Indicator");
if (indType != null)
{
    Console.WriteLine("\n--- Indicator methods containing 'alert' or 'Alert' ---");
    foreach (var m in indType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
        .Where(m => m.Name.Contains("lert", StringComparison.OrdinalIgnoreCase) || 
                     m.Name.Contains("Panel") || m.Name.Contains("Draw") || m.Name.Contains("Render") ||
                     m.Name.Contains("Subscribe") || m.Name.Contains("Redraw") || m.Name.Contains("Recalc") ||
                     m.Name.Contains("Enable") || m.Name.Contains("Deny") || m.Name.Contains("OnInit") ||
                     m.Name.Contains("OnDispose") || m.Name.Contains("OnCalculate"))
        .OrderBy(m => m.Name))
    {
        try { Console.WriteLine($"  {m.Name}({SafeParams(m)}) -> {SafeReturn(m)} [{(m.IsPublic?"pub":"prot")}]"); } catch { }
    }

    Console.WriteLine("\n--- Indicator properties ---");
    foreach (var p in indType.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
        .OrderBy(p => p.Name))
    {
        try { Console.WriteLine($"  {p.Name} ({SafePropType(p)})"); } catch { }
    }
}

// Container (the panel container for the indicator)
var containerType = asm2.GetType("ATAS.Indicators.Container");
if (containerType != null)
{
    Console.WriteLine("\n--- Container ---");
    foreach (var p in containerType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        try { Console.WriteLine($"  Property: {p.Name} ({SafePropType(p)})"); } catch { }
    foreach (var m in containerType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
        .Where(m => !m.Name.StartsWith("get_") && !m.Name.StartsWith("set_"))
        .OrderBy(m => m.Name))
        try { Console.WriteLine($"  {m.Name}({SafeParams(m)}) -> {SafeReturn(m)}"); } catch { }
}

// IContainer
var icontType = asm2.GetType("ATAS.Indicators.IContainer");
if (icontType != null)
{
    Console.WriteLine("\n--- IContainer ---");
    foreach (var p in icontType.GetProperties())
        try { Console.WriteLine($"  Property: {p.Name} ({SafePropType(p)})"); } catch { }
    foreach (var m in icontType.GetMethods().Where(m => !m.Name.StartsWith("get_") && !m.Name.StartsWith("set_")).OrderBy(m => m.Name))
        try { Console.WriteLine($"  {m.Name}({SafeParams(m)}) -> {SafeReturn(m)}"); } catch { }
}

// Skip RenderContext, RenderPen, RenderFont, LineDashStyle (already got them)
Console.WriteLine("DONE");
// Check RedrawArg and InstrumentInfo
var rdType = asm2.GetType("ATAS.Indicators.RedrawArg");
if (rdType != null) {
    Console.WriteLine("--- RedrawArg ---");
    foreach (var c in rdType.GetConstructors()) try { Console.WriteLine($"  Constructor({SafeParams(c)})"); } catch { }
    foreach (var p in rdType.GetProperties()) try { Console.WriteLine($"  Property: {p.Name} ({SafePropType(p)})"); } catch { }
    foreach (var f in rdType.GetFields(BindingFlags.Public | BindingFlags.Static)) try { Console.WriteLine($"  Static: {f.Name}"); } catch { }
}
var iiType = asm2.GetType("ATAS.Indicators.IInstrumentInfo");
if (iiType != null) {
    Console.WriteLine("--- IInstrumentInfo ---");
    foreach (var p in iiType.GetProperties()) try { Console.WriteLine($"  Property: {p.Name} ({SafePropType(p)})"); } catch { }
}
