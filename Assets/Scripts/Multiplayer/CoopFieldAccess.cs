using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

// Compile each field operation once on Mono. Preserve FieldInfo ordering and boxed
// value-type mutation; no constructors, callbacks, properties or Unity APIs run here.
public static class CoopFieldAccess
{
    public sealed class Member
    {
        public FieldInfo field;
        public Func<object, object> get;
        public Action<object, object> set;
    }
    private static readonly Dictionary<Type, Member[]> cache = new Dictionary<Type, Member[]>();
    public static Member[] For(Type type, FieldInfo[] fields)
    {
        if (cache.TryGetValue(type, out Member[] result)) return result;
        result = new Member[fields.Length];
        for (int i = 0; i < fields.Length; i++)
        {
            FieldInfo field = fields[i];
            var member = new Member { field = field, get = field.GetValue, set = field.SetValue };
#if !ENABLE_IL2CPP
            var getter = new DynamicMethod("CoopRead_" + field.Name, typeof(object), new[] { typeof(object) }, typeof(CoopFieldAccess), true);
            ILGenerator il = getter.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(field.DeclaringType.IsValueType ? OpCodes.Unbox : OpCodes.Castclass, field.DeclaringType);
            il.Emit(OpCodes.Ldfld, field);
            if (field.FieldType.IsValueType) il.Emit(OpCodes.Box, field.FieldType);
            il.Emit(OpCodes.Ret);
            member.get = (Func<object, object>)getter.CreateDelegate(typeof(Func<object, object>));
            // Keep reflection's behavior for readonly fields in plain state classes.
            if (!field.IsInitOnly)
            {
                var setter = new DynamicMethod("CoopWrite_" + field.Name, typeof(void), new[] { typeof(object), typeof(object) }, typeof(CoopFieldAccess), true);
                il = setter.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(field.DeclaringType.IsValueType ? OpCodes.Unbox : OpCodes.Castclass, field.DeclaringType);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(field.FieldType.IsValueType ? OpCodes.Unbox_Any : OpCodes.Castclass, field.FieldType);
                il.Emit(OpCodes.Stfld, field); il.Emit(OpCodes.Ret);
                member.set = (Action<object, object>)setter.CreateDelegate(typeof(Action<object, object>));
            }
#endif
            result[i] = member;
        }
        cache[type] = result; return result;
    }
}
