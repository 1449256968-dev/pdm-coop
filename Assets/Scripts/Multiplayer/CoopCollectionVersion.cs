using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

// Detect local writes to exposed BCL collections without deserializing unchanged
// contents. Arrays/custom enumerables have no reliable mutation version and fall back.
public static class CoopCollectionVersion
{
    private static readonly Dictionary<Type, Func<object, int>> readers = new Dictionary<Type, Func<object, int>>();
    public static Func<object, int> For(Type type)
    {
        if (readers.TryGetValue(type, out Func<object, int> reader)) return reader;
        if (!type.IsGenericType) { readers[type] = null; return null; }
        Type definition = type.GetGenericTypeDefinition();
        if (definition != typeof(List<>) && definition != typeof(Dictionary<,>) && definition != typeof(HashSet<>))
        { readers[type] = null; return null; }
        FieldInfo field = type.GetField("_version", BindingFlags.Instance | BindingFlags.NonPublic) ??
            type.GetField("version", BindingFlags.Instance | BindingFlags.NonPublic) ??
            type.GetField("m_version", BindingFlags.Instance | BindingFlags.NonPublic);
        if (field == null || field.FieldType != typeof(int)) { readers[type] = null; return null; }
#if ENABLE_IL2CPP
        reader = value => (int)field.GetValue(value);
#else
        var method = new DynamicMethod("CoopCollectionVersion", typeof(int), new[] { typeof(object) }, typeof(CoopCollectionVersion).Module, true);
        ILGenerator il = method.GetILGenerator(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Castclass, type);
        il.Emit(OpCodes.Ldfld, field); il.Emit(OpCodes.Ret);
        reader = (Func<object, int>)method.CreateDelegate(typeof(Func<object, int>));
#endif
        readers[type] = reader; return reader;
    }
}
