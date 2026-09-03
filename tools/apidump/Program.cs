// Dumps the real public surface of the game's Il2CppInterop assemblies.
//
// Why this exists: the interop assemblies are generated, so their member names are not
// always the ones in Unity's documentation. Properties come back as get_/set_ pairs, some
// members fail to unstrip and are simply absent, and the game's own types keep whatever
// the IL2CPP metadata called them. Guessing costs a build cycle every time; this reads the
// truth in a second.
//
//   dotnet build/apidump/net8.0/apidump.dll SceneManager XRSettings
//   dotnet build/apidump/net8.0/apidump.dll --types Nobeta
//
// Assemblies are read from the game install; override with NOBETA_INTEROP.

using System.Reflection;

var interop = Environment.GetEnvironmentVariable("NOBETA_INTEROP")
    ?? "H:/Steam/steamapps/common/Little Witch Nobeta/BepInEx/interop";

if (!Directory.Exists(interop))
{
    Console.Error.WriteLine($"No interop directory at '{interop}'. Set NOBETA_INTEROP.");
    return 1;
}

if (args.Length == 0)
{
    Console.Error.WriteLine("usage: apidump <TypeNameSubstring>... | --types <NameSubstring>...");
    return 1;
}

// The .NET base assemblies must be resolvable too, or every type mentioning System.Object
// fails to load.
var paths = Directory.GetFiles(interop, "*.dll").ToList();
paths.AddRange(Directory.GetFiles(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "*.dll"));

// Every interop type carries Il2CppInterop attributes, so BepInEx's own assemblies have to
// be resolvable or member enumeration throws before printing anything.
var core = Path.Combine(Path.GetDirectoryName(interop)!, "core");
if (Directory.Exists(core)) paths.AddRange(Directory.GetFiles(core, "*.dll"));

using var mlc = new MetadataLoadContext(new PathAssemblyResolver(paths));

var assemblies = new List<Assembly>();
foreach (var p in Directory.GetFiles(interop, "*.dll"))
{
    // A few generated assemblies are not loadable at all. They are never the ones asked
    // for, and failing the whole run over them would make the tool useless.
    try { assemblies.Add(mlc.LoadFromAssemblyPath(p)); }
    catch { }
}

var listTypes = args[0] == "--types";
var needles = (listTypes ? args.Skip(1) : args).ToArray();

// Il2CppInterop emits some types whose metadata trips MetadataLoadContext when their name
// is computed. Reading a name must never be able to kill the run.
static string? NameOf(Type t)
{
    try { return t.FullName; } catch { return null; }
}

var all = new List<(Assembly asm, Type type, string name)>();
foreach (var a in assemblies)
{
    Type[] types;
    try { types = a.GetTypes(); }
    catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t is not null).ToArray()!; }
    catch { continue; }

    foreach (var t in types)
    {
        var n = NameOf(t);
        if (n is not null) all.Add((a, t, n));
    }
}

foreach (var needle in needles)
{
    var matches = all
        .Where(x => x.name.Contains(needle, StringComparison.OrdinalIgnoreCase))
        .OrderBy(x => x.name.Length)
        .ToList();

    if (listTypes)
    {
        Console.WriteLine($"### types matching '{needle}' ({matches.Count})");
        foreach (var m in matches.Take(300))
            Console.WriteLine($"  {m.name}   [{m.asm.GetName().Name}]");
        Console.WriteLine();
        continue;
    }

    var chosen = matches.FirstOrDefault(x =>
        string.Equals(x.type.Name, needle, StringComparison.OrdinalIgnoreCase));
    if (chosen.type is null) chosen = matches.FirstOrDefault();
    if (chosen.type is null) { Console.WriteLine($"### '{needle}': no match\n"); continue; }

    var t = chosen.type;
    Console.WriteLine($"### {chosen.name}   [{chosen.asm.GetName().Name}]   base {t.BaseType?.Name}");
    if (matches.Count > 1)
        Console.WriteLine($"    ({matches.Count} types matched this substring; showing the closest name)");

    const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic
                             | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    try
    {
        foreach (var f in t.GetFields(Flags).Where(f => f.IsPublic))
            Console.WriteLine($"  field   {Sig(f.FieldType)} {f.Name}{(f.IsStatic ? "  [static]" : "")}");

        foreach (var p in t.GetProperties(Flags))
            Console.WriteLine($"  prop    {Sig(p.PropertyType)} {p.Name}");

        foreach (var m in t.GetMethods(Flags).Where(m => m.IsPublic).OrderBy(m => m.Name))
            Console.WriteLine($"  method  {Sig(m.ReturnType)} {m.Name}("
                + string.Join(", ", m.GetParameters().Select(q => $"{Sig(q.ParameterType)} {q.Name}"))
                + $"){(m.IsStatic ? "  [static]" : "")}");
    }
    catch (Exception e)
    {
        Console.WriteLine($"  (members unreadable: {e.GetType().Name}: {e.Message})");
    }

    Console.WriteLine();
}

return 0;

// Generic arguments are the whole point when the target is Il2CppInterop: knowing a
// parameter is List`1 is useless, knowing it is List<SubsystemDescriptor> is the answer.
static string Sig(Type t)
{
    try
    {
        if (t.IsByRef) return Sig(t.GetElementType()!) + "&";
        if (t.IsArray) return Sig(t.GetElementType()!) + "[]";
        if (!t.IsGenericType) return t.Name;
        var name = t.Name;
        var tick = name.IndexOf('`');
        if (tick > 0) name = name[..tick];
        return name + "<" + string.Join(", ", t.GetGenericArguments().Select(Sig)) + ">";
    }
    catch { return "?"; }
}
