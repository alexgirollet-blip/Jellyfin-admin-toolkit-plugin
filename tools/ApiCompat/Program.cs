using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

if (args.Length == 2 && args[0] == "--list" && File.Exists(args[1]))
{
    // Print every Jellyfin type member referenced by the plugin DLL (from its metadata).
    foreach (var line in UsedMembers.List(args[1]))
    {
        Console.WriteLine(line);
    }

    return 0;
}

if (args.Length != 1 || !File.Exists(args[0]))
{
    Console.Error.WriteLine("usage: ApiCompat [--list] <path to Jellyfin.Plugin.AdminToolkit.dll>");
    return 2;
}

var jellyfin = typeof(MediaBrowser.Controller.Library.IUserManager).Assembly.GetName().Version;
Console.WriteLine($"Checking {Path.GetFileName(args[0])} against Jellyfin runtime assemblies {jellyfin}");

Assembly plugin;
try
{
    plugin = Assembly.LoadFrom(Path.GetFullPath(args[0]));
}
catch (Exception ex)
{
    Console.Error.WriteLine("FAIL cannot load plugin: " + ex.Message);
    return 1;
}

var failures = new List<string>();
Type[] types;
try
{
    types = plugin.GetTypes();
}
catch (ReflectionTypeLoadException ex)
{
    foreach (var le in ex.LoaderExceptions.Where(e => e is not null))
    {
        failures.Add("type load: " + le!.Message);
    }

    types = ex.Types.Where(t => t is not null).ToArray()!;
}

int prepared = 0, skipped = 0;
const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

foreach (var type in types)
{
    var concrete = Close(type);
    if (concrete is null)
    {
        skipped++;
        continue;
    }

    IEnumerable<MethodBase> methods;
    try
    {
        methods = concrete.GetMethods(All).Cast<MethodBase>().Concat(concrete.GetConstructors(All));
        // Touch field and property types so missing types in signatures are found too.
        foreach (var f in concrete.GetFields(All)) { _ = f.FieldType; }
        foreach (var p in concrete.GetProperties(All)) { _ = p.PropertyType; }
    }
    catch (Exception ex)
    {
        failures.Add($"{type.FullName}: {ex.GetType().Name}: {ex.Message}");
        continue;
    }

    foreach (var m in methods)
    {
        if (m.IsAbstract || (m.MethodImplementationFlags & MethodImplAttributes.InternalCall) != 0)
        {
            continue;
        }

        MethodBase target = m;
        if (m is MethodInfo mi && mi.IsGenericMethodDefinition)
        {
            try
            {
                target = mi.MakeGenericMethod(mi.GetGenericArguments().Select(_ => typeof(object)).ToArray());
            }
            catch (ArgumentException)
            {
                skipped++;
                continue;
            }
        }

        try
        {
            RuntimeHelpers.PrepareMethod(target.MethodHandle, (target.DeclaringType?.IsGenericType ?? false) ? target.DeclaringType!.GetGenericArguments().Select(t => t.TypeHandle).ToArray() : null);
            prepared++;
        }
        catch (Exception ex) when (ex is MissingMemberException or TypeLoadException or FileNotFoundException or FileLoadException or BadImageFormatException or InvalidProgramException)
        {
            failures.Add($"{type.FullName}.{m.Name}: {ex.GetType().Name}: {ex.Message}");
        }
        catch (Exception ex)
        {
            // Other exceptions (e.g. from static constructors) are not binding problems; report but do not fail.
            Console.WriteLine($"note {type.FullName}.{m.Name}: {ex.GetType().Name}: {ex.Message}");
        }
    }
}

Console.WriteLine($"JIT-compiled {prepared} methods, skipped {skipped} open generic items.");
if (failures.Count > 0)
{
    Console.Error.WriteLine($"FAIL: {failures.Count} member(s) do not bind against Jellyfin {jellyfin}:");
    foreach (var f in failures.Distinct())
    {
        Console.Error.WriteLine("  " + f);
    }

    return 1;
}

Console.WriteLine($"OK: every Jellyfin member used by the plugin exists in Jellyfin {jellyfin}.");
return 0;

static Type? Close(Type t)
{
    if (!t.IsGenericTypeDefinition)
    {
        return t;
    }

    try
    {
        return t.MakeGenericType(t.GetGenericArguments().Select(_ => typeof(object)).ToArray());
    }
    catch (ArgumentException)
    {
        return null;
    }
}

static class UsedMembers
{
    public static IEnumerable<string> List(string path)
    {
        using var fs = File.OpenRead(path);
        using var pe = new System.Reflection.PortableExecutable.PEReader(fs);
        var md = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
        var result = new SortedSet<string>(StringComparer.Ordinal);

        string? Scope(System.Reflection.Metadata.TypeReferenceHandle h)
        {
            var tr = md.GetTypeReference(h);
            var scope = tr.ResolutionScope;
            if (scope.Kind == System.Reflection.Metadata.HandleKind.AssemblyReference)
            {
                return md.GetString(md.GetAssemblyReference((System.Reflection.Metadata.AssemblyReferenceHandle)scope).Name);
            }

            if (scope.Kind == System.Reflection.Metadata.HandleKind.TypeReference)
            {
                return Scope((System.Reflection.Metadata.TypeReferenceHandle)scope);
            }

            return null;
        }

        string Name(System.Reflection.Metadata.TypeReferenceHandle h)
        {
            var tr = md.GetTypeReference(h);
            var ns = md.GetString(tr.Namespace);
            var n = md.GetString(tr.Name);
            if (tr.ResolutionScope.Kind == System.Reflection.Metadata.HandleKind.TypeReference)
            {
                return Name((System.Reflection.Metadata.TypeReferenceHandle)tr.ResolutionScope) + "+" + n;
            }

            return string.IsNullOrEmpty(ns) ? n : ns + "." + n;
        }

        static bool IsJellyfin(string? asm) => asm is not null && (asm.StartsWith("Jellyfin.", StringComparison.Ordinal) || asm.StartsWith("MediaBrowser.", StringComparison.Ordinal));

        foreach (var h in md.TypeReferences)
        {
            var asm = Scope(h);
            if (IsJellyfin(asm))
            {
                result.Add($"[{asm}] type {Name(h)}");
            }
        }

        foreach (var h in md.MemberReferences)
        {
            var mr = md.GetMemberReference(h);
            System.Reflection.Metadata.TypeReferenceHandle? parent = mr.Parent.Kind switch
            {
                System.Reflection.Metadata.HandleKind.TypeReference => (System.Reflection.Metadata.TypeReferenceHandle)mr.Parent,
                System.Reflection.Metadata.HandleKind.TypeSpecification => null,
                _ => null
            };
            if (parent is null)
            {
                continue;
            }

            var asm = Scope(parent.Value);
            if (IsJellyfin(asm))
            {
                result.Add($"[{asm}] {Name(parent.Value)}::{md.GetString(mr.Name)}");
            }
        }

        return result;
    }
}
