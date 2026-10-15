using System;
using System.Collections;
using System.Globalization;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

// Compare against the existing serialized baseline without creating a second JSON
// tree/string every poll. Use the same Json.NET contracts as the room serializer.
public static class CoopSettingsComparison
{
    private static readonly Dictionary<JsonProperty, Func<object, JToken, bool>> scalarMembers = new Dictionary<JsonProperty, Func<object, JToken, bool>>();
    private static Func<object, JToken, bool> ScalarMember(JsonProperty property)
    {
        if (scalarMembers.TryGetValue(property, out var cached)) return cached;
        Func<object, JToken, bool> result = null;
#if !ENABLE_IL2CPP
        Type type = property.PropertyType;
        if (type == typeof(int) || type == typeof(float) || type == typeof(bool) || type == typeof(string))
        {
            FieldInfo field = property.DeclaringType.GetField(property.UnderlyingName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field != null)
            {
                var target = Expression.Parameter(typeof(object)); var baseline = Expression.Parameter(typeof(JToken));
                var actual = Expression.Field(Expression.Convert(target, field.DeclaringType), field);
                var expected = Expression.Convert(baseline, type);
                Expression comparison = type == typeof(float)
                    ? (Expression)Expression.Call(actual, typeof(float).GetMethod("Equals", new[] { typeof(float) }), expected)
                    : Expression.Equal(actual, expected);
                result = Expression.Lambda<Func<object, JToken, bool>>(comparison, target, baseline).Compile();
            }
        }
#endif
        scalarMembers[property] = result; return result;
    }
    // The deep comparison walks the whole settings tree four times a second. Reading a
    // member through JsonProperty.ValueProvider is reflective; compiling the accessor
    // once keeps the semantics identical while removing the reflection from the loop.
    private static readonly Dictionary<JsonProperty, Func<object, object>> memberGetters = new Dictionary<JsonProperty, Func<object, object>>();
    private static Func<object, object> MemberGetter(JsonProperty property)
    {
        if (memberGetters.TryGetValue(property, out var cached)) return cached;
        Func<object, object> result = null;
#if !ENABLE_IL2CPP
        Type type = property.DeclaringType; string name = property.UnderlyingName;
        if (type != null && !string.IsNullOrEmpty(name))
        {
            MemberInfo member = (MemberInfo)type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) ??
                type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (member != null)
            {
                var target = Expression.Parameter(typeof(object));
                result = Expression.Lambda<Func<object, object>>(
                    Expression.Convert(Expression.MakeMemberAccess(Expression.Convert(target, type), member), typeof(object)), target).Compile();
            }
        }
#endif
        memberGetters[property] = result; return result;
    }
    public static bool Matches(object value, JToken baseline, IContractResolver resolver, int depth = 0)
    {
        if (depth > 64) throw new InvalidOperationException("Room setting nesting exceeds limit");
        if (value == null) return baseline == null || baseline.Type == JTokenType.Null;
        if (baseline == null || baseline.Type == JTokenType.Null) return false;
        if (baseline is JValue scalar)
        {
            switch (baseline.Type)
            {
                case JTokenType.Integer:
                    // Integral room values dominate the tree; the decimal conversions
                    // below are the slow fallback for exotic numeric types only.
                    if (scalar.Value is long exact)
                    {
                        if (value is int integer) return integer == exact;
                        if (value is long wide) return wide == exact;
                        if (value is short narrow) return narrow == exact;
                        if (value is byte octet) return octet == exact;
                        if (value is sbyte signedOctet) return signedOctet == exact;
                        if (value is uint unsigned) return unsigned == exact;
                        if (value is char character) return character == exact;
                    }
                    return Convert.ToDecimal(value, CultureInfo.InvariantCulture) == Convert.ToDecimal(scalar.Value, CultureInfo.InvariantCulture);
                case JTokenType.Float:
                    if (value is float single) return single.Equals(Convert.ToSingle(scalar.Value, CultureInfo.InvariantCulture));
                    if (value is decimal number) return number == Convert.ToDecimal(scalar.Value, CultureInfo.InvariantCulture);
                    return Convert.ToDouble(value, CultureInfo.InvariantCulture).Equals(Convert.ToDouble(scalar.Value, CultureInfo.InvariantCulture));
                default: return Equals(value, scalar.Value);
            }
        }
        if (baseline is JArray array)
        {
            if (value is int[] integers)
            {
                if (integers.Length != array.Count) return false;
                for (int i = 0; i < integers.Length; i++) if (integers[i] != (int)array[i]) return false;
                return true;
            }
            if (value is List<int> integerList)
            {
                if (integerList.Count != array.Count) return false;
                for (int i = 0; i < integerList.Count; i++) if (integerList[i] != (int)array[i]) return false;
                return true;
            }
            if (value is List<bool> flags)
            {
                if (flags.Count != array.Count) return false;
                for (int i = 0; i < flags.Count; i++) if (flags[i] != (bool)array[i]) return false;
                return true;
            }
            if (value is IList list)
            {
                if (list.Count != array.Count) return false;
                for (int i = 0; i < list.Count; i++) if (!Matches(list[i], array[i], resolver, depth + 1)) return false;
                return true;
            }
            if (!(value is IEnumerable sequence)) return false;
            int index = 0;
            foreach (object item in sequence)
            {
                if (index >= array.Count || !Matches(item, array[index++], resolver, depth + 1)) return false;
            }
            return index == array.Count;
        }
        if (!(baseline is JObject obj)) return false;
        if (value is IDictionary dictionary)
        {
            if (dictionary.Count != obj.Count) return false;
            foreach (DictionaryEntry entry in dictionary)
            {
                string key = Convert.ToString(entry.Key, CultureInfo.InvariantCulture);
                if (!obj.TryGetValue(key, out JToken expected) || !Matches(entry.Value, expected, resolver, depth + 1)) return false;
            }
            return true;
        }
        var contract = resolver.ResolveContract(value.GetType()) as JsonObjectContract;
        if (contract == null) return false;
        foreach (JProperty property in obj.Properties())
        {
            JsonProperty member = contract.Properties.GetClosestMatchProperty(property.Name);
            if (member == null || member.Ignored || !member.Readable) return false;
            var compare = ScalarMember(member);
            if (compare != null && (property.Value.Type != JTokenType.Null || member.PropertyType == typeof(string)))
            { if (!compare(value, property.Value)) return false; }
            else
            {
                var getter = MemberGetter(member);
                object memberValue = getter != null ? getter(value) : member.ValueProvider.GetValue(value);
                if (!Matches(memberValue, property.Value, resolver, depth + 1)) return false;
            }
        }
        return true;
    }
}
