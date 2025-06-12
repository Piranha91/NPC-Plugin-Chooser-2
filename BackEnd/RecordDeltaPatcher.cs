// RecordDeltaPatcher.cs
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins.Records;
using NPC_Plugin_Chooser_2.Models;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Mutagen.Bethesda.Plugins;

namespace NPC_Plugin_Chooser_2.BackEnd
{
    public class RecordDeltaPatcher : OptionalUIModule
    {
        #region Fields and Initialization
        private readonly Dictionary<Type, IReadOnlyDictionary<string, PropertyInfo>> _getterCache = new();
        private readonly Dictionary<Type, IReadOnlyDictionary<string, PropertyInfo>> _setterCache = new();
        private readonly Dictionary<FormKey, Dictionary<string, object?>> _patchedValues = new();

        public void Reinitialize()
        {
            _patchedValues.Clear();
        }
        #endregion

        #region Property Diff Classes
        public abstract class PropertyDiff
        {
            public string PropertyName { get; }
            protected PropertyDiff(string propertyName) => PropertyName = propertyName;
            public abstract void Apply(MajorRecord destinationRecord, PropertyInfo setter);
            public abstract object? GetValue();
        }

        public class ValueDiff : PropertyDiff
        {
            public object? NewValue { get; }
            public ValueDiff(string propertyName, object? newValue) : base(propertyName) => NewValue = newValue;

            public override void Apply(MajorRecord destinationRecord, PropertyInfo setter)
            {
                if (NewValue == null)
                {
                    setter.SetValue(destinationRecord, null);
                    return;
                }

                if (setter.PropertyType.IsAssignableFrom(NewValue.GetType()))
                {
                    setter.SetValue(destinationRecord, NewValue);
                }
                else
                {
                    var destStruct = setter.GetValue(destinationRecord);
                    if (destStruct == null)
                    {
                        destStruct = Activator.CreateInstance(setter.PropertyType);
                        setter.SetValue(destinationRecord, destStruct);
                    }
                    // Use the shared, recursive helper
                    CopyPropertiesRecursively(NewValue, destStruct);
                }
            }
            public override object? GetValue() => NewValue;
        }

        public class ListDiff : PropertyDiff
        {
            public IReadOnlyList<object> Items { get; }
            public ListDiff(string propertyName, IEnumerable sourceList) : base(propertyName)
            {
                Items = sourceList.Cast<object>().ToList();
            }

            public override void Apply(MajorRecord destinationRecord, PropertyInfo setter)
            {
                if (setter.GetValue(destinationRecord) is not IList destinationList) return;

                var destItemType = destinationList.GetType().GetGenericArguments()[0];
                destinationList.Clear();

                foreach (var sourceItem in Items)
                {
                    if (sourceItem == null) continue;

                    // --- REVISED LOGIC ---
                    // Case 1: The item is a simple, assignable type like FormLink, string, or enum.
                    if (destItemType.IsAssignableFrom(sourceItem.GetType()))
                    {
                        destinationList.Add(sourceItem);
                    }
                    // Case 2: The item is a complex struct that needs a deep copy.
                    else
                    {
                        var concreteItem = Activator.CreateInstance(destItemType);
                        CopyPropertiesRecursively(sourceItem, concreteItem);
                        destinationList.Add(concreteItem);
                    }
                }
            }
            public override object? GetValue() => Items;
        }
        #endregion

        #region Public Interface
        public IReadOnlyList<PropertyDiff> GetPropertyDiffs<TGetter>(TGetter source, TGetter target)
            where TGetter : class, IMajorRecordGetter
        {
            if (source is null) throw new ArgumentNullException(nameof(source));
            if (target is null) throw new ArgumentNullException(nameof(target));

            var differences = new List<PropertyDiff>();
            var getterProps = GetGetterProperties(typeof(TGetter));

            foreach (var pi in getterProps.Values)
            {
                object? sourceValue = pi.GetValue(source);
                object? targetValue = pi.GetValue(target);

                if (sourceValue is IEnumerable sourceList && sourceValue is not string)
                {
                    var targetList = targetValue as IEnumerable ?? Enumerable.Empty<object>();
                    if (!sourceList.Cast<object>().SequenceEqual(targetList.Cast<object>()))
                    {
                        differences.Add(new ListDiff(pi.Name, sourceList));
                    }
                }
                else if (!Equals(sourceValue, targetValue))
                {
                    differences.Add(new ValueDiff(pi.Name, sourceValue));
                }
            }
            return differences;
        }

        public void ApplyPropertyDiffs<TRecord>(TRecord destination, IEnumerable<PropertyDiff> diffs)
            where TRecord : MajorRecord, IMajorRecordGetter
        {
            if (destination is null) throw new ArgumentNullException(nameof(destination));
            if (diffs is null) throw new ArgumentNullException(nameof(diffs));

            var setterProps = GetSetterProperties(typeof(TRecord));

            if (!_patchedValues.ContainsKey(destination.FormKey))
            {
                _patchedValues[destination.FormKey] = new Dictionary<string, object?>();
            }
            var recordPatchedValues = _patchedValues[destination.FormKey];

            foreach (var diff in diffs)
            {
                if (!setterProps.TryGetValue(diff.PropertyName, out var dstPi)) continue;

                object? newValue = diff.GetValue();

                if (recordPatchedValues.TryGetValue(diff.PropertyName, out var oldValue))
                {
                    bool isConflict = false;
                    if (oldValue is IReadOnlyList<object> oldList && newValue is IReadOnlyList<object> newList)
                    {
                        if (!oldList.SequenceEqual(newList)) isConflict = true;
                    }
                    else if (!Equals(oldValue, newValue))
                    {
                        isConflict = true;
                    }

                    if (isConflict)
                    {
                        AppendLog($"CONFLICT: Property '{diff.PropertyName}' on record '{destination.EditorID ?? destination.FormKey.ToString()}' is being overwritten with a different value.");
                    }
                }

                recordPatchedValues[diff.PropertyName] = newValue;
                diff.Apply(destination, dstPi);
            }
        }
        #endregion

        #region Core Recursive Copy Logic
        /// <summary>
        /// A shared, recursive helper that performs a deep copy of properties from a source
        /// getter object to a destination concrete object.
        /// </summary>
        private static void CopyPropertiesRecursively(object source, object destination)
        {
            if (source == null || destination == null) return;

            var destProps = destination.GetType()
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanWrite)
                .ToDictionary(p => p.Name);

            var sourceProps = source.GetType()
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead);

            foreach (var srcProp in sourceProps)
            {
                if (!destProps.TryGetValue(srcProp.Name, out var destProp)) continue;

                var sourceValue = srcProp.GetValue(source);
                if (sourceValue == null)
                {
                    destProp.SetValue(destination, null);
                    continue;
                }

                // Case 1: Simple types that can be assigned directly.
                if (destProp.PropertyType.IsAssignableFrom(sourceValue.GetType()))
                {
                    destProp.SetValue(destination, sourceValue);
                }
                // Case 2: Lists.
                else if (sourceValue is IEnumerable sourceList && sourceValue is not string)
                {
                    if (destProp.GetValue(destination) is not IList destList) continue;

                    var destItemType = destList.GetType().GetGenericArguments()[0];
                    destList.Clear();

                    foreach (var sourceListItem in sourceList)
                    {
                        var destListItem = Activator.CreateInstance(destItemType);
                        CopyPropertiesRecursively(sourceListItem, destListItem); // Recurse on list items
                        destList.Add(destListItem);
                    }
                }
                // Case 3: Nested complex types that need their own properties copied.
                else
                {
                    var nestedDest = destProp.GetValue(destination);
                    if (nestedDest == null)
                    {
                        nestedDest = Activator.CreateInstance(destProp.PropertyType);
                        destProp.SetValue(destination, nestedDest);
                    }
                    CopyPropertiesRecursively(sourceValue, nestedDest); // The recursive call for nested structs
                }
            }
        }
        #endregion

        #region Reflection Caching
        private IReadOnlyDictionary<string, PropertyInfo> GetGetterProperties(Type getterType)
        {
            if (_getterCache.TryGetValue(getterType, out var cachedProperties))
            {
                return cachedProperties;
            }

            var newProperties = getterType.GetInterfaces().Prepend(getterType)
                .SelectMany(i => i.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                .GroupBy(p => p.Name)
                .Select(g => g.First())
                .ToDictionary(p => p.Name);

            _getterCache[getterType] = newProperties;
            return newProperties;
        }

        private IReadOnlyDictionary<string, PropertyInfo> GetSetterProperties(Type recordType)
        {
            if (_setterCache.TryGetValue(recordType, out var cachedProperties))
            {
                return cachedProperties;
            }

            var newProperties = recordType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanWrite)
                .ToDictionary(p => p.Name);

            _setterCache[recordType] = newProperties;
            return newProperties;
        }
        #endregion
    }
}