// RecordDeltaPatcher.cs
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins.Records;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Mutagen.Bethesda.Plugins; 
using Noggog; 

namespace NPC_Plugin_Chooser_2.BackEnd
{
    public class RecordDeltaPatcher : OptionalUIModule
    {
        #region Fields and Initialization
        private readonly Dictionary<Type, IReadOnlyDictionary<string, PropertyInfo>> _getterCache = new();
        private readonly Dictionary<Type, IReadOnlyDictionary<string, PropertyInfo>> _setterCache = new();
        private readonly Dictionary<FormKey, Dictionary<string, object?>> _patchedValues = new();

        public void Reinitialize() { _patchedValues.Clear(); }
        #endregion

        #region Property Diff Classes
        public abstract class PropertyDiff
        {
            public string PropertyName { get; }
            protected PropertyDiff(string propertyName) => PropertyName = propertyName;
            public abstract void Apply(MajorRecord destinationRecord, PropertyInfo destPropInfo, RecordDeltaPatcher patcher);
            public abstract object? GetValue();
        }

        public class ValueDiff : PropertyDiff
        {
            public object? NewValue { get; }
            public ValueDiff(string propertyName, object? newValue) : base(propertyName) => NewValue = newValue;

            public override void Apply(MajorRecord destinationRecord, PropertyInfo destPropInfo, RecordDeltaPatcher patcher)
            {
                var processedObjects = new HashSet<object>(ReferenceEqualityComparer.Instance); // Initialize for this top-level Apply

                if (NewValue == null) {
                    if (destPropInfo.CanWrite) destPropInfo.SetValue(destinationRecord, null);
                    else { /* ... clear collection logic ... */ } return;
                }
                Type newValueType = NewValue.GetType(); Type destPropertyType = destPropInfo.PropertyType;
                if (destPropInfo.CanWrite) {
                    patcher.AppendLog($"DEBUG (ValueDiff.Apply): Applying to writable property '{PropertyName}'.");
                    if (destPropertyType.IsAssignableFrom(newValueType)) destPropInfo.SetValue(destinationRecord, NewValue);
                    else {
                        object? convertedInstance = patcher.TryCreateInstanceFromGetter(destPropertyType, NewValue);
                        if (convertedInstance != null) {
                            destPropInfo.SetValue(destinationRecord, convertedInstance);
                            if (!patcher.IsSimpleType(destPropertyType) && !patcher.IsSimpleType(newValueType) && destPropertyType != newValueType) {
                               object? targetForRecursion = destPropInfo.GetValue(destinationRecord);
                               if (targetForRecursion != null) { patcher.CopyPropertiesRecursively(NewValue, targetForRecursion, processedObjects); if (destPropertyType.IsValueType) destPropInfo.SetValue(destinationRecord, targetForRecursion); }
                            }
                        } else { /* ... create, recurse, set logic for writable complex ... */
                            object? destinationPropertyValue = destPropInfo.GetValue(destinationRecord); bool newInstanceCreated = false;
                            if (destinationPropertyValue == null && !destPropertyType.IsValueType) { /* ... create class ... */ try { destinationPropertyValue = Activator.CreateInstance(destPropertyType); newInstanceCreated = true; } catch { /* log */ return;} }
                            else if (destPropertyType.IsValueType) { /* ... create struct ... */ destinationPropertyValue = Activator.CreateInstance(destPropertyType); } // Simplified
                            if (destinationPropertyValue == null) { /* log */ return; }
                            patcher.CopyPropertiesRecursively(NewValue, destinationPropertyValue, processedObjects);
                            if (newInstanceCreated || destPropertyType.IsValueType) destPropInfo.SetValue(destinationRecord, destinationPropertyValue);
                        }
                    }
                } else { 
                    patcher.AppendLog($"DEBUG (ValueDiff.Apply): Applying to read-only property '{PropertyName}'. Type: {destPropertyType.FullName}");
                    if (typeof(IDictionary).IsAssignableFrom(destPropertyType) && NewValue is IEnumerable sourceDictEnum) {
                        patcher.CopyDictionaryProperty(sourceDictEnum, destPropInfo, destinationRecord, processedObjects);
                    } else if (typeof(IList).IsAssignableFrom(destPropertyType) && NewValue is IEnumerable sourceListEnum) {
                        patcher.CopyListProperty(sourceListEnum, destPropInfo, destinationRecord, processedObjects);
                    } else { /* ... read-only complex object (non-collection) ... */
                        object? readOnlyDestInstance = destPropInfo.GetValue(destinationRecord);
                        if (readOnlyDestInstance != null && !patcher.IsSimpleType(readOnlyDestInstance.GetType())) {
                            patcher.CopyPropertiesRecursively(NewValue, readOnlyDestInstance, processedObjects);
                            // Note: if readOnlyDestInstance is struct, changes are lost
                        } else { /* log warning */ }
                    }
                }
            }
            public override object? GetValue() => NewValue;
        }

        public class ListDiff : PropertyDiff
        {
            public IReadOnlyList<object?> Items { get; }
            public ListDiff(string propertyName, IEnumerable sourceList) : base(propertyName) { Items = sourceList.Cast<object?>().ToList(); }
            public override void Apply(MajorRecord destinationRecord, PropertyInfo destPropInfo, RecordDeltaPatcher patcher) { 
                patcher.CopyListProperty(Items, destPropInfo, destinationRecord, new HashSet<object>(ReferenceEqualityComparer.Instance)); 
            }
            public override object? GetValue() => Items;
        }
        #endregion

        #region Public Interface
        public IReadOnlyList<PropertyDiff> GetPropertyDiffs(dynamic source, dynamic target) { /* ... as before ... */ return new List<PropertyDiff>(); }
        private bool ValuesAreEqual(object? val1, object? val2, string propertyNameForDebug = "") { /* ... as before ... */ return EqualityComparer<object>.Default.Equals(val1, val2); }
        public void ApplyPropertyDiffs(dynamic destination, IEnumerable<PropertyDiff> diffs) { /* ... (logic with refined GetPropertyInfo to pass to Apply) as before ... */ }
        private string FormatValue(object? val) { /* ... as before ... */ return val?.ToString() ?? "null"; }
        #endregion

        #region Core Recursive Copy Logic
        private bool IsSimpleType(Type type) { /* ... as before ... */ return type.IsPrimitive || type.IsEnum || type == typeof(string) || (type.IsValueType && !type.IsGenericType && type.Namespace!=null && type.Namespace.StartsWith("System")); }

        // Added processedObjects for cycle detection
        private void CopyPropertiesRecursively(object source, object destination, HashSet<object> processedObjects)
        {
            if (source == null || destination == null) return;

            // CYCLE DETECTION: Check if source object has already been processed in this call chain
            if (processedObjects.Contains(source)) {
                AppendLog($"DEBUG_CPR: Cycle detected or source object already processed: {source.GetType().Name} (Source Hash: {source.GetHashCode()}). Halting recursion for this path.");
                return;
            }
            processedObjects.Add(source); // Mark this source object as being processed

            Type sourceType = source.GetType(); Type destType = destination.GetType(); 
            AppendLog($"DEBUG_CPR_ENTRY: SourceType='{sourceType.Name}', DestType='{destType.Name}' (Processed Set Size: {processedObjects.Count})");
            var sourceReadableProps = this.GetGetterProperties(sourceType);
            var destinationProperties = this.GetGetterProperties(destType); 
            string sourcePropNames = string.Join(", ", sourceReadableProps.Keys); AppendLog($"    CPR_SourcePropNames: [{sourcePropNames}]");
            string destPropNames = string.Join(", ", destinationProperties.Keys); AppendLog($"    CPR_DestPropNames: [{destPropNames}]");
            // bool processedActorEffect = false; bool processedAttacks = false; // These flags can be removed if logs are sufficient

            foreach (var srcPropKvp in sourceReadableProps) {
                string propName = srcPropKvp.Key; PropertyInfo srcProp = srcPropKvp.Value;
                // if (string.Equals(propName, "ActorEffect", StringComparison.OrdinalIgnoreCase)) processedActorEffect = true; // Can remove
                // if (string.Equals(propName, "Attacks", StringComparison.OrdinalIgnoreCase)) processedAttacks = true; // Can remove

                if (!destinationProperties.TryGetValue(propName, out var destProp)) { /* ... log skip ... */ continue; }
                object? sourceValue = srcProp.GetValue(source); Type destPropType = destProp.PropertyType; 
                if (propName == "Name" || propName == "SkeletalModel" || /* ... other debugged props ... */ propName == "ActorEffect" || propName == "Attacks") {
                    AppendLog($"DEBUG_CPR: --- Property '{propName}' on SourceObj '{source.GetType().Name}' (Current DestObj '{destination.GetType().Name}') ---");
                    // ... (Full DEBUG_CPR logging block as in previous response) ...
                    AppendLog($"       CPR_destProp.CanWrite: {destProp.CanWrite}"); 
                }

                if (sourceValue == null) { /* ... set to null or clear ... */ continue; }
                Type sourceValueType = sourceValue.GetType();

                if (destProp.CanWrite) { 
                    if (destPropType.IsAssignableFrom(sourceValueType)) { /* Direct Assign */ destProp.SetValue(destination, sourceValue); }
                    else if (destPropType.IsGenericType && destPropType.GetGenericTypeDefinition() == typeof(Noggog.ExtendedList<>) && sourceValue is IEnumerable elSourceEnumW && !(sourceValue is string)) {
                        this.CopyListProperty(elSourceEnumW, destProp, destination, processedObjects);  // Pass processedObjects
                    } else if (typeof(IDictionary).IsAssignableFrom(destPropType) && sourceValue is IEnumerable dictSourceEnumW && !(sourceValue is string)) {
                        this.CopyDictionaryProperty(dictSourceEnumW, destProp, destination, processedObjects); // Pass processedObjects
                    } else if (typeof(IList).IsAssignableFrom(destPropType) && !(destPropType.IsGenericType && destPropType.GetGenericTypeDefinition() == typeof(Noggog.ExtendedList<>)) && sourceValue is IEnumerable listSourceEnumW && !(sourceValue is string)) {
                        this.CopyListProperty(listSourceEnumW, destProp, destination, processedObjects); // Pass processedObjects
                    } else { // Writable Complex Object/Struct
                        object? convertedInstance = this.TryCreateInstanceFromGetter(destPropType, sourceValue);
                        if (convertedInstance != null) {
                            destProp.SetValue(destination, convertedInstance);
                            if (!this.IsSimpleType(destPropType) && !this.IsSimpleType(sourceValueType) && destPropType != sourceValueType) {
                                object? targetForRec = destProp.GetValue(destination);
                                if (targetForRec != null) { this.CopyPropertiesRecursively(sourceValue, targetForRec, processedObjects); if (destPropType.IsValueType) destProp.SetValue(destination, targetForRec); } // Pass processedObjects
                            }
                        } else { /* ... create, populate (with processedObjects), set ... */
                            object? nestedDest = destProp.GetValue(destination); bool newCreated = false;
                            if (nestedDest == null && !destPropType.IsValueType) { /* Create class */ try { nestedDest = Activator.CreateInstance(destPropType); newCreated = true; } catch {continue;} }
                            else if (destPropType.IsValueType) { /* Create struct */ nestedDest = Activator.CreateInstance(destPropType); }
                            if (nestedDest == null) continue;
                            this.CopyPropertiesRecursively(sourceValue, nestedDest, processedObjects); // Pass processedObjects
                            if (newCreated || destPropType.IsValueType) destProp.SetValue(destination, nestedDest);
                        }
                    }
                } else { // Read-Only Property
                    if (typeof(IDictionary).IsAssignableFrom(destPropType) && sourceValue is IEnumerable dictSourceEnumRO && !(sourceValue is string)) {
                        this.CopyDictionaryProperty(dictSourceEnumRO, destProp, destination, processedObjects); // Pass processedObjects
                    } else if (typeof(IList).IsAssignableFrom(destPropType) && sourceValue is IEnumerable listSourceEnumRO && !(sourceValue is string)) {
                        this.CopyListProperty(listSourceEnumRO, destProp, destination, processedObjects); // Pass processedObjects
                    } else { /* ... read-only complex object (pass processedObjects if recursing) ... */
                         object? readOnlyInstance = destProp.GetValue(destination);
                         if (readOnlyInstance != null && !this.IsSimpleType(readOnlyInstance.GetType())) {
                            this.CopyPropertiesRecursively(sourceValue, readOnlyInstance, processedObjects); // Pass processedObjects
                         }
                    }
                }
                if (propName == "Name" || propName == "SkeletalModel" || /* ... */ propName == "ActorEffect" || propName == "Attacks") { AppendLog($"DEBUG_CPR: --- Finished Property '{propName}' ---"); }
            } 
            // Remove source from processedObjects *if* you want to allow it to be processed again via a *different path* in the same top-level copy.
            // For strict cycle breaking within a single traversal path, leaving it is fine.
            // processedObjects.Remove(source); // Optional: if source can be part of multiple distinct sub-graphs from the root.
            AppendLog($"DEBUG_CPR_EXIT: SourceType='{sourceType.Name}', DestType='{destType.Name}' (Processed Set Size: {processedObjects.Count})");
        }
        
        // Modified to accept and pass processedObjects
                // Modified to accept and pass processedObjects
        private void CopyListProperty(IEnumerable sourceEnumerable, PropertyInfo destPropertyInfo, object parentDestinationObject, HashSet<object> processedObjects) 
        {
            object? destListObj = destPropertyInfo.GetValue(parentDestinationObject); 
            Type destListPropertyType = destPropertyInfo.PropertyType;

            // Ensure destination list instance exists or create it if the property is writable
            if (destListObj == null) {
                if (destPropertyInfo.CanWrite) {
                    if (destListPropertyType.IsInterface || destListPropertyType.IsAbstract) { 
                        if (destListPropertyType.IsGenericType && destListPropertyType.GetGenericTypeDefinition() == typeof(Noggog.ExtendedList<>)) {
                            destListObj = Activator.CreateInstance(destListPropertyType);
                        } else if (destListPropertyType.IsGenericType && destListPropertyType.GetGenericArguments().Length > 0 && 
                                   (destListPropertyType.GetGenericTypeDefinition() == typeof(IList<>) || destListPropertyType.GetGenericTypeDefinition() == typeof(ICollection<>) || destListPropertyType.GetGenericTypeDefinition() == typeof(IEnumerable<>))) {
                            Type itemType = destListPropertyType.GetGenericArguments()[0]; 
                            Type concreteListType = typeof(List<>).MakeGenericType(itemType); 
                            destListObj = Activator.CreateInstance(concreteListType);
                        } else { 
                            AppendLog($"Warning (CopyListProperty): Cannot create instance for null list property '{destPropertyInfo.Name}' (interface/abstract type not IList<>/ExtendedList<>). Skipping."); 
                            return; 
                        }
                    } else { 
                       try { destListObj = Activator.CreateInstance(destListPropertyType); }
                       catch (Exception ex) { AppendLog($"Error (CopyListProperty): Failed to create list instance for '{destPropertyInfo.Name}', type '{destListPropertyType.FullName}'. {ex.Message}", true); return; }
                    }
                    destPropertyInfo.SetValue(parentDestinationObject, destListObj);
                } else { // Property is read-only and null
                    AppendLog($"Warning (CopyListProperty): List property '{destPropertyInfo.Name}' is null and read-only. Cannot create or populate.", true); 
                    return;
                }
            }

            if (destListObj is not IList destList) { 
                AppendLog($"Warning (CopyListProperty): Property '{destPropertyInfo.Name}' on '{parentDestinationObject.GetType().Name}' is not IList. Type: {destListObj?.GetType().FullName}. Skipping."); 
                return; 
            }

            Type listActualType = destList.GetType(); // Use the actual type of the instance
            if (!listActualType.IsGenericType || listActualType.GetGenericArguments().Length == 0) { 
                 AppendLog($"Warning (CopyListProperty): Destination list for property '{destPropertyInfo.Name}' (ActualType: {listActualType.FullName}) is not a generic list or has no generic arguments. Skipping."); 
                return; 
            }
            var destItemType = listActualType.GetGenericArguments()[0]; 
            destList.Clear();

            foreach (var listItemSource in sourceEnumerable) {
                if (listItemSource == null) { 
                    if (!destItemType.IsValueType || Nullable.GetUnderlyingType(destItemType) != null) destList.Add(null); 
                    continue; 
                }
                Type listItemSourceType = listItemSource.GetType();
                if (destItemType.IsAssignableFrom(listItemSourceType)) {
                    destList.Add(listItemSource);
                } else {
                    object? concreteListItem = this.TryCreateInstanceFromGetter(destItemType, listItemSource);
                    if (concreteListItem != null) { 
                        if (!this.IsSimpleType(destItemType)) { // If not simple, recurse to ensure full copy
                            this.CopyPropertiesRecursively(listItemSource, concreteListItem, processedObjects); // Pass processedObjects
                        }
                        destList.Add(concreteListItem); 
                    } else { // Fallback: Create instance and recurse
                        if (destItemType.IsInterface || destItemType.IsAbstract) {
                            AppendLog($"Error (CopyListProperty): Cannot Activator.CreateInstance for list item of interface/abstract type {destItemType.Name} in list {destPropertyInfo.Name}. Skipping item.", true);
                            continue;
                        }
                        try { 
                            concreteListItem = Activator.CreateInstance(destItemType); 
                            this.CopyPropertiesRecursively(listItemSource, concreteListItem, processedObjects); // Pass processedObjects
                            destList.Add(concreteListItem); 
                        } catch (Exception ex) {
                            AppendLog($"Error (CopyListProperty): Failed to process list item for {destPropertyInfo.Name}. DestItemType: {destItemType.Name}, SourceItemType: {listItemSourceType.Name}. {ex.Message}", true);
                        }
                    }
                }
            }
        }

        // Modified to accept and pass processedObjects
                // Modified to accept and pass processedObjects
        private void CopyDictionaryProperty(IEnumerable sourceEnumerable, PropertyInfo destPropertyInfo, object parentDestinationObject, HashSet<object> processedObjects)
        {
            object? destDictObj = destPropertyInfo.GetValue(parentDestinationObject);
            Type destDictPropertyType = destPropertyInfo.PropertyType;

            // Ensure destination dictionary instance exists or create it if the property is writable
            if (destDictObj == null) {
                if (destPropertyInfo.CanWrite) {
                    if (destDictPropertyType.IsInterface || destDictPropertyType.IsAbstract) {
                        if (destDictPropertyType.IsGenericType && destDictPropertyType.GetGenericArguments().Length == 2 && 
                            (destDictPropertyType.GetGenericTypeDefinition() == typeof(IDictionary<,>) || destDictPropertyType.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>))) {
                            Type keyType = destDictPropertyType.GetGenericArguments()[0]; 
                            Type valueType = destDictPropertyType.GetGenericArguments()[1]; 
                            Type concreteDictType = typeof(Dictionary<,>).MakeGenericType(keyType, valueType); 
                            destDictObj = Activator.CreateInstance(concreteDictType);
                        } else { 
                            AppendLog($"Warning (CopyDictionaryProperty): Cannot create instance for null dictionary property '{destPropertyInfo.Name}' (interface/abstract type not IDictionary<,>). Skipping."); 
                            return; 
                        }
                    } else { 
                        try { destDictObj = Activator.CreateInstance(destDictPropertyType); }
                        catch (Exception ex) { AppendLog($"Error (CopyDictionaryProperty): Failed to create dictionary instance for '{destPropertyInfo.Name}', type '{destDictPropertyType.FullName}'. {ex.Message}", true); return; }
                    }
                    destPropertyInfo.SetValue(parentDestinationObject, destDictObj);
                } else { // Property is read-only and null
                     AppendLog($"Warning (CopyDictionaryProperty): Dictionary property '{destPropertyInfo.Name}' is null and read-only. Cannot create or populate.", true); 
                     return;
                }
            }

            if (destDictObj is not IDictionary destDict) {
                 AppendLog($"Warning (CopyDictionaryProperty): Property '{destPropertyInfo.Name}' on '{parentDestinationObject.GetType().Name}' is not IDictionary. Type: {destDictObj?.GetType().FullName}. Skipping.");
                return;
            }

            Type dictActualType = destDict.GetType(); // Use the actual type of the instance
            if (!dictActualType.IsGenericType || dictActualType.GetGenericArguments().Length != 2) {
                AppendLog($"Warning (CopyDictionaryProperty): Destination dictionary '{destPropertyInfo.Name}' (ActualType: {dictActualType.FullName}) is not a generic dictionary with 2 arguments. Skipping.");
                return;
            }
            Type keyDestType = dictActualType.GetGenericArguments()[0]; 
            Type valueDestType = dictActualType.GetGenericArguments()[1]; 
            destDict.Clear();

            foreach (object kvpObject in sourceEnumerable) {
                PropertyInfo? keyProp = kvpObject.GetType().GetProperty("Key"); 
                PropertyInfo? valueProp = kvpObject.GetType().GetProperty("Value");
                if (keyProp == null || valueProp == null) {
                    AppendLog($"Warning (CopyDictionaryProperty): Item in sourceEnumerable for '{destPropertyInfo.Name}' is not a KeyValuePair or is missing Key/Value properties. Skipping item.");
                    continue;
                }

                object? sourceKey = keyProp.GetValue(kvpObject); 
                object? sourceVal = valueProp.GetValue(kvpObject);
                if (sourceKey == null) { // Dictionary keys cannot be null
                    AppendLog($"Warning (CopyDictionaryProperty): Source key is null for dictionary '{destPropertyInfo.Name}'. Skipping entry.");
                    continue; 
                }

                object? destKey; 
                if (keyDestType.IsAssignableFrom(sourceKey.GetType())) {
                    destKey = sourceKey;
                } else {
                    destKey = this.TryCreateInstanceFromGetter(keyDestType, sourceKey);
                    if (destKey == null && !this.IsSimpleType(keyDestType)) { // If complex key type and no constructor, try creating and copying
                         try { 
                            destKey = Activator.CreateInstance(keyDestType);
                            // For keys, deep copy is usually not needed unless keys are complex mutable objects.
                            // If keys are complex, ensure their Equals/GetHashCode are correctly implemented.
                            // For simplicity here, we are not recursively copying keys further after creation.
                            // If key needs properties copied: this.CopyPropertiesRecursively(sourceKey, destKey, processedObjects);
                         } catch (Exception ex) { 
                            AppendLog($"Error (CopyDictionaryProperty) creating key instance for '{sourceKey.GetType().Name}' to '{keyDestType.Name}': {ex.Message}", true); 
                            destKey = null; 
                         }
                    }
                    if (destKey == null) {
                        AppendLog($"Warning (CopyDictionaryProperty): Could not convert/create key '{sourceKey}' for dictionary '{destPropertyInfo.Name}'. Skipping entry.");
                        continue;
                    }
                }

                object? destValue; 
                if (sourceVal == null) {
                    destValue = null;
                } else if (valueDestType.IsAssignableFrom(sourceVal.GetType())) {
                    destValue = sourceVal;
                } else {
                    destValue = this.TryCreateInstanceFromGetter(valueDestType, sourceVal);
                    if (destValue != null) { 
                        // If successfully created via constructor, recurse if not simple to ensure full copy
                        if (!this.IsSimpleType(valueDestType)) {
                            this.CopyPropertiesRecursively(sourceVal, destValue, processedObjects); // Pass processedObjects
                        }
                    } else { // Fallback: Create instance and recurse
                         if (valueDestType.IsInterface || valueDestType.IsAbstract) {
                            AppendLog($"Error (CopyDictionaryProperty): Cannot Activator.CreateInstance for dictionary value of interface/abstract type {valueDestType.Name} in dictionary {destPropertyInfo.Name}. Skipping entry.", true);
                            continue;
                        }
                        try {
                            destValue = Activator.CreateInstance(valueDestType);
                            this.CopyPropertiesRecursively(sourceVal, destValue, processedObjects); // Pass processedObjects
                        } catch (Exception ex) {
                             AppendLog($"Error (CopyDictionaryProperty): Failed to process dictionary value for {destPropertyInfo.Name}. DestValueType: {valueDestType.Name}, SourceValueType: {sourceVal.GetType().Name}. {ex.Message}", true);
                             continue;
                        }
                    }
                }
                try {
                    destDict[destKey] = destValue;
                } catch (Exception ex) {
                     AppendLog($"Error (CopyDictionaryProperty): Failed to add item to dictionary '{destPropertyInfo.Name}'. Key: '{destKey}', Value: '{destValue}'. {ex.Message}", true);
                }
            }
        }
        #endregion

        #region Reflection Caching & Helpers
        private object? TryCreateInstanceFromGetter(Type targetType, object sourceValueAsGetter) { /* ... as before ... */  if (sourceValueAsGetter == null) return null; Type sourceGetterType = sourceValueAsGetter.GetType(); if (targetType.IsAssignableFrom(sourceGetterType)) return sourceValueAsGetter; ConstructorInfo? ctor = targetType.GetConstructor(new[] { sourceGetterType }); if (ctor != null) return ctor.Invoke(new[] { sourceValueAsGetter }); foreach (var iface in sourceGetterType.GetInterfaces()) { ctor = targetType.GetConstructor(new[] { iface }); if (ctor != null) return ctor.Invoke(new[] { sourceValueAsGetter }); } Type? currentBaseType = sourceGetterType.BaseType; while (currentBaseType != null && currentBaseType != typeof(object)) { ctor = targetType.GetConstructor(new[] { currentBaseType }); if (ctor != null) return ctor.Invoke(new[] { sourceValueAsGetter }); currentBaseType = currentBaseType.BaseType; } return null; }
        private IReadOnlyDictionary<string, PropertyInfo> GetGetterProperties(Type type) { if (_getterCache.TryGetValue(type, out var cachedProperties)) return cachedProperties; var properties = new Dictionary<string, PropertyInfo>(); foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance)) properties[prop.Name] = prop;  var interfaces = type.GetInterfaces(); foreach (var iface in interfaces) { foreach (var prop in iface.GetProperties(BindingFlags.Public | BindingFlags.Instance)) { if (!properties.ContainsKey(prop.Name)) properties[prop.Name] = prop; } } _getterCache[type] = properties; return properties; }
        private IReadOnlyDictionary<string, PropertyInfo> GetSetterProperties(Type recordType) { if (_setterCache.TryGetValue(recordType, out var cachedProperties)) return cachedProperties; var properties = recordType.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite).GroupBy(p => p.Name).Select(g => g.OrderByDescending(pinfo => pinfo.DeclaringType == recordType ? 2 : (pinfo.DeclaringType?.IsAssignableFrom(recordType) ?? false) ? 1: 0).First()).ToDictionary(p => p.Name); _setterCache[recordType] = properties; return properties; }
        #endregion
    }
}