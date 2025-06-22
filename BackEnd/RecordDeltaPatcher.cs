// RecordDeltaPatcher.cs
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins.Records;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Mutagen.Bethesda.Plugins; // For FormKey, IFormLink etc.
// using NPC_Plugin_Chooser_2.Models; // Assuming OptionalUIModule is here

namespace NPC_Plugin_Chooser_2.BackEnd
{
    // public abstract class OptionalUIModule // Ensure this is accessible
    // {
    //     protected void AppendLog(string message, bool isError = false, bool forceLog = false) => Console.WriteLine($"LOG: {(isError ? "ERROR: " : "")}{message}");
    // }

    public class RecordDeltaPatcher : OptionalUIModule
    {
        #region Fields and Initialization
        private readonly Dictionary<Type, IReadOnlyDictionary<string, PropertyInfo>> _getterCache = new();
        private readonly Dictionary<Type, IReadOnlyDictionary<string, PropertyInfo>> _setterCache = new();
        private readonly Dictionary<FormKey, Dictionary<string, object?>> _patchedValues = new();

        public void Reinitialize()
        {
            _patchedValues.Clear();
            // _getterCache.Clear(); // Optionally clear if types might change or for full reset
            // _setterCache.Clear();
        }
        #endregion

        #region Property Diff Classes
        public abstract class PropertyDiff
        {
            public string PropertyName { get; }
            protected PropertyDiff(string propertyName) => PropertyName = propertyName;
            public abstract void Apply(MajorRecord destinationRecord, PropertyInfo setter, RecordDeltaPatcher patcher);
            public abstract object? GetValue();
        }

        public class ValueDiff : PropertyDiff
        {
            public object? NewValue { get; }
            public ValueDiff(string propertyName, object? newValue) : base(propertyName) => NewValue = newValue;

            public override void Apply(MajorRecord destinationRecord, PropertyInfo setter, RecordDeltaPatcher patcher)
            {
                if (NewValue == null)
                {
                    setter.SetValue(destinationRecord, null);
                    return;
                }

                Type newValueType = NewValue.GetType();
                Type setterType = setter.PropertyType;

                if (setterType.IsAssignableFrom(newValueType))
                {
                    setter.SetValue(destinationRecord, NewValue);
                }
                else
                {
                    object? convertedInstance = patcher.TryCreateInstanceFromGetter(setterType, NewValue);
                    if (convertedInstance != null)
                    {
                        setter.SetValue(destinationRecord, convertedInstance);
                        // Optional: If TryCreateInstanceFromGetter might not be exhaustive for complex types
                        // if (!patcher.IsSimpleType(setterType)) {
                        //    patcher.CopyPropertiesRecursively(NewValue, convertedInstance);
                        //    if (setterType.IsValueType) setter.SetValue(destinationRecord, convertedInstance);
                        // }
                    }
                    else
                    {
                        object? destinationPropertyValue = setter.GetValue(destinationRecord);
                        bool newInstanceCreated = false;

                        if (destinationPropertyValue == null && !setterType.IsValueType) // Class type that is null
                        {
                            if (setterType.IsInterface || setterType.IsAbstract)
                            {
                                patcher.AppendLog($"Error (ValueDiff.Apply): Cannot create instance of interface/abstract type '{setterType.FullName}' for property '{PropertyName}'. Recursive copy aborted.", true);
                                return;
                            }
                            try
                            {
                                destinationPropertyValue = Activator.CreateInstance(setterType);
                                newInstanceCreated = true;
                            }
                            catch (Exception ex)
                            {
                                patcher.AppendLog($"Error (ValueDiff.Apply): Failed to Activator.CreateInstance for '{setterType.FullName}' (property: {PropertyName}). {ex.Message}", true);
                                return;
                            }
                        }
                        else if (setterType.IsValueType) // Struct type (GetValue returns a copy or default)
                        {
                            // If it's a nullable struct that's null, create instance of underlying type
                            var underlyingType = Nullable.GetUnderlyingType(setterType);
                            if (destinationPropertyValue == null && underlyingType != null) {
                                destinationPropertyValue = Activator.CreateInstance(underlyingType);
                            } else { // Regular struct or non-null nullable struct, get/create instance
                                destinationPropertyValue = Activator.CreateInstance(setterType);
                            }
                            // No newInstanceCreated flag needed here as structs are always set back
                        }
                        
                        if (destinationPropertyValue == null) {
                             patcher.AppendLog($"Error (ValueDiff.Apply): Could not obtain or create a destination object for property '{PropertyName}' of type '{setterType.FullName}'. Recursive copy aborted.", true);
                             return;
                        }

                        patcher.CopyPropertiesRecursively(NewValue, destinationPropertyValue);

                        if (newInstanceCreated || setterType.IsValueType) // Always set back structs, or if new class instance was made
                        {
                            setter.SetValue(destinationRecord, destinationPropertyValue);
                        }
                    }
                }
            }
            public override object? GetValue() => NewValue;
        }

        public class ListDiff : PropertyDiff
        {
            public IReadOnlyList<object?> Items { get; }
            public ListDiff(string propertyName, IEnumerable sourceList) : base(propertyName)
            {
                Items = sourceList.Cast<object?>().ToList();
            }

            public override void Apply(MajorRecord destinationRecord, PropertyInfo setter, RecordDeltaPatcher patcher)
            {
                patcher.CopyListProperty(Items, setter, destinationRecord);
            }
            public override object? GetValue() => Items;
        }
        #endregion

        #region Public Interface
                public IReadOnlyList<PropertyDiff> GetPropertyDiffs(dynamic source, dynamic target)
        {
            if (source is null) throw new ArgumentNullException(nameof(source));
            if (target is null) throw new ArgumentNullException(nameof(target));

            var differences = new List<PropertyDiff>();
            Type sourceType = source.GetType();
            Type targetType = target.GetType();

            var getterProps = GetGetterProperties(sourceType); // Properties from the source object
            // Get properties from the target object for comparison.
            // We need these to get targetValue. It's okay if target doesn't have all source props.
            var targetReadableProps = GetGetterProperties(targetType); 

            foreach (var piKvp in getterProps) // piKvp is KeyValuePair<string, PropertyInfo>
            {
                PropertyInfo sourcePi = piKvp.Value; // PropertyInfo from the source
                string propertyName = sourcePi.Name;

                object? sourceValue = sourcePi.GetValue(source);
                object? targetValue = null;

                if (targetReadableProps.TryGetValue(propertyName, out var targetPi))
                {
                    targetValue = targetPi.GetValue(target);
                }
                // If property doesn't exist on target, targetValue remains null.
                // The ValuesAreEqual check will handle this (sourceValue vs null).

                // --- Start Debugging Logic for GetPropertyDiffs ---
                if (propertyName == "Name" || propertyName == "SkeletalModel" || propertyName == "Description" || propertyName == "Voices" || propertyName == "HeadData")
                {
                    AppendLog($"DEBUG (GetPropertyDiffs): --- Property: '{propertyName}' ---");
                    AppendLog($"    Source Type (source object): {sourceType.FullName}");
                    AppendLog($"    Source Property Declared Type (sourcePi.PropertyType): {sourcePi.PropertyType.FullName}");
                    AppendLog($"    Source Value Actual Type: {(sourceValue == null ? "NULL" : sourceValue.GetType().FullName)}");
                    
                    if (targetPi != null) {
                        AppendLog($"    Target Property Declared Type (targetPi.PropertyType): {targetPi.PropertyType.FullName}");
                        AppendLog($"    Target Value Actual Type: {(targetValue == null ? "NULL" : targetValue.GetType().FullName)}");
                    } else {
                        AppendLog($"    Target Property '{propertyName}' not found on Target Type: {targetType.FullName}");
                    }

                    bool isSourceValueIList = sourceValue is IList && !(sourceValue is string);
                    AppendLog($"    Is sourceValue IList (and not string)?: {isSourceValueIList}");

                    bool isSourceNoggogExtendedList = sourceValue?.GetType().IsGenericType == true &&
                                                      sourceValue.GetType().GetGenericTypeDefinition() == typeof(Noggog.ExtendedList<>);
                    AppendLog($"    Is sourceValue Noggog.ExtendedList<>?: {isSourceNoggogExtendedList}");
                    
                    bool isSourceJustIEnumerable = sourceValue is IEnumerable && !(sourceValue is string);
                    AppendLog($"    Is sourceValue just IEnumerable (and not string)?: {isSourceJustIEnumerable}");
                }
                // --- End Debugging Logic ---

                // Determine if the source value represents a "list" for diffing purposes
                // (i.e., should create a ListDiff)
                bool treatAsListDiff = false;
                if (sourceValue != null && !(sourceValue is string)) // Strings are IEnumerable<char> but not lists for our purpose
                {
                    if (sourceValue is IList) // Covers arrays, List<T>, ExtendedList<T> (as it implements IList)
                    {
                        treatAsListDiff = true;
                    }
                    // Could add more specific checks if IList is too broad or narrow, e.g.,
                    // else if (sourceValue.GetType().IsGenericType && 
                    //            sourceValue.GetType().GetGenericTypeDefinition() == typeof(Noggog.ExtendedList<>))
                    // {
                    //     treatAsListDiff = true;
                    // }
                }

                if (treatAsListDiff)
                {
                    var sourceListForDiff = (IEnumerable)sourceValue!; // Safe cast due to checks
                    var targetList = targetValue as IEnumerable;
                    bool areEqual = false;

                    if (targetList != null)
                    {
                        var sourceObjList = sourceListForDiff.Cast<object?>().ToList();
                        var targetObjList = targetList.Cast<object?>().ToList();
                        if (sourceObjList.Count == targetObjList.Count)
                        {
                            // This SequenceEqual is shallow for complex objects.
                            // A proper diff would compare item by item recursively.
                            // For now, this determines if a ListDiff needs to be created at all.
                            areEqual = sourceObjList.SequenceEqual(targetObjList);
                        }
                    }

                    if (!areEqual)
                    {
                        if (propertyName == "Name" || propertyName == "SkeletalModel" || propertyName == "Description") // Example properties for debug log
                        {
                             AppendLog($"    >>> (GetPropertyDiffs) Creating ListDiff for '{propertyName}'.");
                        }
                        differences.Add(new ListDiff(propertyName, sourceListForDiff));
                    }
                    else
                    {
                        if (propertyName == "Name" || propertyName == "SkeletalModel" || propertyName == "Description")
                        {
                             AppendLog($"    >>> (GetPropertyDiffs) Lists for '{propertyName}' are considered equal. No ListDiff created.");
                        }
                    }
                }
                else // Treat as a single value (could be simple, struct, or complex object like TranslatedString)
                {
                    if (!ValuesAreEqual(sourceValue, targetValue, propertyName))
                    {
                        if (propertyName == "Name" || propertyName == "SkeletalModel" || propertyName == "Description") // Example properties for debug log
                        {
                             AppendLog($"    >>> (GetPropertyDiffs) Creating ValueDiff for '{propertyName}'.");
                        }
                        differences.Add(new ValueDiff(propertyName, sourceValue));
                    }
                    else
                    {
                         if (propertyName == "Name" || propertyName == "SkeletalModel" || propertyName == "Description")
                        {
                             AppendLog($"    >>> (GetPropertyDiffs) Values for '{propertyName}' are considered equal. No ValueDiff created.");
                        }
                    }
                }
                if (propertyName == "Name" || propertyName == "SkeletalModel" || propertyName == "Description" || propertyName == "Voices" || propertyName == "HeadData")
                {
                     AppendLog($"DEBUG (GetPropertyDiffs): --- Finished Property: '{propertyName}' ---");
                }
            }
            return differences;
        }

        // Helper for robust value comparison, especially for complex objects / structs
        // This is a simplified version for diff generation. Deep equality for complex objects
        // would require its own recursive comparison if `Equals` is not sufficient.
        private bool ValuesAreEqual(object? val1, object? val2, string propertyNameForDebug = "")
        {
            if (ReferenceEquals(val1, val2)) return true; // Same instance or both null
            if (val1 == null || val2 == null) return false; // One is null, the other isn't

            // Basic .Equals() check. This relies on the type overriding Equals for value-based comparison.
            // For many Mutagen types (structs, FormLink, TranslatedString), Equals IS overridden meaningfully.
            if (val1.Equals(val2)) return true;

            // Optional: More sophisticated checks if val1.Equals(val2) is false but they might be semantically equal
            // e.g., comparing an IGetter to a concrete instance.
            // For now, if Equals returns false, we consider them different for diffing.
            // Type type1 = val1.GetType();
            // Type type2 = val2.GetType();
            // if (type1 != type2) {
            //     // Example: if one is ITranslatedStringGetter and other is TranslatedString
            //     // You might try to convert one to the other's type and then compare
            //     // object? convertedVal1 = TryCreateInstanceFromGetter(type2, val1);
            //     // if (convertedVal1 != null && convertedVal1.Equals(val2)) return true;
            //     // object? convertedVal2 = TryCreateInstanceFromGetter(type1, val2);
            //     // if (convertedVal2 != null && convertedVal2.Equals(val1)) return true;
            // }

            // If we reach here, they are considered different.
            // Add debug logging if a specific property is problematic:
            // if (!string.IsNullOrEmpty(propertyNameForDebug) && (propertyNameForDebug == "Name" || propertyNameForDebug == "SkeletalModel")) {
            //     AppendLog($"DEBUG (ValuesAreEqual for '{propertyNameForDebug}'): val1.Equals(val2) was false. val1 Type: {val1.GetType()}, val2 Type: {val2.GetType()}");
            // }

            return false;
        }

        private bool IsDefaultValue(object obj)
        {
            if (obj == null) return true;
            Type type = obj.GetType();
            if (type.IsValueType)
            {
                return obj.Equals(Activator.CreateInstance(type));
            }
            return false;
        }

        public void ApplyPropertyDiffs(dynamic destination, IEnumerable<PropertyDiff> diffs)
        {
            if (destination is null) throw new ArgumentNullException(nameof(destination));
            if (diffs is null) throw new ArgumentNullException(nameof(diffs));

            if (destination is not MajorRecord concreteDestination)
            {
                AppendLog($"Error: Destination for ApplyPropertyDiffs must be a MajorRecord. Got {destination.GetType().FullName}", true);
                return;
            }

            IReadOnlyDictionary<string, PropertyInfo> setterProps = GetSetterProperties(concreteDestination.GetType());
            
            _patchedValues.TryGetValue(concreteDestination.FormKey, out var recordPatchedValues);
            if (recordPatchedValues == null && !concreteDestination.FormKey.IsNull)
            {
                recordPatchedValues = new Dictionary<string, object?>();
                _patchedValues[concreteDestination.FormKey] = recordPatchedValues;
            }

            foreach (var diff in diffs)
            {
                if (!setterProps.TryGetValue(diff.PropertyName, out var dstPi))
                {
                    AppendLog($"Warning: Setter for property '{diff.PropertyName}' not found on destination type '{concreteDestination.GetType().FullName}'. Skipping diff.");
                    continue;
                }

                object? newValueToStore = diff.GetValue();

                if (recordPatchedValues != null && recordPatchedValues.TryGetValue(diff.PropertyName, out object? oldValue))
                {
                    bool isConflict = false;
                    if (oldValue is IReadOnlyList<object?> oldList && newValueToStore is IReadOnlyList<object?> newList)
                    {
                        if (!oldList.SequenceEqual(newList)) isConflict = true;
                    }
                    else if (!Equals(oldValue, newValueToStore))
                    {
                        isConflict = true;
                    }

                    if (isConflict)
                    {
                        AppendLog($"CONFLICT: Property '{diff.PropertyName}' on record '{concreteDestination.EditorID ?? concreteDestination.FormKey.ToString()}' already patched with '{FormatValue(oldValue)}', overwriting with '{FormatValue(newValueToStore)}'.");
                    }
                }

                if (recordPatchedValues != null)
                {
                    recordPatchedValues[diff.PropertyName] = newValueToStore;
                }
                diff.Apply(concreteDestination, dstPi, this);
            }
        }

        private string FormatValue(object? val)
        {
            if (val == null) return "null";
            if (val is string s) return $"\"{s}\"";
            if (val is IEnumerable enumerable && val is not string) return $"[collection({enumerable.Cast<object>().Count()}) items]";
            return val.ToString() ?? "N/A";
        }
        #endregion

        #region Core Recursive Copy Logic
        // Instance method
        private bool IsSimpleType(Type type)
        {
            return type.IsPrimitive ||
                   type.IsEnum ||
                   type == typeof(string) ||
                   type == typeof(decimal) ||
                   type == typeof(DateTime) ||
                   type == typeof(Guid) ||
                   (type.IsValueType && Nullable.GetUnderlyingType(type) == null && type.Namespace != null && type.Namespace.StartsWith("System") && !type.IsGenericType) ||
                   (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IFormLinkGetter<>)) ||
                   (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IFormLink<>)) ||
                   (type.IsValueType && type.IsGenericType && type.GetGenericTypeDefinition() == typeof(FormLink<>));
        }

        // Instance method
        private void CopyPropertiesRecursively(object source, object destination)
        {
            if (source == null || destination == null) return;

            Type sourceType = source.GetType();
            Type destType = destination.GetType();

            var destProps = this.GetSetterProperties(destType);
            var sourceProps = this.GetGetterProperties(sourceType);

            // Logging for the parent objects being copied
            // AppendLog($"DEBUG: CopyPropertiesRecursively called. Source Type: {sourceType.FullName}, Destination Type: {destType.FullName}");

            foreach (var srcPropKvp in sourceProps)
            {
                PropertyInfo srcProp = srcPropKvp.Value;
                if (!destProps.TryGetValue(srcProp.Name, out var destProp)) continue;

                object? sourceValue = srcProp.GetValue(source);
                Type destPropType = destProp.PropertyType;

                // ===== START INTENSE DEBUGGING FOR A SPECIFIC PROPERTY =====
                if (srcProp.Name == "Name" || srcProp.Name == "SkeletalModel" || srcProp.Name == "HeadData" || srcProp.Name == "Description" || srcProp.Name == "Voices") // Add other failing properties
                {
                    AppendLog($"DEBUG: Property '{srcProp.Name}' on Source '{source.GetType().Name}'");
                    AppendLog($"       SourceValue Type: {(sourceValue == null ? "NULL" : sourceValue.GetType().FullName)}");
                    AppendLog($"       DestPropType: {destPropType.FullName}");

                    bool isSourceValueEnumerable = sourceValue is IEnumerable && !(sourceValue is string);
                    AppendLog($"       isSourceValueEnumerable (and not string): {isSourceValueEnumerable}");

                    bool isDestPropTypeDictionary = (typeof(IDictionary).IsAssignableFrom(destPropType) || (destPropType.IsInterface && destPropType.IsGenericType && destPropType.GetGenericTypeDefinition() == typeof(IDictionary<,>)));
                    AppendLog($"       isDestPropTypeDictionary: {isDestPropTypeDictionary}");
                    
                    bool isDestPropTypeList = (typeof(IList).IsAssignableFrom(destPropType) || (destPropType.IsInterface && destPropType.IsGenericType && destPropType.GetGenericTypeDefinition() == typeof(IList<>)));
                    AppendLog($"       isDestPropTypeList: {isDestPropTypeList}");

                    bool canAssignDirectly = destPropType.IsAssignableFrom(sourceValue?.GetType() ?? typeof(object)); // Handle null sourceValue for GetType()
                    AppendLog($"       canAssignDirectly: {canAssignDirectly}");
                }
                // ===== END INTENSE DEBUGGING =====


                if (sourceValue == null)
                {
                    destProp.SetValue(destination, null);
                    continue;
                }

                Type sourceValueType = sourceValue.GetType();

                // Current Dispatch Logic (keep your latest version here)
                if (destPropType.IsAssignableFrom(sourceValueType))
                {
                    if (srcProp.Name == "Name" || srcProp.Name == "SkeletalModel") AppendLog($"       >>> Route: Direct Assign");
                    destProp.SetValue(destination, sourceValue);
                }
                else if ((typeof(IDictionary).IsAssignableFrom(destPropType) || (destPropType.IsInterface && destPropType.IsGenericType && destPropType.GetGenericTypeDefinition() == typeof(IDictionary<,>))) &&
                         sourceValue is IEnumerable kvpSourceEnumerable && !(sourceValue is string))
                {
                    if (srcProp.Name == "Name" || srcProp.Name == "SkeletalModel") AppendLog($"       >>> Route: Dictionary");
                    this.CopyDictionaryProperty(kvpSourceEnumerable, destProp, destination);
                }
                else if ((typeof(IList).IsAssignableFrom(destPropType) || (destPropType.IsInterface && destPropType.IsGenericType && destPropType.GetGenericTypeDefinition() == typeof(IList<>))) &&
                         sourceValue is IEnumerable sourceEnumerable && !(sourceValue is string))
                {
                    if (srcProp.Name == "Name" || srcProp.Name == "SkeletalModel") AppendLog($"       >>> Route: List");
                    this.CopyListProperty(sourceEnumerable, destProp, destination);
                }
                else 
                {
                    if (srcProp.Name == "Name" || srcProp.Name == "SkeletalModel") AppendLog($"       >>> Route: Complex Object/Struct");
                    // ... (rest of complex object logic) ...
                    object? convertedNestedInstance = this.TryCreateInstanceFromGetter(destPropType, sourceValue);
                    if (convertedNestedInstance != null)
                    {
                        destProp.SetValue(destination, convertedNestedInstance);
                        if (!this.IsSimpleType(destPropType) && !this.IsSimpleType(sourceValueType) && destPropType != sourceValueType) { 
                           object? targetForRecursion = destProp.GetValue(destination);
                           if (targetForRecursion != null) {
                               this.CopyPropertiesRecursively(sourceValue, targetForRecursion);
                               if (destPropType.IsValueType) destProp.SetValue(destination, targetForRecursion); 
                           }
                        }
                    }
                    else 
                    {
                        object? nestedDestObject = destProp.GetValue(destination);
                        bool newNestedInstanceCreated = false;

                        if (nestedDestObject == null && !destPropType.IsValueType) 
                        {
                            if (destPropType.IsInterface || destPropType.IsAbstract)
                            {
                                AppendLog($"Error (RecursiveCopy): Cannot create instance of interface/abstract type '{destPropType.FullName}' for nested property '{destProp.Name}'. Skipping.", true);
                                continue;
                            }
                            try
                            {
                                nestedDestObject = Activator.CreateInstance(destPropType);
                                newNestedInstanceCreated = true;
                            }
                            catch (Exception ex)
                            {
                                AppendLog($"Error (RecursiveCopy): Failed to Activator.CreateInstance for nested '{destPropType.FullName}' (property: {destProp.Name}). {ex.Message}", true);
                                continue;
                            }
                        }
                        else if (destPropType.IsValueType) 
                        {
                            var underlyingType = Nullable.GetUnderlyingType(destPropType);
                            if (nestedDestObject == null && underlyingType != null) { 
                                nestedDestObject = Activator.CreateInstance(underlyingType);
                            } else if (nestedDestObject == null || (nestedDestObject.GetType() != destPropType && (underlyingType == null || nestedDestObject.GetType() != underlyingType)) ) { 
                                nestedDestObject = Activator.CreateInstance(destPropType);
                            }
                        }
                        
                        if (nestedDestObject == null) {
                            AppendLog($"Error (RecursiveCopy): Could not obtain or create nested destination object for property '{destProp.Name}' of type '{destPropType.FullName}'. Skipping.", true);
                            continue;
                        }

                        this.CopyPropertiesRecursively(sourceValue, nestedDestObject);

                        if (newNestedInstanceCreated || destPropType.IsValueType)
                        {
                            destProp.SetValue(destination, nestedDestObject);
                        }
                    }
                }
            }
        }
        
        private void CopyListProperty(IEnumerable sourceEnumerable, PropertyInfo destPropertyInfo, object parentDestinationObject)
        {
            object? destListObj = destPropertyInfo.GetValue(parentDestinationObject);
            Type destListPropertyType = destPropertyInfo.PropertyType;

            if (destListObj == null)
            {
                if (destListPropertyType.IsInterface || destListPropertyType.IsAbstract) { 
                    if (destListPropertyType.IsGenericType && destListPropertyType.GetGenericTypeDefinition() == typeof(Noggog.ExtendedList<>)) {
                        destListObj = Activator.CreateInstance(destListPropertyType);
                    } else if (destListPropertyType.IsGenericType && destListPropertyType.GetGenericArguments().Length > 0 && (destListPropertyType.GetGenericTypeDefinition() == typeof(IList<>) || destListPropertyType.GetGenericTypeDefinition() == typeof(ICollection<>) || destListPropertyType.GetGenericTypeDefinition() == typeof(IEnumerable<>))) {
                        Type itemType = destListPropertyType.GetGenericArguments()[0];
                        Type concreteListType = typeof(List<>).MakeGenericType(itemType);
                        destListObj = Activator.CreateInstance(concreteListType);
                    } else {
                        AppendLog($"Warning (CopyListProperty): Cannot create instance for null list property '{destPropertyInfo.Name}' of interface/abstract type {destListPropertyType.FullName}. Skipping.");
                        return;
                    }
                } else {
                   try { destListObj = Activator.CreateInstance(destListPropertyType); }
                   catch (Exception ex) { AppendLog($"Error (CopyListProperty): Failed to create list instance for '{destPropertyInfo.Name}', type '{destListPropertyType.FullName}'. {ex.Message}", true); return; }
                }
                destPropertyInfo.SetValue(parentDestinationObject, destListObj);
            }

            if (destListObj is not IList destList) {
                AppendLog($"Warning (CopyListProperty): Property '{destPropertyInfo.Name}' on '{parentDestinationObject.GetType().Name}' is not IList. Type: {destListObj?.GetType().FullName}. Skipping.");
                return;
            }

            Type listActualType = destList.GetType(); // Use actual type of the instance for GetGenericArguments
            if (!listActualType.IsGenericType || listActualType.GetGenericArguments().Length == 0) {
                 AppendLog($"Warning (CopyListProperty): Destination list for property '{destPropertyInfo.Name}' is not a generic list or has no generic arguments. Actual Type: {listActualType.FullName}. Skipping.");
                return;
            }
            var destItemType = listActualType.GetGenericArguments()[0];
            destList.Clear();

            foreach (var listItemSource in sourceEnumerable)
            {
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
                        if (!this.IsSimpleType(destItemType) ) { 
                            this.CopyPropertiesRecursively(listItemSource, concreteListItem);
                        }
                        destList.Add(concreteListItem);
                    } else {
                        if (destItemType.IsInterface || destItemType.IsAbstract) {
                            AppendLog($"Error (CopyListProperty): Cannot Activator.CreateInstance for list item of interface/abstract type {destItemType.Name} in list {destPropertyInfo.Name}. Skipping item.", true);
                            continue;
                        }
                        try {
                            concreteListItem = Activator.CreateInstance(destItemType);
                            this.CopyPropertiesRecursively(listItemSource, concreteListItem); 
                            destList.Add(concreteListItem); 
                        } catch (Exception ex) {
                            AppendLog($"Error (CopyListProperty): Failed to process list item for {destPropertyInfo.Name}. DestItemType: {destItemType.Name}, SourceItemType: {listItemSourceType.Name}. {ex.Message}", true);
                        }
                    }
                }
            }
        }

        private void CopyDictionaryProperty(IEnumerable sourceEnumerable, PropertyInfo destPropertyInfo, object parentDestinationObject)
        {
            object? destDictObj = destPropertyInfo.GetValue(parentDestinationObject);
            Type destDictPropertyType = destPropertyInfo.PropertyType;

            if (destDictObj == null) { 
                if (destDictPropertyType.IsInterface || destDictPropertyType.IsAbstract) {
                    if (destDictPropertyType.IsGenericType && destDictPropertyType.GetGenericArguments().Length == 2 && (destDictPropertyType.GetGenericTypeDefinition() == typeof(IDictionary<,>) || destDictPropertyType.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>))) {
                        Type keyType = destDictPropertyType.GetGenericArguments()[0];
                        Type valueType = destDictPropertyType.GetGenericArguments()[1];
                        Type concreteDictType = typeof(Dictionary<,>).MakeGenericType(keyType, valueType);
                        destDictObj = Activator.CreateInstance(concreteDictType);
                    } else {
                        AppendLog($"Warning (CopyDictionaryProperty): Cannot create instance for null dictionary property '{destPropertyInfo.Name}' of interface/abstract type {destDictPropertyType.FullName}. Skipping.");
                        return;
                    }
                 } else {
                    try { destDictObj = Activator.CreateInstance(destDictPropertyType); }
                    catch (Exception ex) { AppendLog($"Error (CopyDictionaryProperty): Failed to create dictionary instance for '{destPropertyInfo.Name}', type '{destDictPropertyType.FullName}'. {ex.Message}", true); return; }
                 }
                destPropertyInfo.SetValue(parentDestinationObject, destDictObj);
            }

            if (destDictObj is not IDictionary destDict) {
                 AppendLog($"Warning (CopyDictionaryProperty): Property '{destPropertyInfo.Name}' on '{parentDestinationObject.GetType().Name}' is not IDictionary. Type: {destDictObj?.GetType().FullName}. Skipping.");
                return;
            }

            Type dictActualType = destDict.GetType();
            if (!dictActualType.IsGenericType || dictActualType.GetGenericArguments().Length != 2) {
                AppendLog($"Warning (CopyDictionaryProperty): Destination dictionary for property '{destPropertyInfo.Name}' is not a generic dictionary with 2 arguments. Actual Type: {dictActualType.FullName}. Skipping.");
                return;
            }
            Type keyDestType = dictActualType.GetGenericArguments()[0];
            Type valueDestType = dictActualType.GetGenericArguments()[1];
            destDict.Clear();

            foreach (object kvpObject in sourceEnumerable) {
                PropertyInfo? keyProp = kvpObject.GetType().GetProperty("Key");
                PropertyInfo? valueProp = kvpObject.GetType().GetProperty("Value");
                if (keyProp == null || valueProp == null) continue;

                object? sourceKey = keyProp.GetValue(kvpObject);
                object? sourceVal = valueProp.GetValue(kvpObject);
                if (sourceKey == null) continue; 

                object? destKey; 
                if (keyDestType.IsAssignableFrom(sourceKey.GetType())) {
                    destKey = sourceKey;
                } else {
                    destKey = this.TryCreateInstanceFromGetter(keyDestType, sourceKey);
                    if (destKey == null && !this.IsSimpleType(keyDestType)) { // If complex key type and no constructor, try creating and copying
                         try { 
                            destKey = Activator.CreateInstance(keyDestType);
                            this.CopyPropertiesRecursively(sourceKey, destKey);
                         } catch { /* log error */ destKey = null; }
                    }
                    if (destKey == null) {
                        AppendLog($"Warning (CopyDictionaryProperty): Could not convert key '{sourceKey}' for dictionary '{destPropertyInfo.Name}'. Skipping entry.");
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
                        if (!this.IsSimpleType(valueDestType)) {
                            this.CopyPropertiesRecursively(sourceVal, destValue);
                        }
                    } else {
                         if (valueDestType.IsInterface || valueDestType.IsAbstract) {
                            AppendLog($"Error (CopyDictionaryProperty): Cannot Activator.CreateInstance for dictionary value of interface/abstract type {valueDestType.Name} in dictionary {destPropertyInfo.Name}. Skipping entry.", true);
                            continue;
                        }
                        try {
                            destValue = Activator.CreateInstance(valueDestType);
                            this.CopyPropertiesRecursively(sourceVal, destValue);
                        } catch (Exception ex) {
                             AppendLog($"Error (CopyDictionaryProperty): Failed to process dictionary value for {destPropertyInfo.Name}. DestValueType: {valueDestType.Name}, SourceValueType: {sourceVal.GetType().Name}. {ex.Message}", true);
                             continue;
                        }
                    }
                }
                destDict[destKey] = destValue;
            }
        }

        #endregion

        #region Reflection Caching & Helpers
        private object? TryCreateInstanceFromGetter(Type targetType, object sourceValueAsGetter)
        {
            if (sourceValueAsGetter == null) return null;
            Type sourceGetterType = sourceValueAsGetter.GetType();

            if (targetType.IsAssignableFrom(sourceGetterType)) return sourceValueAsGetter;

            ConstructorInfo? ctor = targetType.GetConstructor(new[] { sourceGetterType });
            if (ctor != null) return ctor.Invoke(new[] { sourceValueAsGetter });

            foreach (var iface in sourceGetterType.GetInterfaces())
            {
                ctor = targetType.GetConstructor(new[] { iface });
                if (ctor != null) return ctor.Invoke(new[] { sourceValueAsGetter });
            }

            Type? currentBaseType = sourceGetterType.BaseType;
            while (currentBaseType != null && currentBaseType != typeof(object))
            {
                ctor = targetType.GetConstructor(new[] { currentBaseType });
                if (ctor != null) return ctor.Invoke(new[] { sourceValueAsGetter });
                currentBaseType = currentBaseType.BaseType;
            }
            return null;
        }

        private IReadOnlyDictionary<string, PropertyInfo> GetGetterProperties(Type type)
        {
            if (_getterCache.TryGetValue(type, out var cachedProperties))
            {
                return cachedProperties;
            }
            var properties = new Dictionary<string, PropertyInfo>();
            var interfaces = type.GetInterfaces().ToList(); // Process interfaces
            // Add properties from the type itself first (most specific)
            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                 properties[prop.Name] = prop; 
            }
            // Then add/overwrite with interface properties - though concrete type usually wins for getters
            // This order might need adjustment based on desired precedence if type and interface declare same named prop
            foreach (var iface in interfaces)
            {
                foreach (var prop in iface.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (!properties.ContainsKey(prop.Name)) // Only add if not already defined by concrete type or more specific interface
                    {
                        properties[prop.Name] = prop;
                    }
                }
            }
            _getterCache[type] = properties;
            return properties;
        }

        private IReadOnlyDictionary<string, PropertyInfo> GetSetterProperties(Type recordType)
        {
            if (_setterCache.TryGetValue(recordType, out var cachedProperties))
            {
                return cachedProperties;
            }
            var properties = recordType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanWrite)
                .GroupBy(p => p.Name)
                .Select(g => g.OrderByDescending(pinfo => pinfo.DeclaringType == recordType ? 2 : (pinfo.DeclaringType?.IsAssignableFrom(recordType) ?? false) ? 1: 0).First())
                .ToDictionary(p => p.Name);
            _setterCache[recordType] = properties;
            return properties;
        }
        #endregion
    }
}