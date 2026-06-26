using System.Reflection;
using System.Runtime.Serialization;

namespace NPC_Plugin_Chooser_2.Tests.TestSupport;

/// <summary>
/// Minimal reflection helpers for exercising private members and for building
/// heavy-constructor services without running their constructor. Used sparingly —
/// preferred order is (1) public/internal API, (2) these helpers — so the suite can
/// reach deterministic logic buried in stateful classes without altering production code.
/// </summary>
public static class Reflect
{
    private const BindingFlags All =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    /// <summary>Allocates an instance without running any constructor (fields default-initialised).</summary>
    public static T Uninitialized<T>() => (T)FormatterServices.GetUninitializedObject(typeof(T));

    /// <summary>Sets a private/internal instance field by name.</summary>
    public static void SetField(object target, string name, object? value)
    {
        var f = FindField(target.GetType(), name)
                ?? throw new MissingFieldException(target.GetType().FullName, name);
        f.SetValue(target, value);
    }

    /// <summary>Gets a private/internal instance field by name.</summary>
    public static T GetField<T>(object target, string name)
    {
        var f = FindField(target.GetType(), name)
                ?? throw new MissingFieldException(target.GetType().FullName, name);
        return (T)f.GetValue(target)!;
    }

    /// <summary>Invokes a private/internal instance method by name.</summary>
    public static T? Invoke<T>(object target, string name, params object?[] args)
    {
        var m = FindMethod(target.GetType(), name, args.Length)
                ?? throw new MissingMethodException(target.GetType().FullName, name);
        return (T?)m.Invoke(target, args);
    }

    /// <summary>Invokes a private/internal instance method by name, ignoring the return value.</summary>
    public static void InvokeVoid(object target, string name, params object?[] args)
    {
        var m = FindMethod(target.GetType(), name, args.Length)
                ?? throw new MissingMethodException(target.GetType().FullName, name);
        m.Invoke(target, args);
    }

    /// <summary>Invokes a private/internal static method on <typeparamref name="TOwner"/>.</summary>
    public static T? InvokeStatic<TOwner, T>(string name, params object?[] args)
    {
        var m = FindMethod(typeof(TOwner), name, args.Length)
                ?? throw new MissingMethodException(typeof(TOwner).FullName, name);
        return (T?)m.Invoke(null, args);
    }

    /// <summary>
    /// Invokes a private/internal static method on <paramref name="owner"/>, passed as a
    /// <see cref="Type"/> so static classes (which can't be generic type arguments) are supported.
    /// </summary>
    public static T? InvokeStatic<T>(Type owner, string name, params object?[] args)
    {
        var m = FindMethod(owner, name, args.Length)
                ?? throw new MissingMethodException(owner.FullName, name);
        return (T?)m.Invoke(null, args);
    }

    private static FieldInfo? FindField(Type? t, string name)
    {
        // Exact name first.
        for (var ty = t; ty != null; ty = ty.BaseType)
        {
            var f = ty.GetField(name, All);
            if (f != null) return f;
        }

        // Resolve the logical property name from a mangled/decorated name, then try the
        // common backing-field spellings — including ReactiveUI.Fody's "$Prop" form (which
        // [Reactive] auto-properties use) and the C# auto-property "<Prop>k__BackingField" form.
        var core = name;
        var m = System.Text.RegularExpressions.Regex.Match(name, @"^<(.+)>k__BackingField$");
        if (m.Success) core = m.Groups[1].Value;
        else if (name.Length > 1 && (name[0] == '$' || name[0] == '_')) core = name.Substring(1);

        var candidates = new[] { "$" + core, "_" + core, core, "<" + core + ">k__BackingField" };
        for (var ty = t; ty != null; ty = ty.BaseType)
        {
            foreach (var c in candidates)
            {
                var f = ty.GetField(c, All);
                if (f != null) return f;
            }
        }
        return null;
    }

    private static MethodInfo? FindMethod(Type? t, string name, int argCount)
    {
        for (; t != null; t = t.BaseType)
        {
            var candidates = t.GetMethods(All).Where(m => m.Name == name && m.GetParameters().Length == argCount).ToList();
            if (candidates.Count > 0) return candidates[0];
        }
        return null;
    }
}
