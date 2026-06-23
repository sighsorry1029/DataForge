using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace DataForge;

internal static class ReferenceValue
{
    internal static T? ClonePruned<T>(T? source) where T : class
    {
        return CloneValue(source, typeof(T), "") as T;
    }

    private static object? CloneValue(object? source, Type targetType, string propertyName)
    {
        if (source == null)
        {
            return null;
        }

        Type valueType = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (valueType == typeof(string))
        {
            return ReferenceDefaultRules.IsDefaultString((string)source, propertyName) ? null : source;
        }

        if (valueType == typeof(bool))
        {
            bool value = (bool)source;
            return value == ReferenceDefaultRules.DefaultBool(propertyName) ? null : value;
        }

        if (valueType == typeof(int))
        {
            int value = (int)source;
            return value == ReferenceDefaultRules.DefaultInt(propertyName) ? null : value;
        }

        if (valueType == typeof(float))
        {
            float value = (float)source;
            return ReferenceDefaultRules.IsDefaultFloat(value, propertyName) ? null : value;
        }

        if (source is IDictionary dictionary)
        {
            return dictionary.Count == 0 ? null : source;
        }

        if (typeof(IEnumerable).IsAssignableFrom(valueType) && valueType != typeof(string))
        {
            return CloneList(source);
        }

        if (valueType.IsValueType)
        {
            object defaultValue = Activator.CreateInstance(valueType)!;
            return source.Equals(defaultValue) ? null : source;
        }

        object clone = Activator.CreateInstance(valueType)!;
        bool hasContent = false;
        foreach (PropertyInfo property in valueType.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (!property.CanRead || !property.CanWrite || property.GetIndexParameters().Length != 0)
            {
                continue;
            }

            object? child = property.GetValue(source);
            object? prunedChild = CloneValue(child, property.PropertyType, property.Name);
            if (prunedChild == null)
            {
                continue;
            }

            property.SetValue(clone, prunedChild);
            hasContent = true;
        }

        return hasContent ? clone : null;
    }

    private static object? CloneList(object source)
    {
        Type? elementType = FindElementType(source.GetType());
        if (elementType == null)
        {
            return null;
        }

        IList clone = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(elementType))!;
        foreach (object? item in (IEnumerable)source)
        {
            object? prunedItem = CloneValue(item, elementType, "");
            if (prunedItem != null)
            {
                clone.Add(prunedItem);
            }
        }

        return clone.Count > 0 ? clone : null;
    }

    private static Type? FindElementType(Type type)
    {
        if (type.IsArray)
        {
            return type.GetElementType();
        }

        if (type.IsGenericType && type.GetGenericArguments().Length == 1)
        {
            return type.GetGenericArguments()[0];
        }

        return type.GetInterfaces()
            .Where(interfaceType => interfaceType.IsGenericType && interfaceType.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            .Select(interfaceType => interfaceType.GetGenericArguments()[0])
            .FirstOrDefault();
    }
}
