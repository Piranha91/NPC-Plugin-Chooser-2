// RecordDeltaPatcher.cs
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins.Records;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Mutagen.Bethesda.Plugins; 
using Noggog; // For Noggog.ExtendedList<>

namespace NPC_Plugin_Chooser_2.BackEnd
{
    /// <summary>
    /// Provides functionality to calculate differences (deltas) between records
    /// and apply these differences to a destination record. This class uses reflection
    /// and handles complex types, collections, and read-only collection properties.
    /// It also includes cycle detection to prevent stack overflows during recursive copying.
    /// </summary>
    public class RecordDeltaPatcher : OptionalUIModule // Assuming OptionalUIModule provides AppendLog
    {
        #region Fields and Initialization

        // Cache for PropertyInfo objects retrieved via reflection for getters, keyed by Type.
        private readonly Dictionary<Type, IReadOnlyDictionary<string, PropertyInfo>> _getterCache = new();
        // Cache for PropertyInfo objects retrieved via reflection for setters, keyed by Type.
        private readonly Dictionary<Type, IReadOnlyDictionary<string, PropertyInfo>> _setterCache = new();
        // Stores the values that have been patched onto records, keyed by FormKey, then by property name.
        // Used for conflict detection.
        private readonly Dictionary<FormKey, Dictionary<string, object?>> _patchedValues = new();

        /// <summary>
        /// Reinitializes the patcher by clearing any stored patched values.
        /// Reflection caches for getters and setters are typically preserved for performance
        /// unless a full reset of types is anticipated.
        /// </summary>
        public void Reinitialize()
        {
            _patchedValues.Clear();
            // Optionally clear reflection caches if needed:
            // _getterCache.Clear();
            // _setterCache.Clear();
        }
        #endregion

        #region Property Diff Classes

        /// <summary>
        /// Abstract base class for representing a difference in a property's value.
        /// </summary>
        public abstract class PropertyDiff
        {
            /// <summary>
            /// Gets the name of the property that has changed.
            /// </summary>
            public string PropertyName { get; }

            /// <summary>
            /// Initializes a new instance of the <see cref="PropertyDiff"/> class.
            /// </summary>
            /// <param name="propertyName">The name of the property.</param>
            protected PropertyDiff(string propertyName) => PropertyName = propertyName;

            /// <summary>
            /// Applies this property difference to the destination record.
            /// </summary>
            /// <param name="destinationRecord">The record to apply the changes to.</param>
            /// <param name="destPropInfo">The <see cref="PropertyInfo"/> for the property on the destination record.</param>
            /// <param name="patcher">The instance of the <see cref="RecordDeltaPatcher"/> to use for recursive operations and helpers.</param>
            public abstract void Apply(MajorRecord destinationRecord, PropertyInfo destPropInfo, RecordDeltaPatcher patcher);
            
            /// <summary>
            /// Gets the new value or representation of the value for this property difference.
            /// </summary>
            /// <returns>The new value.</returns>
            public abstract object? GetValue();
        }

        /// <summary>
        /// Represents a difference for a single value property (including complex objects/structs not treated as lists).
        /// </summary>
        public class ValueDiff : PropertyDiff
        {
            /// <summary>
            /// Gets the new value for the property.
            /// </summary>
            public object? NewValue { get; }

            /// <summary>
            /// Initializes a new instance of the <see cref="ValueDiff"/> class.
            /// </summary>
            /// <param name="propertyName">The name of the property.</param>
            /// <param name="newValue">The new value for the property.</param>
            public ValueDiff(string propertyName, object? newValue) : base(propertyName) => NewValue = newValue;

            /// <summary>
            /// Applies the value difference to the destination record.
            /// Handles direct assignment, conversion via constructors, recursive copying for complex types,
            /// and modification of read-only collection properties (like IDictionary).
            /// </summary>
            /// <param name="destinationRecord">The record to apply the changes to.</param>
            /// <param name="destPropInfo">The <see cref="PropertyInfo"/> for the property on the destination record. This PropertyInfo might represent a writable property or a read-only property that returns a modifiable collection.</param>
            /// <param name="patcher">The instance of the <see cref="RecordDeltaPatcher"/> to use for recursive operations and helpers.</param>
            public override void Apply(MajorRecord destinationRecord, PropertyInfo destPropInfo, RecordDeltaPatcher patcher)
            {
                var processedObjects = new HashSet<object>(ReferenceEqualityComparer.Instance); 

                if (NewValue == null) {
                    if (destPropInfo.CanWrite) destPropInfo.SetValue(destinationRecord, null);
                    else { // Handle clearing read-only collections
                        Type propertyType = destPropInfo.PropertyType;
                        if (typeof(IDictionary).IsAssignableFrom(propertyType)) { (destPropInfo.GetValue(destinationRecord) as IDictionary)?.Clear(); }
                        else if (typeof(IList).IsAssignableFrom(propertyType)) { (destPropInfo.GetValue(destinationRecord) as IList)?.Clear(); }
                        else patcher.AppendLog($"Warning (ValueDiff.Apply): Cannot set read-only property '{PropertyName}' to null as it's not a recognized clearable collection.", true);
                    }
                    return;
                }
                Type newValueType = NewValue.GetType(); Type destPropertyType = destPropInfo.PropertyType;

                if (destPropInfo.CanWrite) { // Property has a public setter
                    if (destPropertyType.IsAssignableFrom(newValueType)) destPropInfo.SetValue(destinationRecord, NewValue); // Direct assignment
                    else { // Types differ, attempt conversion or deep copy
                        object? convertedInstance = patcher.TryCreateInstanceFromGetter(destPropertyType, NewValue);
                        if (convertedInstance != null) { // Converted via constructor (e.g., IGetter -> Concrete)
                            destPropInfo.SetValue(destinationRecord, convertedInstance);
                            // If conversion might not be exhaustive, recurse to copy remaining/overridden properties
                            if (!patcher.IsSimpleType(destPropertyType) && !patcher.IsSimpleType(newValueType) && destPropertyType != newValueType) {
                               object? targetForRecursion = destPropInfo.GetValue(destinationRecord);
                               if (targetForRecursion != null) { patcher.CopyPropertiesRecursively(NewValue, targetForRecursion, processedObjects); if (destPropertyType.IsValueType) destPropInfo.SetValue(destinationRecord, targetForRecursion); }
                            }
                        } else { // Fallback: Create instance, then recursively copy properties
                            object? destinationPropertyValue = destPropInfo.GetValue(destinationRecord); bool newInstanceCreated = false;
                            if (destinationPropertyValue == null && !destPropertyType.IsValueType) { 
                                if (destPropertyType.IsInterface || destPropertyType.IsAbstract) { patcher.AppendLog($"Error (ValueDiff.Apply complex): Cannot create instance of interface/abstract type '{destPropertyType.FullName}' for property '{PropertyName}'.", true); return; }
                                try { destinationPropertyValue = Activator.CreateInstance(destPropertyType); newInstanceCreated = true; } 
                                catch (Exception ex) { patcher.AppendLog($"Error (ValueDiff.Apply complex): Failed to Activator.CreateInstance for '{destPropertyType.FullName}'. {ex.Message}", true); return; }
                            } else if (destPropertyType.IsValueType) { // For structs, ensure we have an instance to populate
                                 var underlyingType = Nullable.GetUnderlyingType(destPropertyType);
                                 if (destinationPropertyValue == null && underlyingType != null) { destinationPropertyValue = Activator.CreateInstance(underlyingType); }
                                 else if (destinationPropertyValue == null || (destinationPropertyValue.GetType() != destPropertyType && (underlyingType == null || destinationPropertyValue.GetType() != underlyingType))) { destinationPropertyValue = Activator.CreateInstance(destPropertyType); }
                            }
                            if (destinationPropertyValue == null) { patcher.AppendLog($"Error (ValueDiff.Apply complex): Could not obtain destination for '{PropertyName}'.", true); return; }
                            
                            patcher.CopyPropertiesRecursively(NewValue, destinationPropertyValue, processedObjects);
                            if (newInstanceCreated || destPropertyType.IsValueType) destPropInfo.SetValue(destinationRecord, destinationPropertyValue); // Set back if new class instance or any struct
                        }
                    }
                } else { // Property is read-only (CanWrite is false), attempt to modify contents if collection
                    
                    // --- MODIFIED LOGIC TO FIX COLLECTION HANDLING ---

                    // Flag to prevent further recursion if the property is handled as a collection.
                    // This stops the code from trying to recursively copy the properties of the collection
                    // object itself (e.g., IsFixedSize, Count) after its contents have already been copied.
                    bool handledAsCollection = false;

                    // More robust checks for list/dictionary types, handling both generic and non-generic interfaces.
                    bool isDictionary = (destPropertyType.IsGenericType && destPropertyType.GetGenericTypeDefinition() == typeof(IDictionary<,>)) || typeof(IDictionary).IsAssignableFrom(destPropertyType);
                    bool isList = (destPropertyType.IsGenericType && destPropertyType.GetGenericTypeDefinition() == typeof(IList<>)) || typeof(IList).IsAssignableFrom(destPropertyType) || (destPropertyType.IsGenericType && destPropertyType.GetGenericTypeDefinition() == typeof(Noggog.ExtendedList<>));

                    if (isDictionary && NewValue is IEnumerable sourceDictEnum)
                    {
                        patcher.CopyDictionaryProperty(sourceDictEnum, destPropInfo, destinationRecord, processedObjects);
                        handledAsCollection = true;
                    }
                    else if (isList && NewValue is IEnumerable sourceListEnum)
                    {
                        patcher.CopyListProperty(sourceListEnum, destPropInfo, destinationRecord, processedObjects);
                        handledAsCollection = true;
                    }
                    
                    // Only proceed if the property was not handled as a modifiable collection.
                    // This path is for read-only complex objects that are not collections.
                    if (!handledAsCollection)
                    {
                        object? readOnlyDestInstance = destPropInfo.GetValue(destinationRecord);
                        if (readOnlyDestInstance != null && !patcher.IsSimpleType(readOnlyDestInstance.GetType()))
                        {
                            patcher.CopyPropertiesRecursively(NewValue, readOnlyDestInstance, processedObjects);
                            if (destPropertyType.IsValueType) patcher.AppendLog($"Warning (ValueDiff.Apply): Property '{PropertyName}' is a read-only struct. Changes made by recursion will likely be lost as it cannot be set back.", true);
                        }
                        else
                        {
                            patcher.AppendLog($"Warning (ValueDiff.Apply): Property '{PropertyName}' is read-only and not a recognized modifiable collection or suitable complex object. Cannot apply changes. ValueType: {destPropertyType.IsValueType}, SimpleType: {patcher.IsSimpleType(destPropertyType)}", true);
                        }
                    }
                    // --- END OF MODIFIED LOGIC ---
                }
            }
            public override object? GetValue() => NewValue;
        }

        /// <summary>
        /// Represents a difference for a list property.
        /// </summary>
        public class ListDiff : PropertyDiff
        {
            /// <summary>
            /// Gets the items that should be in the list.
            /// </summary>
            public IReadOnlyList<object?> Items { get; }

            /// <summary>
            /// Initializes a new instance of the <see cref="ListDiff"/> class.
            /// </summary>
            /// <param name="propertyName">The name of the list property.</param>
            /// <param name="sourceList">The source list containing the new items.</param>
            public ListDiff(string propertyName, IEnumerable sourceList) : base(propertyName) { Items = sourceList.Cast<object?>().ToList(); }

            /// <summary>
            /// Applies the list difference by clearing the destination list and adding all items from this diff.
            /// </summary>
            /// <param name="destinationRecord">The record to apply the changes to.</param>
            /// <param name="destPropInfo">The <see cref="PropertyInfo"/> for the list property on the destination record.</param>
            /// <param name="patcher">The instance of the <see cref="RecordDeltaPatcher"/>.</param>
            public override void Apply(MajorRecord destinationRecord, PropertyInfo destPropInfo, RecordDeltaPatcher patcher) { 
                patcher.CopyListProperty(Items, destPropInfo, destinationRecord, new HashSet<object>(ReferenceEqualityComparer.Instance)); 
            }
            public override object? GetValue() => Items;
        }
        #endregion

        #region Public Interface

        /// <summary>
        /// Compares two dynamic objects (source and target) and returns a list of property differences.
        /// </summary>
        /// <param name="source">The source object.</param>
        /// <param name="target">The target object to compare against.</param>
        /// <returns>A list of <see cref="PropertyDiff"/> objects representing the differences.</returns>
        public IReadOnlyList<PropertyDiff> GetPropertyDiffs(dynamic source, dynamic target)
        {
            if (source is null) throw new ArgumentNullException(nameof(source)); 
            if (target is null) throw new ArgumentNullException(nameof(target));
            var differences = new List<PropertyDiff>(); 
            Type sourceType = source.GetType(); Type targetType = target.GetType();
            var getterProps = GetGetterProperties(sourceType); 
            var targetReadableProps = GetGetterProperties(targetType); 

            foreach (var piKvp in getterProps) {
                PropertyInfo sourcePi = piKvp.Value; string propertyName = sourcePi.Name;
                object? sourceValue = sourcePi.GetValue(source); object? targetValue = null;
                if (targetReadableProps.TryGetValue(propertyName, out var targetPi)) targetValue = targetPi.GetValue(target);
                
                bool treatAsListDiff = false; 
                if (sourceValue != null && !(sourceValue is string)) { 
                    if (sourceValue is IList) treatAsListDiff = true; // Covers arrays, List<T>, ExtendedList<T>
                }

                if (treatAsListDiff) {
                    var sourceListForDiff = (IEnumerable)sourceValue!; 
                    var targetList = targetValue as IEnumerable; bool areEqual = false;
                    if (targetList != null) { 
                        var sourceObjList = sourceListForDiff.Cast<object?>().ToList(); 
                        var targetObjList = targetList.Cast<object?>().ToList(); 
                        if (sourceObjList.Count == targetObjList.Count) areEqual = sourceObjList.SequenceEqual(targetObjList); // Shallow list item comparison
                    }
                    if (!areEqual) differences.Add(new ListDiff(propertyName, sourceListForDiff));
                } else {
                    if (!ValuesAreEqual(sourceValue, targetValue, propertyName)) differences.Add(new ValueDiff(propertyName, sourceValue));
                }
            }
            return differences;
        }
        
        /// <summary>
        /// Helper method to compare two values for equality. Relies on the object's Equals method.
        /// For complex types, this means value equality if Equals is overridden, otherwise reference equality.
        /// </summary>
        private bool ValuesAreEqual(object? val1, object? val2, string propertyNameForDebug = "") {
            if (ReferenceEquals(val1, val2)) return true; 
            if (val1 == null || val2 == null) return false; 
            // Relies on type's Equals override for value comparison (e.g., FormLink, TranslatedString)
            if (val1.Equals(val2)) return true; 
            return false; 
        }

        /// <summary>
        /// Applies a collection of property differences to a destination record.
        /// Handles writable properties and read-only collection properties (IDictionary, IList).
        /// </summary>
        /// <param name="destination">The destination record (must be a <see cref="MajorRecord"/>).</param>
        /// <param name="diffs">The collection of <see cref="PropertyDiff"/> to apply.</param>
        public void ApplyPropertyDiffs(dynamic destination, IEnumerable<PropertyDiff> diffs) {
            if (destination is null) throw new ArgumentNullException(nameof(destination)); 
            if (diffs is null) throw new ArgumentNullException(nameof(diffs));
            if (destination is not MajorRecord concreteDestination) { AppendLog($"Error: Destination for ApplyPropertyDiffs must be a MajorRecord. Got {destination.GetType().FullName}", true); return; }

            var allDestReadableProps = GetGetterProperties(concreteDestination.GetType()); 
            var destWritableSetterProps = GetSetterProperties(concreteDestination.GetType()); 
            
            _patchedValues.TryGetValue(concreteDestination.FormKey, out var recordPatchedValues);
            if (recordPatchedValues == null && !concreteDestination.FormKey.IsNull) { 
                recordPatchedValues = new Dictionary<string, object?>(); 
                _patchedValues[concreteDestination.FormKey] = recordPatchedValues; 
            }

            foreach (var diff in diffs) {
                PropertyInfo? propInfoToApply = null; 

                if (destWritableSetterProps.TryGetValue(diff.PropertyName, out var writablePi)) {
                    propInfoToApply = writablePi;
                } else if (allDestReadableProps.TryGetValue(diff.PropertyName, out var readablePi)) {
                    Type readablePropType = readablePi.PropertyType;

                    // Robust check for various collection types that are read-only but modifiable
                    if ((readablePropType.IsGenericType && readablePropType.GetGenericTypeDefinition() == typeof(IDictionary<,>)) || 
                        typeof(IDictionary).IsAssignableFrom(readablePropType) ||
                        (readablePropType.IsGenericType && readablePropType.GetGenericTypeDefinition() == typeof(IList<>)) ||
                        typeof(IList).IsAssignableFrom(readablePropType) ||
                        (readablePropType.IsGenericType && readablePropType.GetGenericTypeDefinition() == typeof(Noggog.ExtendedList<>)))
                    {
                        propInfoToApply = readablePi;
                    } else { 
                        AppendLog($"Warning: Setter for property '{diff.PropertyName}' not found, and it's not a recognized modifiable read-only collection. Skipping diff. Type: {readablePropType.FullName}"); 
                        continue; 
                    }
                } else { 
                    AppendLog($"Warning: Property '{diff.PropertyName}' not found on destination type '{concreteDestination.GetType().FullName}'. Skipping diff."); 
                    continue; 
                }
                
                if (propInfoToApply == null) { AppendLog($"Internal Error: propInfoToApply is null for '{diff.PropertyName}'. This should have been caught by earlier checks.", true); continue; }

                object? newValueToStore = diff.GetValue();
                // Conflict checking logic (remains shallow for collections for now)
                if (recordPatchedValues != null && recordPatchedValues.TryGetValue(diff.PropertyName, out object? oldValue)) { 
                    bool isConflict = false;
                    if (oldValue is IReadOnlyList<object?> oldList && newValueToStore is IReadOnlyList<object?> newList) { if (!oldList.SequenceEqual(newList)) isConflict = true; } 
                    else if (!Equals(oldValue, newValueToStore)) { isConflict = true; }
                    if (isConflict) AppendLog($"CONFLICT: Property '{diff.PropertyName}' on record '{concreteDestination.EditorID ?? concreteDestination.FormKey.ToString()}' already patched, overwriting.");
                }
                if (recordPatchedValues != null) recordPatchedValues[diff.PropertyName] = newValueToStore;

                diff.Apply(concreteDestination, propInfoToApply, this);
            }
        }

        /// <summary>
        /// Formats a value for logging purposes.
        /// </summary>
        private string FormatValue(object? val) { 
            if (val == null) return "null";
            if (val is string s) return $"\"{s}\"";
            if (val is IEnumerable enumerable && val is not string) return $"[collection({enumerable.Cast<object>().Count()}) items]";
            return val.ToString() ?? "N/A";
        }
        #endregion

        #region Core Recursive Copy Logic

        /// <summary>
        /// Determines if a type is considered "simple" (primitive, enum, string, common value types, FormLink variants),
        /// meaning it typically doesn't require recursive property copying.
        /// </summary>
        private bool IsSimpleType(Type type) { 
            return type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal) || 
                   type == typeof(DateTime) || type == typeof(Guid) ||
                   (type.IsValueType && Nullable.GetUnderlyingType(type) == null && type.Namespace != null && type.Namespace.StartsWith("System") && !type.IsGenericType) ||
                   (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IFormLinkGetter<>)) ||
                   (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IFormLink<>)) ||
                   (type.IsValueType && type.IsGenericType && type.GetGenericTypeDefinition() == typeof(FormLink<>));
        }

        /// <summary>
        /// Recursively copies properties from a source object to a destination object.
        /// Handles direct assignments, collections (lists and dictionaries), and nested complex objects.
        /// Includes cycle detection to prevent stack overflows.
        /// </summary>
        /// <param name="source">The source object to copy from.</param>
        /// <param name="destination">The destination object to copy to. This object's properties will be modified.</param>
        /// <param name="processedObjects">A set used to track already processed source objects in the current copy chain to prevent cyclical recursion.</param>
        private void CopyPropertiesRecursively(object source, object destination, HashSet<object> processedObjects)
        {
            if (source == null || destination == null) return;

            if (processedObjects.Contains(source)) {
                // Cycle detected or this source object's graph part already processed in this call chain.
                return;
            }
            processedObjects.Add(source);

            Type sourceType = source.GetType(); Type destType = destination.GetType(); 
            var sourceReadableProps = this.GetGetterProperties(sourceType);
            var destinationAllProps = this.GetGetterProperties(destType); // Get all readable properties, will check CanWrite individually

            foreach (var srcPropKvp in sourceReadableProps) {
                string propName = srcPropKvp.Key; PropertyInfo srcProp = srcPropKvp.Value;
                if (!destinationAllProps.TryGetValue(propName, out var destProp)) continue; 
                
                object? sourceValue = srcProp.GetValue(source); Type destPropType = destProp.PropertyType; 

                if (sourceValue == null) { // Handle setting null
                    if (destProp.CanWrite) destProp.SetValue(destination, null);
                    else { // For read-only collections, null typically means clear
                        if (typeof(IDictionary).IsAssignableFrom(destPropType)) (destProp.GetValue(destination) as IDictionary)?.Clear();
                        else if (typeof(IList).IsAssignableFrom(destPropType)) (destProp.GetValue(destination) as IList)?.Clear();
                    }
                    continue;
                }
                Type sourceValueType = sourceValue.GetType();

                if (destProp.CanWrite) { // --- Destination property is Writable ---
                    if (destPropType.IsAssignableFrom(sourceValueType)) destProp.SetValue(destination, sourceValue); // Direct assign
                    else if (destPropType.IsGenericType && destPropType.GetGenericTypeDefinition() == typeof(Noggog.ExtendedList<>) && sourceValue is IEnumerable elSourceEnumW && !(sourceValue is string)) {
                        this.CopyListProperty(elSourceEnumW, destProp, destination, processedObjects); 
                    } else if (typeof(IDictionary).IsAssignableFrom(destPropType) && sourceValue is IEnumerable dictSourceEnumW && !(sourceValue is string)) {
                        this.CopyDictionaryProperty(dictSourceEnumW, destProp, destination, processedObjects); 
                    } else if (typeof(IList).IsAssignableFrom(destPropType) && !(destPropType.IsGenericType && destPropType.GetGenericTypeDefinition() == typeof(Noggog.ExtendedList<>)) && sourceValue is IEnumerable listSourceEnumW && !(sourceValue is string)) {
                        this.CopyListProperty(listSourceEnumW, destProp, destination, processedObjects); 
                    } else { // Writable Complex Object/Struct
                        object? convertedInstance = this.TryCreateInstanceFromGetter(destPropType, sourceValue);
                        if (convertedInstance != null) {
                            destProp.SetValue(destination, convertedInstance);
                            if (!this.IsSimpleType(destPropType) && !this.IsSimpleType(sourceValueType) && destPropType != sourceValueType) {
                                object? targetForRec = destProp.GetValue(destination);
                                if (targetForRec != null) { this.CopyPropertiesRecursively(sourceValue, targetForRec, processedObjects); if (destPropType.IsValueType) destProp.SetValue(destination, targetForRec); }
                            }
                        } else {
                            object? nestedDest = destProp.GetValue(destination); bool newCreated = false;
                            if (nestedDest == null && !destPropType.IsValueType) { 
                                if (destPropType.IsInterface || destPropType.IsAbstract) { AppendLog($"Error_CPR WritableComplex: Cannot create interface/abstract '{destPropType.Name}' for '{propName}'.",true); continue;}
                                try { nestedDest = Activator.CreateInstance(destPropType); newCreated = true; } catch (Exception ex) { AppendLog($"Error_CPR WritableComplex: CreateInstance failed for '{destPropType.Name}' for '{propName}'. {ex.Message}",true); continue; }
                             } else if (destPropType.IsValueType) { 
                                nestedDest = destProp.GetValue(destination); 
                                if(nestedDest == null || nestedDest.GetType() != destPropType) nestedDest = Activator.CreateInstance(destPropType); // Ensure correct type for structs
                             }
                            if (nestedDest == null) { AppendLog($"Error_CPR WritableComplex: Could not obtain instance for '{propName}'.",true); continue; }
                            this.CopyPropertiesRecursively(sourceValue, nestedDest, processedObjects);
                            if (newCreated || destPropType.IsValueType) destProp.SetValue(destination, nestedDest);
                        }
                    }
                } else { // --- Destination property is Read-Only (destProp.CanWrite is false) ---
                    if (typeof(IDictionary).IsAssignableFrom(destPropType) && sourceValue is IEnumerable dictSourceEnumRO && !(sourceValue is string)) {
                        this.CopyDictionaryProperty(dictSourceEnumRO, destProp, destination, processedObjects);
                    } else if (typeof(IList).IsAssignableFrom(destPropType) && sourceValue is IEnumerable listSourceEnumRO && !(sourceValue is string)) {
                        this.CopyListProperty(listSourceEnumRO, destProp, destination, processedObjects);
                    } else { 
                        object? readOnlyInstance = destProp.GetValue(destination);
                        if (readOnlyInstance != null && !this.IsSimpleType(readOnlyInstance.GetType())) {
                            this.CopyPropertiesRecursively(sourceValue, readOnlyInstance, processedObjects);
                            if (destPropType.IsValueType) AppendLog($"Warning_CPR: Property '{propName}' is a read-only struct. Changes by recursion likely lost.", true);
                        } else {
                            // --- MUTE SPECIFIC EXPECTED WARNINGS ---
                            // This prevents logging warnings for known, benign read-only properties on collections (like Count, IsFixedSize, etc.)
                            bool isExpectedReadOnlySimpleProperty = 
                                (propName == "Count" && destPropType == typeof(int)) ||
                                (propName == "IsReadOnly" && destPropType == typeof(bool)) ||
                                (propName == "IsSynchronized" && destPropType == typeof(bool)) ||
                                (propName == "Capacity" && destPropType == typeof(int)) ||
                                (propName == "IsFixedSize" && destPropType == typeof(bool)); // Added to prevent unnecessary warnings on dictionaries

                            if (!isExpectedReadOnlySimpleProperty)
                            {
                                AppendLog($"Warning_CPR: Property '{propName}' on '{destType.Name}' is read-only and not a recognized modifiable collection or suitable complex object. SourceValueType: '{sourceValueType.Name}', DestPropType: {destPropType.FullName}", true);
                            }
                            // --- END MUTE ---
                        }
                    }
                }
            } 
            // processedObjects.Remove(source); // Optional: remove if an object can be part of multiple distinct sub-graphs from the root that need independent processing. For simple cycle breaking, not removing is safer.
        }
        
        /// <summary>
        /// Copies items from a source enumerable to a destination list property.
        /// The destination list is obtained via the property getter, cleared, and then populated.
        /// Handles item type conversion and recursive copying for complex list items.
        /// </summary>
        private void CopyListProperty(IEnumerable sourceEnumerable, PropertyInfo destPropertyInfo, object parentDestinationObject, HashSet<object> processedObjects) 
        {
            object? destListObj = destPropertyInfo.GetValue(parentDestinationObject); 
            Type destListPropertyType = destPropertyInfo.PropertyType;
            if (destListObj == null) {
                if (destPropertyInfo.CanWrite) { /* ... create list instance and SetValue ... */ 
                    if (destListPropertyType.IsInterface || destListPropertyType.IsAbstract) { 
                        if (destListPropertyType.IsGenericType && destListPropertyType.GetGenericTypeDefinition() == typeof(Noggog.ExtendedList<>)) destListObj = Activator.CreateInstance(destListPropertyType);
                        else if (destListPropertyType.IsGenericType && destListPropertyType.GetGenericArguments().Length > 0 && (destListPropertyType.GetGenericTypeDefinition() == typeof(IList<>) || destListPropertyType.GetGenericTypeDefinition() == typeof(ICollection<>) || destListPropertyType.GetGenericTypeDefinition() == typeof(IEnumerable<>))) { Type itemType = destListPropertyType.GetGenericArguments()[0]; Type concreteListType = typeof(List<>).MakeGenericType(itemType); destListObj = Activator.CreateInstance(concreteListType); }
                        else { AppendLog($"Warning (CopyListProperty): Cannot create instance for null list property '{destPropertyInfo.Name}' (interface/abstract). Skipping."); return; }
                    } else { try { destListObj = Activator.CreateInstance(destListPropertyType); } catch (Exception ex) { AppendLog($"Error (CopyListProperty): Failed to create list instance for '{destPropertyInfo.Name}'. {ex.Message}", true); return; } }
                    destPropertyInfo.SetValue(parentDestinationObject, destListObj);
                } else { AppendLog($"Warning (CopyListProperty): List property '{destPropertyInfo.Name}' is null and read-only. Cannot create or populate.", true); return; }
            }
            if (destListObj is not IList destList) { AppendLog($"Warning (CopyListProperty): Property '{destPropertyInfo.Name}' is not IList. Type: {destListObj?.GetType().FullName}. Skipping."); return; }
            Type listActualType = destList.GetType(); 
            if (!listActualType.IsGenericType || listActualType.GetGenericArguments().Length == 0) { AppendLog($"Warning (CopyListProperty): Dest list '{destPropertyInfo.Name}' not generic. Type: {listActualType.FullName}. Skipping."); return; }
            var destItemType = listActualType.GetGenericArguments()[0]; destList.Clear();
            foreach (var listItemSource in sourceEnumerable) {
                if (listItemSource == null) { if (!destItemType.IsValueType || Nullable.GetUnderlyingType(destItemType) != null) destList.Add(null); continue; }
                Type listItemSourceType = listItemSource.GetType();
                if (destItemType.IsAssignableFrom(listItemSourceType)) destList.Add(listItemSource);
                else {
                    object? concreteListItem = this.TryCreateInstanceFromGetter(destItemType, listItemSource);
                    if (concreteListItem != null) { if (!this.IsSimpleType(destItemType)) this.CopyPropertiesRecursively(listItemSource, concreteListItem, processedObjects); destList.Add(concreteListItem); }
                    else {
                        if (destItemType.IsInterface || destItemType.IsAbstract) { AppendLog($"Error (CopyListProperty): Cannot CreateInstance for list item interface/abstract '{destItemType.Name}'. Skipping.", true); continue; }
                        try { concreteListItem = Activator.CreateInstance(destItemType); this.CopyPropertiesRecursively(listItemSource, concreteListItem, processedObjects); destList.Add(concreteListItem); }
                        catch (Exception ex) { AppendLog($"Error (CopyListProperty): Failed to process list item for {destPropertyInfo.Name}. {ex.Message}", true); }
                    }
                }
            }
        }

        /// <summary>
        /// Copies entries from a source enumerable (of KeyValuePairs) to a destination dictionary property.
        /// The destination dictionary is obtained via the property getter, cleared, and then populated.
        /// Handles key/value type conversion and recursive copying for complex dictionary values.
        /// </summary>
        private void CopyDictionaryProperty(IEnumerable sourceEnumerable, PropertyInfo destPropertyInfo, object parentDestinationObject, HashSet<object> processedObjects) {
            object? destDictObj = destPropertyInfo.GetValue(parentDestinationObject); Type destDictPropertyType = destPropertyInfo.PropertyType;
            if (destDictObj == null) {
                if (destPropertyInfo.CanWrite) { /* ... create dictionary instance and SetValue ... */
                    if (destDictPropertyType.IsInterface || destDictPropertyType.IsAbstract) {
                        if (destDictPropertyType.IsGenericType && destDictPropertyType.GetGenericArguments().Length == 2 && (destDictPropertyType.GetGenericTypeDefinition() == typeof(IDictionary<,>) || destDictPropertyType.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>))) { Type keyType = destDictPropertyType.GetGenericArguments()[0]; Type valueType = destDictPropertyType.GetGenericArguments()[1]; Type concreteDictType = typeof(Dictionary<,>).MakeGenericType(keyType, valueType); destDictObj = Activator.CreateInstance(concreteDictType); }
                        else { AppendLog($"Warning (CopyDictionaryProperty): Cannot create instance for null dictionary property '{destPropertyInfo.Name}' (interface/abstract). Skipping."); return; }
                    } else { try { destDictObj = Activator.CreateInstance(destDictPropertyType); } catch (Exception ex) { AppendLog($"Error (CopyDictionaryProperty): Failed to create dictionary instance for '{destPropertyInfo.Name}'. {ex.Message}", true); return; } }
                    destPropertyInfo.SetValue(parentDestinationObject, destDictObj);
                } else { AppendLog($"Warning (CopyDictionaryProperty): Dictionary property '{destPropertyInfo.Name}' is null and read-only. Cannot create or populate.", true); return; }
            }
            if (destDictObj is not IDictionary destDict) { AppendLog($"Warning (CopyDictionaryProperty): Property '{destPropertyInfo.Name}' is not IDictionary. Type: {destDictObj?.GetType().FullName}. Skipping."); return; }
            Type dictActualType = destDict.GetType();
            if (!dictActualType.IsGenericType || dictActualType.GetGenericArguments().Length != 2) { AppendLog($"Warning (CopyDictionaryProperty): Dest dictionary '{destPropertyInfo.Name}' not generic with 2 args. Type: {dictActualType.FullName}. Skipping."); return; }
            Type keyDestType = dictActualType.GetGenericArguments()[0]; Type valueDestType = dictActualType.GetGenericArguments()[1]; destDict.Clear();
            foreach (object kvpObject in sourceEnumerable) {
                PropertyInfo? keyProp = kvpObject.GetType().GetProperty("Key"); PropertyInfo? valueProp = kvpObject.GetType().GetProperty("Value");
                if (keyProp == null || valueProp == null) { AppendLog($"Warning (CopyDictionaryProperty): Item in source for '{destPropertyInfo.Name}' not KeyValuePair. Skipping."); continue; }
                object? sourceKey = keyProp.GetValue(kvpObject); object? sourceVal = valueProp.GetValue(kvpObject);
                if (sourceKey == null) { AppendLog($"Warning (CopyDictionaryProperty): Source key is null for '{destPropertyInfo.Name}'. Skipping."); continue; }
                object? destKey; 
                if (keyDestType.IsAssignableFrom(sourceKey.GetType())) destKey = sourceKey;
                else {
                    destKey = this.TryCreateInstanceFromGetter(keyDestType, sourceKey);
                    if (destKey == null && !this.IsSimpleType(keyDestType)) { try { destKey = Activator.CreateInstance(keyDestType); /* this.CopyPropertiesRecursively(sourceKey, destKey, processedObjects); // Keys usually not deep copied */ } catch (Exception ex) { AppendLog($"Error (CopyDictionaryProperty) creating key '{sourceKey}': {ex.Message}", true); destKey = null; } }
                    if (destKey == null) { AppendLog($"Warning (CopyDictionaryProperty): Could not convert key '{sourceKey}'. Skipping entry."); continue; }
                }
                object? destValue; 
                if (sourceVal == null) destValue = null;
                else if (valueDestType.IsAssignableFrom(sourceVal.GetType())) destValue = sourceVal;
                else {
                    destValue = this.TryCreateInstanceFromGetter(valueDestType, sourceVal);
                    if (destValue != null) { if (!this.IsSimpleType(valueDestType)) this.CopyPropertiesRecursively(sourceVal, destValue, processedObjects); }
                    else {
                         if (valueDestType.IsInterface || valueDestType.IsAbstract) { AppendLog($"Error (CopyDictionaryProperty): Cannot CreateInstance for dict value interface/abstract '{valueDestType.Name}'. Skipping.", true); continue; }
                        try { destValue = Activator.CreateInstance(valueDestType); this.CopyPropertiesRecursively(sourceVal, destValue, processedObjects); }
                        catch (Exception ex) { AppendLog($"Error (CopyDictionaryProperty): Failed to process dict value for {destPropertyInfo.Name}. {ex.Message}", true); continue; }
                    }
                }
                try { destDict[destKey] = destValue; } catch (Exception ex) { AppendLog($"Error (CopyDictionaryProperty): Failed to add item to dictionary '{destPropertyInfo.Name}'. Key: '{destKey}'. {ex.Message}", true); }
            }
        }
        #endregion

        #region Reflection Caching & Helpers

        /// <summary>
        /// Attempts to create an instance of <paramref name="targetType"/> using <paramref name="sourceValueAsGetter"/>,
        /// typically by finding a constructor on <paramref name="targetType"/> that accepts the type of 
        /// <paramref name="sourceValueAsGetter"/> or one of its implemented interfaces or base types.
        /// Common Mutagen pattern: new ConcreteType(IGetterInterface).
        /// </summary>
        /// <param name="targetType">The desired type of the new instance.</param>
        /// <param name="sourceValueAsGetter">The source object, often a getter interface implementation.</param>
        /// <returns>A new instance of <paramref name="targetType"/>, or null if no suitable constructor is found or conversion fails.</returns>
        private object? TryCreateInstanceFromGetter(Type targetType, object sourceValueAsGetter) { 
            if (sourceValueAsGetter == null) return null; Type sourceGetterType = sourceValueAsGetter.GetType(); 
            if (targetType.IsAssignableFrom(sourceGetterType)) return sourceValueAsGetter; // Already compatible
            ConstructorInfo? ctor = targetType.GetConstructor(new[] { sourceGetterType }); if (ctor != null) return ctor.Invoke(new[] { sourceValueAsGetter }); 
            foreach (var iface in sourceGetterType.GetInterfaces()) { ctor = targetType.GetConstructor(new[] { iface }); if (ctor != null) return ctor.Invoke(new[] { sourceValueAsGetter }); } 
            Type? currentBaseType = sourceGetterType.BaseType; 
            while (currentBaseType != null && currentBaseType != typeof(object)) { ctor = targetType.GetConstructor(new[] { currentBaseType }); if (ctor != null) return ctor.Invoke(new[] { sourceValueAsGetter }); currentBaseType = currentBaseType.BaseType; } 
            return null; 
        }

        /// <summary>
        /// Gets all public instance readable properties for a given type, utilizing a cache.
        /// Filters out indexer properties.
        /// Prioritizes properties from the concrete type, then fills in from interfaces.
        /// </summary>
        /// <param name="type">The type to get properties for.</param>
        /// <returns>A read-only dictionary of property names to <see cref="PropertyInfo"/> objects.</returns>
        private IReadOnlyDictionary<string, PropertyInfo> GetGetterProperties(Type type)
        {
            if (_getterCache.TryGetValue(type, out var cachedProperties)) return cachedProperties;

            var properties = new Dictionary<string, PropertyInfo>();
            
            // Get properties from concrete type first, excluding indexers
            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (prop.GetIndexParameters().Length == 0) // Exclude indexer properties
                {
                    properties[prop.Name] = prop;
                }
            }
            
            // Then fill in from interfaces if not already covered by concrete type, excluding indexers
            var interfaces = type.GetInterfaces();
            foreach (var iface in interfaces)
            {
                foreach (var prop in iface.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (prop.GetIndexParameters().Length == 0 && !properties.ContainsKey(prop.Name)) // Exclude indexers
                    {
                        properties[prop.Name] = prop;
                    }
                }
            }
            _getterCache[type] = properties;
            return properties;
        }

        /// <summary>
        /// Gets all public instance writable properties for a given type, utilizing a cache.
        /// Filters out indexer properties.
        /// Prioritizes properties declared on the most derived type.
        /// </summary>
        /// <param name="recordType">The type to get writable properties for.</param>
        /// <returns>A read-only dictionary of property names to <see cref="PropertyInfo"/> objects for writable properties.</returns>
        private IReadOnlyDictionary<string, PropertyInfo> GetSetterProperties(Type recordType)
        {
            if (_setterCache.TryGetValue(recordType, out var cachedProperties)) return cachedProperties;

            var properties = recordType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanWrite && p.GetIndexParameters().Length == 0) // Exclude indexers here too
                .GroupBy(p => p.Name) 
                .Select(g => g.OrderByDescending(pinfo => pinfo.DeclaringType == recordType ? 2 : (pinfo.DeclaringType?.IsAssignableFrom(recordType) ?? false) ? 1 : 0).First())
                .ToDictionary(p => p.Name);
                
            _setterCache[recordType] = properties;
            return properties;
        }
        #endregion
    }
}