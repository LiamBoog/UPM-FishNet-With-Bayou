﻿using FishNet.CodeGenerating.Helping;
using FishNet.CodeGenerating.Helping.Extension;
using FishNet.Object.Synchronizing;
using FishNet.Object.Synchronizing.Internal;
using FishNet.Serializing;
using FishNet.Transporting;
using MonoFN.Cecil;
using MonoFN.Cecil.Cil;
using System.Collections.Generic;
using MonoFN.Collections.Generic;

namespace FishNet.CodeGenerating.Processing
{
    internal class NetworkBehaviourSyncProcessor
    {
        #region Private.
        /// <summary>
        /// Last instruction to read a sync type.
        /// </summary>
        private Instruction _lastReadInstruction = null;
        /// <summary>
        /// Sync objects, such as get and set, created during this process. Used to skip modifying created methods.
        /// </summary>
        private List<object> _createdSyncTypeMethodDefinitions = new List<object>();
        /// <summary>
        /// ReadSyncVar methods which have had their base call already made.
        /// </summary>
        private HashSet<MethodDefinition> _baseCalledReadSyncVars = new HashSet<MethodDefinition>();
        #endregion

        #region Const.
        private const string SYNCHANDLER_PREFIX = "syncHandler_";
        private const string ACCESSOR_PREFIX = "sync___";
        private const string SETSYNCINDEX_METHOD_NAME = "SetSyncIndex";
        private const string INITIALIZEINSTANCE_METHOD_NAME = "InitializeInstance";
        private const string GETSERIALIZEDTYPE_METHOD_NAME = "GetSerializedType";
        #endregion

        /// <summary>
        /// Processes SyncVars and Objects.
        /// </summary>
        /// <param name="typeDef"></param>
        /// <param name="diagnostics"></param>
        internal bool Process(TypeDefinition typeDef, List<(SyncType, ProcessedSync)> allProcessedSyncs, uint syncTypeStartCount)
        {
            bool modified = false;
            _createdSyncTypeMethodDefinitions.Clear();
            _lastReadInstruction = null;

            FieldDefinition[] fieldDefs = typeDef.Fields.ToArray();
            foreach (FieldDefinition fd in fieldDefs)
            {
                CustomAttribute syncAttribute;
                SyncType st = GetSyncType(fd, out syncAttribute);

                //Not a sync type field.
                if (st == SyncType.Unset)
                    continue;

                if (st == SyncType.Variable)
                {
                    if (TryCreateSyncVar(syncTypeStartCount, allProcessedSyncs, typeDef, fd, syncAttribute))
                        syncTypeStartCount++;
                }
                else if (st == SyncType.List)
                {
                    if (TryCreateSyncList(syncTypeStartCount, allProcessedSyncs, typeDef, fd, syncAttribute))
                        syncTypeStartCount++;
                }
                else if (st == SyncType.Dictionary)
                {
                    if (TryCreateSyncDictionary(syncTypeStartCount, allProcessedSyncs, typeDef, fd, syncAttribute))
                        syncTypeStartCount++;
                }
                else if (st == SyncType.Custom)
                {
                    if (TryCreateCustom(syncTypeStartCount, allProcessedSyncs, typeDef, fd, syncAttribute))
                        syncTypeStartCount++;
                }

                modified = true;
            }

            return modified;
        }


        /// <summary>
        /// Gets number of SyncTypes by checking for SyncVar/Object attributes. This does not perform error checking.
        /// </summary>
        /// <param name="typeDef"></param>
        /// <returns></returns>
        internal uint GetSyncTypeCount(TypeDefinition typeDef)
        {
            uint count = 0;
            foreach (FieldDefinition fd in typeDef.Fields)
            {
                if (HasSyncTypeAttributeUnchecked(fd))
                    count++;
            }

            return count;
        }

        /// <summary>
        /// Replaces GetSets for methods which may use a SyncType.
        /// </summary>
        internal bool ReplaceGetSets(TypeDefinition typeDef, List<(SyncType, ProcessedSync)> allProcessedSyncs)
        {
            bool modified = false;

            List<MethodDefinition> modifiableMethods = GetModifiableMethods(typeDef);
            modified |= ReplaceGetSets(modifiableMethods, allProcessedSyncs);

            return modified;
        }

        /// <summary>
        /// Gets SyncType fieldDef is.
        /// </summary>
        /// <param name="fieldDef"></param>
        /// <param name="diagnostics"></param>
        /// <returns></returns>
        internal SyncType GetSyncType(FieldDefinition fieldDef, out CustomAttribute syncAttribute)
        {
            syncAttribute = null;
            if (fieldDef.Name.StartsWith(SYNCHANDLER_PREFIX))
                return SyncType.Unset;

            bool syncObject;
            syncAttribute = GetSyncTypeAttribute(fieldDef, out syncObject);
            /* If if attribute is null the code must progress
             * to throw errors when user creates a sync type
             * without using the attribute. */

            //Exit early if attribute is known and not a sync object.
            if (syncAttribute != null && !syncObject)
            {
                //Make sure syncvar attribute isnt on a sync object.
                if (GetObjectSyncType(syncAttribute) != SyncType.Unset)
                {
                    CodegenSession.LogError($"{fieldDef.Name} within {fieldDef.DeclaringType.Name} uses a [SyncVar] attribute but should be using [SyncObject].");
                    return SyncType.Unset;
                }
                else
                    return SyncType.Variable;
            }

            /* If here could be syncObject
             * or attribute might be null. */
            if (fieldDef.FieldType.Resolve().ImplementsInterface<ISyncType>())
            {
                return GetObjectSyncType(syncAttribute);
            }

            SyncType GetObjectSyncType(CustomAttribute sa)
            {
                //If attribute is null then throw error.
                if (sa == null)
                {
                    CodegenSession.LogError($"{fieldDef.Name} within {fieldDef.DeclaringType.Name} is a SyncType but [SyncObject] attribute was not found.");
                    return SyncType.Unset;
                }

                if (fieldDef.FieldType.Name == typeof(SyncList<>).Name)
                {
                    return SyncType.List;
                }
                else if (fieldDef.FieldType.Name == typeof(SyncDictionary<,>).Name)
                {
                    return SyncType.Dictionary;
                }
                //Custom types must also implement ICustomSync.
                else if (fieldDef.FieldType.Resolve().ImplementsInterface<ICustomSync>())
                {
                    return SyncType.Custom;
                }
                else
                {
                    return SyncType.Unset;
                }
            }

            //Fall through.
            if (syncAttribute != null)
                CodegenSession.LogError($"SyncObject attribute found on {fieldDef.Name} within {fieldDef.DeclaringType.Name} but type {fieldDef.FieldType.Name} does not inherit from SyncBase, or if a custom type does not implement ICustomSync.");

            return SyncType.Unset;
        }


        /// <summary>
        /// Tries to create a SyncList.
        /// </summary>
        private bool TryCreateCustom(uint syncTypeCount, List<(SyncType, ProcessedSync)> allProcessedSyncs, TypeDefinition typeDef, FieldDefinition originalFieldDef, CustomAttribute syncAttribute)
        {
            //Get the serialized type.
            MethodDefinition getSerialziedTypeMd = originalFieldDef.FieldType.Resolve().GetMethod(GETSERIALIZEDTYPE_METHOD_NAME);
            MethodReference getSerialziedTypeMr = CodegenSession.Module.ImportReference(getSerialziedTypeMd);
            Collection<Instruction> instructions = getSerialziedTypeMr.Resolve().Body.Instructions;

            bool canSerialize = false;
            TypeReference serializedDataTypeRef = null;
            /* If the user is returning null then
             * they are indicating a custom serializer does not
             * have to be implemented. */
            if (instructions.Count == 2 && instructions[0].OpCode == OpCodes.Ldnull && instructions[1].OpCode == OpCodes.Ret)
            {
                canSerialize = true;
            }
            //If not returning null then make a serializer for return type.
            else
            {
                foreach (Instruction item in instructions)
                {
                    //This token references the type.
                    if (item.OpCode == OpCodes.Ldtoken && item.Operand is TypeDefinition td)
                    {
                        serializedDataTypeRef = CodegenSession.Module.ImportReference(td);
                        canSerialize = CodegenSession.GeneralHelper.HasSerializerAndDeserializer(serializedDataTypeRef, true);
                    }
                }
            }

            //Wasn't able to determine serialized type, or create it.
            if (!canSerialize)
            {
                CodegenSession.LogError($"Custom SyncObject {originalFieldDef.Name} data type {serializedDataTypeRef.FullName} does not support serialization. Use a supported type or create a custom serializer.");
                return false;
            }

            bool result = InitializeCustom(syncTypeCount, typeDef, originalFieldDef, syncAttribute);
            if (result)
                allProcessedSyncs.Add((SyncType.Custom, null));
            return result;
        }


        /// <summary>
        /// Tries to create a SyncList.
        /// </summary>
        private bool TryCreateSyncList(uint syncTypeCount, List<(SyncType, ProcessedSync)> allProcessedSyncs, TypeDefinition typeDef, FieldDefinition originalFieldDef, CustomAttribute syncAttribute)
        {
            //Make sure type can be serialized.
            GenericInstanceType tmpGenerinstanceType = originalFieldDef.FieldType as GenericInstanceType;
            //this returns the correct data type, eg SyncList<int> would return int.
            TypeReference dataTypeRef = tmpGenerinstanceType.GenericArguments[0];

            bool canSerialize = CodegenSession.GeneralHelper.HasSerializerAndDeserializer(dataTypeRef, true);
            if (!canSerialize)
            {
                CodegenSession.LogError($"SyncObject {originalFieldDef.Name} data type {dataTypeRef.FullName} does not support serialization. Use a supported type or create a custom serializer.");
                return false;
            }

            bool result = InitializeSyncList(syncTypeCount, typeDef, originalFieldDef, syncAttribute);
            if (result)
                allProcessedSyncs.Add((SyncType.List, null));
            return result;
        }

        /// <summary>
        /// Tries to create a SyncDictionary.
        /// </summary>
        private bool TryCreateSyncDictionary(uint syncTypeCount, List<(SyncType, ProcessedSync)> allProcessedSyncs, TypeDefinition typeDef, FieldDefinition originalFieldDef, CustomAttribute syncAttribute)
        {
            //Make sure type can be serialized.
            GenericInstanceType tmpGenerinstanceType = originalFieldDef.FieldType as GenericInstanceType;
            //this returns the correct data type, eg SyncList<int> would return int.
            TypeReference keyTypeRef = tmpGenerinstanceType.GenericArguments[0];
            TypeReference valueTypeRef = tmpGenerinstanceType.GenericArguments[1];

            bool canSerialize;
            //Check key serializer.
            canSerialize = CodegenSession.GeneralHelper.HasSerializerAndDeserializer(keyTypeRef, true);
            if (!canSerialize)
            {
                CodegenSession.LogError($"SyncObject {originalFieldDef.Name} key type {keyTypeRef.FullName} does not support serialization. Use a supported type or create a custom serializer.");
                return false;
            }
            //Check value serializer.
            canSerialize = CodegenSession.GeneralHelper.HasSerializerAndDeserializer(valueTypeRef, true);
            if (!canSerialize)
            {
                CodegenSession.LogError($"SyncObject {originalFieldDef.Name} value type {valueTypeRef.FullName} does not support serialization. Use a supported type or create a custom serializer.");
                return false;
            }

            bool result = InitializeSyncDictionary(syncTypeCount, typeDef, originalFieldDef, syncAttribute);
            if (result)
                allProcessedSyncs.Add((SyncType.Dictionary, null));
            return result;
        }


        /// <summary>
        /// Tries to create a SyncVar.
        /// </summary>
        /// <param name="moduleDef"></param>
        /// <param name="syncCount"></param>
        /// <param name="typeDef"></param>
        /// <param name="diagnostics"></param>
        private bool TryCreateSyncVar(uint syncCount, List<(SyncType, ProcessedSync)> allProcessedSyncs, TypeDefinition typeDef, FieldDefinition fieldDef, CustomAttribute syncAttribute)
        {
            bool canSerialize = CodegenSession.GeneralHelper.HasSerializerAndDeserializer(fieldDef.FieldType, true);
            if (!canSerialize)
            {
                CodegenSession.LogError($"SyncVar {fieldDef.FullName} field type {fieldDef.FieldType.FullName} does not support serialization. Use a supported type or create a custom serializer.");
                return false;
            }

            MethodReference accessorSetValueMethodRef;
            MethodReference accessorGetValueMethodRef;
            bool created = CreateSyncVar(syncCount, typeDef, fieldDef, syncAttribute, out accessorSetValueMethodRef, out accessorGetValueMethodRef);
            if (created)
            {
                FieldReference originalFieldRef = CodegenSession.Module.ImportReference(fieldDef);
                allProcessedSyncs.Add((SyncType.Variable, new ProcessedSync(originalFieldRef, accessorSetValueMethodRef, accessorGetValueMethodRef)));
            }

            return created;
        }



        /// <summary>
        /// Returns if fieldDef has a SyncType attribute. No error checking is performed.
        /// </summary>
        /// <param name="fieldDef"></param>
        /// <returns></returns>
        private bool HasSyncTypeAttributeUnchecked(FieldDefinition fieldDef)
        {
            foreach (CustomAttribute customAttribute in fieldDef.CustomAttributes)
            {
                if (CodegenSession.AttributeHelper.IsSyncVarAttribute(customAttribute.AttributeType.FullName))
                    return true;
                else if (CodegenSession.AttributeHelper.IsSyncObjectAttribute(customAttribute.AttributeType.FullName))
                    return true;
            }

            return false;
        }


        /// <summary>
        /// Returns the syncvar attribute on a method, if one exist. Otherwise returns null.
        /// </summary>
        /// <param name="fieldDef"></param>
        /// <returns></returns>
        private CustomAttribute GetSyncTypeAttribute(FieldDefinition fieldDef, out bool syncObject)
        {
            CustomAttribute foundAttribute = null;
            //Becomes true if an error occurred during this process.
            bool error = false;
            syncObject = false;

            foreach (CustomAttribute customAttribute in fieldDef.CustomAttributes)
            {
                if (CodegenSession.AttributeHelper.IsSyncVarAttribute(customAttribute.AttributeType.FullName))
                    syncObject = false;
                else if (CodegenSession.AttributeHelper.IsSyncObjectAttribute(customAttribute.AttributeType.FullName))
                    syncObject = true;
                else
                    continue;

                //A syncvar attribute already exist.
                if (foundAttribute != null)
                {
                    CodegenSession.LogError($"{fieldDef.Name} cannot have multiple SyncType attributes.");
                    error = true;
                }
                //Static.
                if (fieldDef.IsStatic)
                {
                    CodegenSession.LogError($"{fieldDef.Name} SyncType cannot be static.");
                    error = true;
                }
                //Generic.
                if (fieldDef.FieldType.IsGenericParameter)
                {
                    CodegenSession.LogError($"{fieldDef.Name} SyncType cannot be be generic.");
                    error = true;
                }
                //SyncObject readonly check.
                if (syncObject && !fieldDef.Attributes.HasFlag(FieldAttributes.InitOnly))
                {
                    CodegenSession.LogError($"{fieldDef.Name} SyncObject must be readonly.");
                    error = true;
                }


                //If all checks passed.
                if (!error)
                    foundAttribute = customAttribute;
            }

            //If an error occurred then reset results.
            if (error)
                foundAttribute = null;

            return foundAttribute;
        }

        /// <summary>
        /// Creates a syncVar class for the user's syncvar.
        /// </summary>
        /// <param name="originalFieldDef"></param>
        /// <param name="syncTypeAttribute"></param>
        /// <returns></returns>
        private bool CreateSyncVar(uint syncCount, TypeDefinition typeDef, FieldDefinition originalFieldDef, CustomAttribute syncTypeAttribute, out MethodReference accessorSetValueMethodRef, out MethodReference accessorGetValueMethodRef)
        {
            accessorGetValueMethodRef = null;
            accessorSetValueMethodRef = null;
            MethodReference syncHandlerGetValueMethodRef;
            MethodReference syncHandlerSetValueMethodRef;
            MethodReference syncHandlerGetPreviousClientValueMethodRef;
            MethodReference syncHandlerReadMethodRef;
            FieldDefinition createdSyncHandlerFieldDef = CreateSyncVarFieldDefinition(typeDef, originalFieldDef,
                out syncHandlerGetValueMethodRef, out syncHandlerSetValueMethodRef, out syncHandlerGetPreviousClientValueMethodRef, out syncHandlerReadMethodRef);

            if (createdSyncHandlerFieldDef != null)
            {
                MethodReference hookMethodRef = GetSyncVarHookMethodReference(typeDef, originalFieldDef, syncTypeAttribute);

                //If accessor was made add it's methods to createdSyncTypeObjects.
                if (CreateSyncVarAccessor(originalFieldDef, createdSyncHandlerFieldDef, syncHandlerSetValueMethodRef,
                    syncHandlerGetPreviousClientValueMethodRef, out accessorGetValueMethodRef, out accessorSetValueMethodRef,
                    hookMethodRef) != null)
                {
                    _createdSyncTypeMethodDefinitions.Add(accessorGetValueMethodRef.Resolve());
                    _createdSyncTypeMethodDefinitions.Add(accessorSetValueMethodRef.Resolve());
                }

                InitializeSyncHandler(syncCount, createdSyncHandlerFieldDef, typeDef, originalFieldDef, syncTypeAttribute);

                MethodDefinition syncHandlerReadMethodDef = CreateSyncHandlerRead(typeDef, syncCount, originalFieldDef, accessorSetValueMethodRef);
                if (syncHandlerReadMethodDef != null)
                    _createdSyncTypeMethodDefinitions.Add(syncHandlerReadMethodDef);

                return true;
            }
            else
            {
                return false;
            }

        }

        /// <summary>
        /// Creates or gets a SyncType class for originalFieldDef.
        /// </summary>
        /// <param name="typeDef"></param>
        /// <param name="originalFieldDef"></param>
        /// <param name="syncVarAttribute"></param>
        /// <param name="createdSetValueMethodRef"></param>
        /// <param name="syncHandlerGetValueMethodRef"></param>
        /// <param name="diagnostics"></param>
        /// <returns></returns>  
        private FieldDefinition CreateSyncVarFieldDefinition(TypeDefinition typeDef, FieldDefinition originalFieldDef, out MethodReference syncHandlerGetValueMethodRef, out MethodReference syncHandlerSetValueMethodRef, out MethodReference syncHandlerGetPreviousClientValueMethodRef, out MethodReference syncHandlerReadMethodRef)
        {
            //Get class stub used.
            TypeDefinition syncStubTypeDef = CodegenSession.SyncHandlerGenerator.GetOrCreateSyncHandler(originalFieldDef.FieldType, out syncHandlerGetValueMethodRef, out syncHandlerSetValueMethodRef, out syncHandlerGetPreviousClientValueMethodRef, out syncHandlerReadMethodRef);
            if (syncStubTypeDef == null)
                return null;

            FieldDefinition createdFieldDef = new FieldDefinition($"{SYNCHANDLER_PREFIX}{originalFieldDef.Name}", originalFieldDef.Attributes, syncStubTypeDef);
            if (createdFieldDef == null)
            {
                CodegenSession.LogError($"Could not create field for Sync type {originalFieldDef.FieldType.FullName}, name of {originalFieldDef.Name}.");
                return null;
            }

            typeDef.Fields.Add(createdFieldDef);
            return createdFieldDef;
        }

        /// <summary>
        /// Validates and gets the hook MethodReference for a SyncVar if available.
        /// </summary>
        /// <param name="moduleDef"></param>
        /// <param name="typeDef"></param>
        /// <param name="attribute"></param>
        /// <returns></returns>
        private MethodReference GetSyncVarHookMethodReference(TypeDefinition typeDef, FieldDefinition originalFieldDef, CustomAttribute attribute)
        {
            string hook = attribute.GetField("OnChange", string.Empty);
            //No hook is specified.
            if (string.IsNullOrEmpty(hook))
                return null;

            MethodDefinition md = typeDef.GetMethod(hook);

            if (md != null)
            {
                string incorrectParametersMsg = $"OnChange method for {originalFieldDef.FullName} must contain 3 parameters in order of {originalFieldDef.FieldType.Name} oldValue, {originalFieldDef.FieldType.Name} newValue, {CodegenSession.Module.TypeSystem.Boolean} asServer.";
                //Not correct number of parameters.
                if (md.Parameters.Count != 3)
                {
                    CodegenSession.LogError(incorrectParametersMsg);
                    return null;
                }
                //One or more parameters are wrong.
                //Not correct number of parameters.
                if (md.Parameters[0].ParameterType != originalFieldDef.FieldType ||
                    md.Parameters[1].ParameterType != originalFieldDef.FieldType ||
                    md.Parameters[2].ParameterType != CodegenSession.Module.TypeSystem.Boolean)
                {
                    CodegenSession.LogError(incorrectParametersMsg);
                    return null;
                }

                //If here everything checks out, return a method reference to hook method.
                return CodegenSession.Module.ImportReference(md);
            }
            //Hook specified but no method found.
            else
            {
                CodegenSession.LogError($"Could not find method name {hook} for SyncType {originalFieldDef.FullName}.");
                return null;
            }
        }

        /// <summary>
        /// Creates accessor for a SyncVar.
        /// </summary>
        /// <param name="moduleDef"></param>
        /// <param name="originalFieldDef"></param>
        /// <param name="syncHandlerFieldDef"></param>
        /// <param name="syncHandlerSetValueMethodRef"></param>
        /// <param name="accessorGetValueMethodRef"></param>
        /// <param name="accessorSetValueMethodRef"></param>
        /// <param name="hookMethodRef"></param>
        /// <param name="diagnostics"></param>
        /// <returns></returns>
        private FieldDefinition CreateSyncVarAccessor(FieldDefinition originalFieldDef, FieldDefinition syncHandlerFieldDef, MethodReference syncHandlerSetValueMethodRef, MethodReference syncHandlerGetPreviousClientValueMethodRef, out MethodReference accessorGetValueMethodRef, out MethodReference accessorSetValueMethodRef, MethodReference hookMethodRef)
        {
            /* Create and add property definition. */
            PropertyDefinition createdPropertyDef = new PropertyDefinition($"SyncAccessor_{originalFieldDef.Name}", PropertyAttributes.None, originalFieldDef.FieldType);
            createdPropertyDef.DeclaringType = originalFieldDef.DeclaringType;
            //add the methods and property to the type.
            originalFieldDef.DeclaringType.Properties.Add(createdPropertyDef);

            ILProcessor processor;

            /* Get method for property definition. */
            MethodDefinition createdGetMethodDef = originalFieldDef.DeclaringType.AddMethod($"{ACCESSOR_PREFIX}get_value_{originalFieldDef.Name}", MethodAttributes.Public |
                    MethodAttributes.SpecialName | MethodAttributes.HideBySig,
                    originalFieldDef.FieldType);
            createdGetMethodDef.SemanticsAttributes = MethodSemanticsAttributes.Getter;

            processor = createdGetMethodDef.Body.GetILProcessor();
            processor.Emit(OpCodes.Ldarg_0); //this.
            processor.Emit(OpCodes.Ldfld, originalFieldDef);
            processor.Emit(OpCodes.Ret);
            accessorGetValueMethodRef = CodegenSession.Module.ImportReference(createdGetMethodDef);
            //Add getter to properties.
            createdPropertyDef.GetMethod = createdGetMethodDef;

            /* Set method. */
            //Create the set method
            MethodDefinition createdSetMethodDef = originalFieldDef.DeclaringType.AddMethod($"{ACCESSOR_PREFIX}set_value_{originalFieldDef.Name}", MethodAttributes.Public |
                    MethodAttributes.SpecialName |
                    MethodAttributes.HideBySig);
            createdSetMethodDef.SemanticsAttributes = MethodSemanticsAttributes.Setter;

            ParameterDefinition valueParameterDef = CodegenSession.GeneralHelper.CreateParameter(createdSetMethodDef, originalFieldDef.FieldType, "value");
            ParameterDefinition asServerParameterDef = CodegenSession.GeneralHelper.CreateParameter(createdSetMethodDef, typeof(bool), "asServer");
            processor = createdSetMethodDef.Body.GetILProcessor();

            //Create previous value for hook. Only store it if hook is available.
            VariableDefinition prevValueVariableDef = CodegenSession.GeneralHelper.CreateVariable(createdSetMethodDef, valueParameterDef.ParameterType);
            if (hookMethodRef != null)
            {
                //If (asServer) previousValue = this.originalField.
                Instruction notAsServerPrevValueInst = processor.Create(OpCodes.Nop);
                Instruction notAsServerEndIfInst = processor.Create(OpCodes.Nop);
                processor.Emit(OpCodes.Ldarg, asServerParameterDef);
                processor.Emit(OpCodes.Brfalse, notAsServerPrevValueInst);
                processor.Emit(OpCodes.Ldarg_0); //this.
                processor.Emit(OpCodes.Ldfld, originalFieldDef);
                processor.Emit(OpCodes.Stloc, prevValueVariableDef);
                processor.Emit(OpCodes.Br, notAsServerEndIfInst);
                processor.Append(notAsServerPrevValueInst);

                //(else) -> If (!asServer)
                //If (!IsServer) previousValue = this.originalField.
                Instruction isServerInst = processor.Create(OpCodes.Nop);
                processor.Emit(OpCodes.Ldarg_0);
                processor.Emit(OpCodes.Call, CodegenSession.GeneralHelper.IsServer_MethodRef);
                processor.Emit(OpCodes.Brtrue, isServerInst);
                processor.Emit(OpCodes.Ldarg_0); //this.
                processor.Emit(OpCodes.Ldfld, originalFieldDef);
                processor.Emit(OpCodes.Stloc, prevValueVariableDef);
                processor.Emit(OpCodes.Br, notAsServerEndIfInst);
                //else else (isServer) previousValue = syncHnadler.GetPreviousClientValue().
                processor.Append(isServerInst);
                processor.Emit(OpCodes.Ldarg_0); //this.
                processor.Emit(OpCodes.Ldfld, syncHandlerFieldDef);
                processor.Emit(OpCodes.Call, syncHandlerGetPreviousClientValueMethodRef);
                processor.Emit(OpCodes.Stloc, prevValueVariableDef);
                processor.Append(notAsServerEndIfInst);
            }

            //if (!syncHandler_XXXX.SetValue(....) return
            Instruction endSetInst = processor.Create(OpCodes.Nop);
            processor.Emit(OpCodes.Ldarg_0); //this.
            processor.Emit(OpCodes.Ldfld, syncHandlerFieldDef);
            processor.Emit(OpCodes.Ldarg, valueParameterDef);
            processor.Emit(OpCodes.Ldarg, asServerParameterDef);
            processor.Emit(OpCodes.Callvirt, syncHandlerSetValueMethodRef);
            processor.Emit(OpCodes.Brfalse, endSetInst);

            //Assign to new value. _originalField = value;
            processor.Emit(OpCodes.Ldarg_0); //this.
            processor.Emit(OpCodes.Ldarg, valueParameterDef);
            processor.Emit(OpCodes.Stfld, originalFieldDef);

            //If hook exist then call it with value changes and asServer.
            if (hookMethodRef != null)
            {
                processor.Emit(OpCodes.Ldarg_0); //this.
                processor.Emit(OpCodes.Ldloc, prevValueVariableDef);
                processor.Emit(OpCodes.Ldarg_0); //this.
                processor.Emit(OpCodes.Ldfld, originalFieldDef);
                processor.Emit(OpCodes.Ldarg, asServerParameterDef);
                processor.Emit(OpCodes.Call, hookMethodRef);
            }

            processor.Append(endSetInst);
            processor.Emit(OpCodes.Ret);
            accessorSetValueMethodRef = CodegenSession.Module.ImportReference(createdSetMethodDef);
            //Add setter to properties.
            createdPropertyDef.SetMethod = createdSetMethodDef;

            return originalFieldDef;
        }

        /// <summary>
        /// Sets methods used from SyncBase for typeDef.
        /// </summary>
        /// <returns></returns>
        private bool SetSyncBaseMethods(TypeDefinition typeDef, out MethodReference setSyncIndexMr, out MethodReference initializeInstanceMr)
        {
            setSyncIndexMr = null;
            initializeInstanceMr = null;
            //Find the SyncBase class.
            TypeDefinition syncBaseTd = null;
            TypeDefinition copyTd = typeDef;
            do
            {
                if (copyTd.Name == nameof(SyncBase))
                {
                    syncBaseTd = copyTd;
                    break;
                }
                copyTd = TypeDefinitionExtensions.GetNextBaseClass(copyTd);
            } while (copyTd != null);

            //If SyncBase isn't found.
            if (syncBaseTd == null)
            {
                CodegenSession.LogError($"Could not find SyncBase within type {typeDef.FullName}.");
                return false;
            }
            else
            {
                MethodDefinition tmpMd;
                //InitializeInstance.
                tmpMd = syncBaseTd.GetMethod(INITIALIZEINSTANCE_METHOD_NAME);
                initializeInstanceMr = CodegenSession.Module.ImportReference(tmpMd);
                //SetSyncIndex.
                tmpMd = syncBaseTd.GetMethod(SETSYNCINDEX_METHOD_NAME);
                setSyncIndexMr = CodegenSession.Module.ImportReference(tmpMd);
                return true;
            }

        }

        /// <summary>
        /// Initializes the SyncHandler FieldDefinition.
        /// </summary>
        internal bool InitializeCustom(uint syncCount, TypeDefinition typeDef, FieldDefinition originalFieldDef, CustomAttribute attribute)
        {
            float sendRate = 0.1f;
            WritePermission writePermissions = WritePermission.ServerOnly;
            ReadPermission readPermissions = ReadPermission.Observers;
            Channel channel = Channel.Reliable;
            //If attribute isn't null then override values.
            if (attribute != null)
            {
                sendRate = attribute.GetField("SendRate", 0.1f);
                writePermissions = WritePermission.ServerOnly;
                readPermissions = attribute.GetField("ReadPermissions", ReadPermission.Observers);
                channel = Channel.Reliable; //attribute.GetField("Channel", Channel.Reliable);
            }

            //Set needed methods from syncbase.
            MethodReference setSyncIndexMr;
            MethodReference initializeInstanceMr;
            if (!SetSyncBaseMethods(originalFieldDef.FieldType.Resolve(), out setSyncIndexMr, out initializeInstanceMr))
                return false;

            MethodDefinition injectionMethodDef;
            ILProcessor processor;
            List<Instruction> instructions = new List<Instruction>();

            /* Initialize with attribute settings. */
            injectionMethodDef = typeDef.GetMethod(NetworkBehaviourProcessor.NETWORKINITIALIZE_EARLY_INTERNAL_NAME);
            processor = injectionMethodDef.Body.GetILProcessor();
            //

            instructions.Add(processor.Create(OpCodes.Ldarg_0)); //this.
            instructions.Add(processor.Create(OpCodes.Ldfld, originalFieldDef));
            instructions.Add(processor.Create(OpCodes.Ldc_I4, (int)writePermissions));
            instructions.Add(processor.Create(OpCodes.Ldc_I4, (int)readPermissions));
            instructions.Add(processor.Create(OpCodes.Ldc_R4, sendRate));
            instructions.Add(processor.Create(OpCodes.Ldc_I4, (int)channel));
            instructions.Add(processor.Create(OpCodes.Ldc_I4_1)); //true for syncObject.
            instructions.Add(processor.Create(OpCodes.Call, initializeInstanceMr));
            processor.InsertFirst(instructions);

            instructions.Clear();
            /* Set NetworkBehaviour and SyncIndex to use. */
            injectionMethodDef = typeDef.GetMethod(NetworkBehaviourProcessor.NETWORKINITIALIZE_LATE_INTERNAL_NAME);
            processor = injectionMethodDef.Body.GetILProcessor();
            //
            uint hash = (uint)syncCount;
            //uint hash = originalFieldDef.FullName.GetStableHash32();
            instructions.Add(processor.Create(OpCodes.Ldarg_0)); //this.
            instructions.Add(processor.Create(OpCodes.Ldfld, originalFieldDef));
            instructions.Add(processor.Create(OpCodes.Ldarg_0)); //this again for NetworkBehaviour.
            instructions.Add(processor.Create(OpCodes.Ldc_I4, (int)hash));
            instructions.Add(processor.Create(OpCodes.Callvirt, setSyncIndexMr));

            processor.InsertLast(instructions);

            return true;
        }




        /// <summary>
        /// Initializes the SyncHandler FieldDefinition.
        /// </summary>
        internal bool InitializeSyncList(uint syncCount, TypeDefinition typeDef, FieldDefinition originalFieldDef, CustomAttribute attribute)
        {
            float sendRate = 0.1f;
            WritePermission writePermissions = WritePermission.ServerOnly;
            ReadPermission readPermissions = ReadPermission.Observers;
            Channel channel = Channel.Reliable;
            //If attribute isn't null then override values.
            if (attribute != null)
            {
                sendRate = attribute.GetField("SendRate", 0.1f);
                writePermissions = WritePermission.ServerOnly;
                readPermissions = attribute.GetField("ReadPermissions", ReadPermission.Observers);
                channel = Channel.Reliable; //attribute.GetField("Channel", Channel.Reliable);
            }

            //This import shouldn't be needed but cecil is stingy so rather be safe than sorry.
            CodegenSession.Module.ImportReference(originalFieldDef);

            //Set needed methods from syncbase.
            MethodReference setSyncIndexMr;
            MethodReference initializeInstanceMr;
            if (!SetSyncBaseMethods(originalFieldDef.FieldType.Resolve(), out setSyncIndexMr, out initializeInstanceMr))
                return false;

            MethodDefinition injectionMethodDef;
            ILProcessor processor;
            List<Instruction> instructions = new List<Instruction>();

            /* Initialize with attribute settings. */
            injectionMethodDef = typeDef.GetMethod(NetworkBehaviourProcessor.NETWORKINITIALIZE_EARLY_INTERNAL_NAME);
            processor = injectionMethodDef.Body.GetILProcessor();

            //InitializeInstance.
            instructions.Add(processor.Create(OpCodes.Ldarg_0)); //this.
            instructions.Add(processor.Create(OpCodes.Ldfld, originalFieldDef));
            instructions.Add(processor.Create(OpCodes.Ldc_I4, (int)writePermissions));
            instructions.Add(processor.Create(OpCodes.Ldc_I4, (int)readPermissions));
            instructions.Add(processor.Create(OpCodes.Ldc_R4, sendRate));
            instructions.Add(processor.Create(OpCodes.Ldc_I4, (int)channel));
            instructions.Add(processor.Create(OpCodes.Ldc_I4_1)); //true for syncObject.
            instructions.Add(processor.Create(OpCodes.Call, initializeInstanceMr));
            processor.InsertFirst(instructions);

            instructions.Clear();
            /* Set NetworkBehaviour and SyncIndex to use. */
            injectionMethodDef = typeDef.GetMethod(NetworkBehaviourProcessor.NETWORKINITIALIZE_LATE_INTERNAL_NAME);
            processor = injectionMethodDef.Body.GetILProcessor();

            //SetSyncIndex.
            uint hash = (uint)syncCount;
            //uint hash = originalFieldDef.FullName.GetStableHash32();
            instructions.Add(processor.Create(OpCodes.Ldarg_0)); //this.
            instructions.Add(processor.Create(OpCodes.Ldfld, originalFieldDef));
            instructions.Add(processor.Create(OpCodes.Ldarg_0)); //this again for NetworkBehaviour.
            instructions.Add(processor.Create(OpCodes.Ldc_I4, (int)hash));
            instructions.Add(processor.Create(OpCodes.Callvirt, setSyncIndexMr));

            processor.InsertLast(instructions);

            return true;
        }



        /// <summary>
        /// Initializes the SyncHandler FieldDefinition.
        /// </summary>
        /// <param name="typeDef"></param>
        /// <param name="originalFieldDef"></param>
        /// <param name="attribute"></param>
        /// <param name="diagnostics"></param>
        internal bool InitializeSyncDictionary(uint syncCount, TypeDefinition typeDef, FieldDefinition originalFieldDef, CustomAttribute attribute)
        {
            float sendRate = 0.1f;
            WritePermission writePermissions = WritePermission.ServerOnly;
            ReadPermission readPermissions = ReadPermission.Observers;
            Channel channel = Channel.Reliable;
            //If attribute isn't null then override values.
            if (attribute != null)
            {
                sendRate = attribute.GetField("SendRate", 0.1f);
                writePermissions = WritePermission.ServerOnly;
                readPermissions = attribute.GetField("ReadPermissions", ReadPermission.Observers);
                channel = Channel.Reliable; //attribute.GetField("Channel", Channel.Reliable);
            }

            //This import shouldn't be needed but cecil is stingy so rather be safe than sorry.
            CodegenSession.Module.ImportReference(originalFieldDef);

            //Set needed methods from syncbase.
            MethodReference setSyncIndexMr;
            MethodReference initializeInstanceMr;
            if (!SetSyncBaseMethods(originalFieldDef.FieldType.Resolve(), out setSyncIndexMr, out initializeInstanceMr))
                return false;

            MethodDefinition injectionMethodDef = typeDef.GetMethod(NetworkBehaviourProcessor.NETWORKINITIALIZE_EARLY_INTERNAL_NAME);
            ILProcessor processor = injectionMethodDef.Body.GetILProcessor();

            List<Instruction> instructions = new List<Instruction>();

            /* Initialize with attribute settings. */
            instructions.Add(processor.Create(OpCodes.Ldarg_0)); //this.
            instructions.Add(processor.Create(OpCodes.Ldfld, originalFieldDef));
            instructions.Add(processor.Create(OpCodes.Ldc_I4, (int)writePermissions));
            instructions.Add(processor.Create(OpCodes.Ldc_I4, (int)readPermissions));
            instructions.Add(processor.Create(OpCodes.Ldc_R4, sendRate));
            instructions.Add(processor.Create(OpCodes.Ldc_I4, (int)channel));
            instructions.Add(processor.Create(OpCodes.Ldc_I4_1)); //true for syncObject.
            instructions.Add(processor.Create(OpCodes.Call, initializeInstanceMr));

            //Set NetworkBehaviour and SyncIndex to use.
            uint hash = (uint)syncCount;
            //uint hash = originalFieldDef.FullName.GetStableHash32();
            instructions.Add(processor.Create(OpCodes.Ldarg_0)); //this.
            instructions.Add(processor.Create(OpCodes.Ldfld, originalFieldDef));
            instructions.Add(processor.Create(OpCodes.Ldarg_0)); //this again for NetworkBehaviour.
            instructions.Add(processor.Create(OpCodes.Ldc_I4, (int)hash));
            instructions.Add(processor.Create(OpCodes.Callvirt, setSyncIndexMr));

            processor.InsertFirst(instructions);

            return true;
        }


        /// <summary>
        /// Initializes the SyncHandler FieldDefinition.
        /// </summary>
        /// <param name="createdFieldDef"></param>
        /// <param name="typeDef"></param>
        /// <param name="originalFieldDef"></param>
        /// <param name="attribute"></param>
        /// <param name="diagnostics"></param>
        internal void InitializeSyncHandler(uint syncCount, FieldDefinition createdFieldDef, TypeDefinition typeDef, FieldDefinition originalFieldDef, CustomAttribute attribute)
        {
            //Get all possible attributes.
            float sendRate = attribute.GetField("SendRate", 0.1f);
            WritePermission writePermissions = WritePermission.ServerOnly;
            ReadPermission readPermissions = attribute.GetField("ReadPermissions", ReadPermission.Observers);
            Channel channel = attribute.GetField("Channel", Channel.Reliable);

            MethodReference syncHandlerConstructorMethodRef;
            //Get constructor for syncType.
            if (CodegenSession.SyncHandlerGenerator.CreatedSyncTypes.TryGetValue(originalFieldDef.FieldType.Resolve(), out CreatedSyncType cst))
            {
                syncHandlerConstructorMethodRef = cst.ConstructorMethodReference;
            }
            else
            {
                CodegenSession.LogError($"Created constructor not found for SyncType {originalFieldDef.DeclaringType}.");
                return;
            }

            MethodDefinition injectionMethodDef = typeDef.GetMethod(NetworkBehaviourProcessor.NETWORKINITIALIZE_EARLY_INTERNAL_NAME);
            ILProcessor processor = injectionMethodDef.Body.GetILProcessor();

            List<Instruction> instructions = new List<Instruction>();
            //Initialize fieldDef with values from attribute.
            instructions.Add(processor.Create(OpCodes.Ldarg_0)); //this.
            instructions.Add(processor.Create(OpCodes.Ldc_I4, (int)writePermissions));
            instructions.Add(processor.Create(OpCodes.Ldc_I4, (int)readPermissions));
            instructions.Add(processor.Create(OpCodes.Ldc_R4, sendRate));
            instructions.Add(processor.Create(OpCodes.Ldc_I4, (int)channel));
            instructions.Add(processor.Create(OpCodes.Ldarg_0)); //this.
            instructions.Add(processor.Create(OpCodes.Ldfld, originalFieldDef)); //initial value.
            instructions.Add(processor.Create(OpCodes.Newobj, syncHandlerConstructorMethodRef));
            instructions.Add(processor.Create(OpCodes.Stfld, createdFieldDef));

            uint hash = (uint)syncCount;
            //uint hash = originalFieldDef.FullName.GetStableHash32();
            //Set NB and SyncIndex to SyncHandler.
            instructions.Add(processor.Create(OpCodes.Ldarg_0)); //this.
            instructions.Add(processor.Create(OpCodes.Ldfld, createdFieldDef));
            instructions.Add(processor.Create(OpCodes.Ldarg_0)); //this again for NetworkBehaviour.
            instructions.Add(processor.Create(OpCodes.Ldc_I4, (int)hash));
            instructions.Add(processor.Create(OpCodes.Callvirt, CodegenSession.SyncHandlerGenerator.SyncBase_SetSyncIndex_MethodRef));

            processor.InsertFirst(instructions);
        }

        /// <summary>
        /// Replaces GetSets for methods which may use a SyncType.
        /// </summary>
        /// <param name="modifiableMethods"></param>
        /// <param name="processedSyncs"></param>
        internal bool ReplaceGetSets(List<MethodDefinition> modifiableMethods, List<(SyncType, ProcessedSync)> processedSyncs)
        {
            //Build processed syncs into dictionary for quicker loookups.
            Dictionary<FieldReference, List<ProcessedSync>> processedLookup = new Dictionary<FieldReference, List<ProcessedSync>>();
            foreach ((SyncType st, ProcessedSync ps) in processedSyncs)
            {
                if (st != SyncType.Variable)
                    continue;

                List<ProcessedSync> result;
                if (!processedLookup.TryGetValue(ps.OriginalFieldReference, out result))
                {
                    result = new List<ProcessedSync>() { ps };
                    processedLookup.Add(ps.OriginalFieldReference, result);
                }

                result.Add(ps);
            }

            bool modified = false;
            foreach (MethodDefinition methodDef in modifiableMethods)
                modified |= ReplaceGetSet(methodDef, processedLookup);

            return modified;
        }

        /// <summary>
        /// Replaces GetSets for a method which may use a SyncType.
        /// </summary>
        /// <param name="methodDef"></param>
        /// <param name="processedLookup"></param>
        private bool ReplaceGetSet(MethodDefinition methodDef, Dictionary<FieldReference, List<ProcessedSync>> processedLookup)
        {
            if (methodDef == null)
            {
                CodegenSession.LogError($"An object expecting value was null. Please try saving your script again.");
                return false;
            }
            if (methodDef.IsAbstract)
                return false;
            if (_createdSyncTypeMethodDefinitions.Contains(methodDef))
                return false;
            if (methodDef.Name == NetworkBehaviourProcessor.NETWORKINITIALIZE_EARLY_INTERNAL_NAME)
                return false;

            bool modified = false;
            for (int i = 0; i < methodDef.Body.Instructions.Count; i++)
            {
                Instruction inst = methodDef.Body.Instructions[i];

                /* Loading a field. (Getter) */
                if (inst.OpCode == OpCodes.Ldfld && inst.Operand is FieldReference opFieldld)
                {
                    FieldReference resolvedOpField = opFieldld.Resolve();
                    if (resolvedOpField == null)
                        resolvedOpField = opFieldld.DeclaringType.Resolve().GetField(opFieldld.Name);

                    modified |= ProcessGetField(methodDef, i, resolvedOpField, processedLookup);
                }
                /* Load address, reference field. */
                else if (inst.OpCode == OpCodes.Ldflda && inst.Operand is FieldReference opFieldlda)
                {
                    FieldReference resolvedOpField = opFieldlda.Resolve();
                    if (resolvedOpField == null)
                        resolvedOpField = opFieldlda.DeclaringType.Resolve().GetField(opFieldlda.Name);

                    modified |= ProcessAddressField(methodDef, i, resolvedOpField, processedLookup);
                }
                /* Setting a field. (Setter) */
                else if (inst.OpCode == OpCodes.Stfld && inst.Operand is FieldReference opFieldst)
                {
                    FieldReference resolvedOpField = opFieldst.Resolve();
                    if (resolvedOpField == null)
                        resolvedOpField = opFieldst.DeclaringType.Resolve().GetField(opFieldst.Name);

                    modified |= ProcessSetField(methodDef, i, resolvedOpField, processedLookup);
                }

            }

            return modified;
        }

        /// <summary>
        /// Replaces Gets for a method which may use a SyncType.
        /// </summary>
        /// <param name="methodDef"></param>
        /// <param name="instructionIndex"></param>
        /// <param name="resolvedOpField"></param>
        /// <param name="processedLookup"></param>
        private bool ProcessGetField(MethodDefinition methodDef, int instructionIndex, FieldReference resolvedOpField, Dictionary<FieldReference, List<ProcessedSync>> processedLookup)
        {
            Instruction inst = methodDef.Body.Instructions[instructionIndex];

            //If was a replaced field.
            if (processedLookup.TryGetValue(resolvedOpField, out List<ProcessedSync> psLst))
            {
                ProcessedSync ps = GetProcessedSync(resolvedOpField, psLst);
                if (ps == null)
                    return false;
                //Don't modify the accessor method.
                if (ps.GetMethodReference.Resolve() == methodDef)
                    return false;

                //Generic type.
                if (resolvedOpField.DeclaringType.IsGenericInstance || resolvedOpField.DeclaringType.HasGenericParameters)
                {
                    FieldReference newField = inst.Operand as FieldReference;
                    GenericInstanceType genericType = (GenericInstanceType)newField.DeclaringType;
                    inst.OpCode = OpCodes.Callvirt;
                    inst.Operand = ps.GetMethodReference.MakeHostInstanceGeneric(genericType);
                }
                //Strong type.
                else
                {
                    inst.OpCode = OpCodes.Call;
                    inst.Operand = ps.GetMethodReference;
                }

                return true;
            }
            else
            {
                return false;
            }
        }


        /// <summary>
        /// Replaces Sets for a method which may use a SyncType.
        /// </summary>
        /// <param name="methodDef"></param>
        /// <param name="instructionIndex"></param>
        /// <param name="resolvedOpField"></param>
        /// <param name="processedLookup"></param>
        private bool ProcessSetField(MethodDefinition methodDef, int instructionIndex, FieldReference resolvedOpField, Dictionary<FieldReference, List<ProcessedSync>> processedLookup)
        {
            Instruction inst = methodDef.Body.Instructions[instructionIndex];

            //Find any instructions that are jmp/breaking to the one we are modifying.            
            HashSet<Instruction> brInstructions = new HashSet<Instruction>();
            foreach (Instruction item in methodDef.Body.Instructions)
            {
                bool canJmp = (item.OpCode == OpCodes.Br || item.OpCode == OpCodes.Brfalse || item.OpCode == OpCodes.Brfalse_S || item.OpCode == OpCodes.Brtrue || item.OpCode == OpCodes.Brtrue_S || item.OpCode == OpCodes.Br_S);
                if (!canJmp)
                    continue;
                if (item.Operand == null)
                    continue;

                if (item.Operand is Instruction jmpInst && jmpInst == inst)
                    brInstructions.Add(item);
            }

            //If was a replaced field.
            if (processedLookup.TryGetValue(resolvedOpField, out List<ProcessedSync> psLst))
            {
                ProcessedSync ps = GetProcessedSync(resolvedOpField, psLst);
                if (ps == null)
                    return false;
                //Don't modify the accessor method.
                if (ps.SetMethodReference.Resolve() == methodDef)
                    return false;

                ILProcessor processor = methodDef.Body.GetILProcessor();
                //Generic type.
                if (resolvedOpField.DeclaringType.IsGenericInstance || resolvedOpField.DeclaringType.HasGenericParameters)
                {
                    //Pass in true for as server.
                    Instruction boolTrueInst = processor.Create(OpCodes.Ldc_I4_1);
                    methodDef.Body.Instructions.Insert(instructionIndex, boolTrueInst);

                    var newField = inst.Operand as FieldReference;
                    var genericType = (GenericInstanceType)newField.DeclaringType;
                    inst.OpCode = OpCodes.Callvirt;
                    inst.Operand = ps.SetMethodReference.MakeHostInstanceGeneric(genericType);
                }
                //Strong typed.
                else
                {
                    //Pass in true for as server.
                    Instruction boolTrueInst = processor.Create(OpCodes.Ldc_I4_1);
                    methodDef.Body.Instructions.Insert(instructionIndex, boolTrueInst);

                    inst.OpCode = OpCodes.Call;
                    inst.Operand = ps.SetMethodReference;
                }

                /* If any instructions are still pointing
                 * to modified value then they need to be
                 * redirected to the instruction right above it.
                 * This is because the boolTrueInst, to indicate
                 * value is being set as server. */
                foreach (Instruction item in brInstructions)
                {
                    if (item.Operand is Instruction jmpInst && jmpInst == inst)
                    {
                        //Use the same index that was passed in, which is now one before modified instruction.
                        Instruction newInst = methodDef.Body.Instructions[instructionIndex];
                        item.Operand = newInst;
                    }
                }

                return true;
            }
            else
            {
                return false;
            }
        }

        /// <summary>
        /// Replaces address Sets for a method which may use a SyncType.
        /// </summary>
        /// <param name="methodDef"></param>
        /// <param name="instructionIndex"></param>
        /// <param name="resolvedOpField"></param>
        /// <param name="processedLookup"></param>
        private bool ProcessAddressField(MethodDefinition methodDef, int instructionIndex, FieldReference resolvedOpField, Dictionary<FieldReference, List<ProcessedSync>> processedLookup)
        {
            Instruction inst = methodDef.Body.Instructions[instructionIndex];
            //Check if next instruction is Initobj, which would be setting a new instance.
            Instruction nextInstr = inst.Next;
            if (nextInstr.OpCode != OpCodes.Initobj)
                return false;

            //If was a replaced field.
            if (processedLookup.TryGetValue(resolvedOpField, out List<ProcessedSync> psLst))
            {
                ProcessedSync ps = GetProcessedSync(resolvedOpField, psLst);
                if (ps == null)
                    return false;
                //Don't modify the accessor method.
                if (ps.GetMethodReference.Resolve() == methodDef || ps.SetMethodReference.Resolve() == methodDef)
                    return false;

                ILProcessor processor = methodDef.Body.GetILProcessor();

                VariableDefinition tmpVariableDef = CodegenSession.GeneralHelper.CreateVariable(methodDef, resolvedOpField.FieldType);
                processor.InsertBefore(inst, processor.Create(OpCodes.Ldloca, tmpVariableDef));
                processor.InsertBefore(inst, processor.Create(OpCodes.Initobj, resolvedOpField.FieldType));
                processor.InsertBefore(inst, processor.Create(OpCodes.Ldloc, tmpVariableDef));
                Instruction newInstr = processor.Create(OpCodes.Call, ps.SetMethodReference);
                processor.InsertBefore(inst, newInstr);

                /* Pass in true for as server.
                 * The instruction index is 3 past ld. */
                Instruction boolTrueInst = processor.Create(OpCodes.Ldc_I4_1);
                methodDef.Body.Instructions.Insert(instructionIndex + 3, boolTrueInst);

                processor.Remove(inst);
                processor.Remove(nextInstr);

                return true;
            }
            else
            {
                return false;
            }

            ///* Next instruction isn't initializing an object. Check if user
            // * might be trying to call a method with ref/out of a synctype. */
            //else
            //{
            //    /* There's many aspects where this might trigger the warning even
            //     * if the sync type isn't being passed in using ref/out. For now
            //     * keep this commented, might revisit it later. */
            //    //for (int i = (instructionIndex + 1); i < methodDef.Body.Instructions.Count; i++)
            //    //{
            //    //    inst = methodDef.Body.Instructions[i];
            //    //    /* Setting something. This is okay, and
            //    //     * is handled elsewhere. */
            //    //    if (inst.OpCode == OpCodes.Stfld || inst.OpCode == OpCodes.Stloc)
            //    //        return;
            //    //    /* Calling something, this would suggest a ref/out keyword. */
            //    //    if (inst.OpCode == OpCodes.Call || inst.opcode == OpCodes.Callvirt)
            //    //    {
            //    //        //If was a replaced field.
            //    //        if (processedLookup.TryGetValue(resolvedOpField, out List<ProcessedSync> psLst))
            //    //        {
            //    //            ProcessedSync ps = GetProcessedSync(resolvedOpField, psLst);
            //    //            if (ps == null)
            //    //                return;

            //    //            CodegenSession.Diagnostics.AddWarning($"SyncType variable '{ps.OriginalFieldReference.Name}' within method '{methodDef.Name}' in class '{methodDef.DeclaringType.Name}' may be calling another method using the ref or out keyword. This is currently not supported. If this message is a mistake please file a bug report.");
            //    //            return;
            //    //        }
            //    //    }
            //    //}
            //}
        }

        /// <summary>
        /// Calls ReadSyncVar going up the hierarchy.
        /// </summary>
        /// <param name="firstTypeDef"></param>
        internal void CallBaseReadSyncVar(TypeDefinition firstTypeDef)
        {
            //TypeDef which needs to make the base call.
            MethodDefinition callerMd = null;
            TypeDefinition copyTd = firstTypeDef;
            do
            {
                MethodDefinition readMd;

                readMd = copyTd.GetMethod(CodegenSession.ObjectHelper.NetworkBehaviour_ReadSyncVar_MethodRef.Name);
                if (readMd != null)
                    callerMd = readMd;

                /* If baseType exist and it's not networkbehaviour
                 * look into calling the ReadSyncVar method. */
                if (copyTd.BaseType != null && copyTd.BaseType.FullName != CodegenSession.ObjectHelper.NetworkBehaviour_FullName)
                {
                    readMd = copyTd.BaseType.Resolve().GetMethod(CodegenSession.ObjectHelper.NetworkBehaviour_ReadSyncVar_MethodRef.Name);
                    //Not all classes will have syncvars to read.
                    if (!_baseCalledReadSyncVars.Contains(callerMd) && readMd != null && callerMd != null)
                    {
                        MethodReference baseReadMr = CodegenSession.Module.ImportReference(readMd);
                        ILProcessor processor = callerMd.Body.GetILProcessor();
                        /* Calls base.ReadSyncVar and if result is true
                         * then exit methods. This is because a true return means the base
                         * was able to process the syncvar. */
                        List<Instruction> baseCallInsts = new List<Instruction>();
                        Instruction skipBaseReturn = processor.Create(OpCodes.Nop);
                        baseCallInsts.Add(processor.Create(OpCodes.Ldarg_0));
                        baseCallInsts.Add(processor.Create(OpCodes.Ldarg_1));
                        baseCallInsts.Add(processor.Create(OpCodes.Ldarg_2));
                        baseCallInsts.Add(processor.Create(OpCodes.Call, baseReadMr));
                        baseCallInsts.Add(processor.Create(OpCodes.Brfalse_S, skipBaseReturn));
                        baseCallInsts.Add(processor.Create(OpCodes.Ldc_I4_1));
                        baseCallInsts.Add(processor.Create(OpCodes.Ret));
                        baseCallInsts.Add(skipBaseReturn);
                        processor.InsertFirst(baseCallInsts);

                        _baseCalledReadSyncVars.Add(callerMd);
                    }
                }

                copyTd = TypeDefinitionExtensions.GetNextBaseClassToProcess(copyTd);

            } while (copyTd != null);


        }

        /// <summary>
        /// Creates a call to a SyncHandler.Read and sets value locally.
        /// </summary>
        /// <param name="typeDef"></param>
        /// <param name="syncIndex"></param>
        /// <param name="originalFieldDef"></param>
        /// <param name="syncHandlerGetValueMethodRef"></param>
        private MethodDefinition CreateSyncHandlerRead(TypeDefinition typeDef, uint syncIndex, FieldDefinition originalFieldDef, MethodReference accessorSetMethodRef)
        {
            Instruction jmpGoalInst;
            ILProcessor processor;

            //Get the read sync method, or create it if not present.
            MethodDefinition readSyncMethodDef = typeDef.GetMethod(CodegenSession.ObjectHelper.NetworkBehaviour_ReadSyncVar_MethodRef.Name);
            if (readSyncMethodDef == null)
            {
                readSyncMethodDef = new MethodDefinition(CodegenSession.ObjectHelper.NetworkBehaviour_ReadSyncVar_MethodRef.Name,
                (MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.Virtual),
                    typeDef.Module.TypeSystem.Void);
                readSyncMethodDef.ReturnType = CodegenSession.GeneralHelper.GetTypeReference(typeof(bool));

                CodegenSession.GeneralHelper.CreateParameter(readSyncMethodDef, typeof(PooledReader));
                CodegenSession.GeneralHelper.CreateParameter(readSyncMethodDef, typeof(uint));
                readSyncMethodDef.Body.InitLocals = true;

                processor = readSyncMethodDef.Body.GetILProcessor();
                //Return false as fall through.
                processor.Emit(OpCodes.Ldc_I4_0);
                processor.Emit(OpCodes.Ret);

                typeDef.Methods.Add(readSyncMethodDef);
            }
            //Already created. 
            else
            {
                processor = readSyncMethodDef.Body.GetILProcessor();
            }

            ParameterDefinition pooledReaderParameterDef = readSyncMethodDef.Parameters[0];
            ParameterDefinition indexParameterDef = readSyncMethodDef.Parameters[1];
            VariableDefinition nextValueVariableDef;
            List<Instruction> readInsts;

            /* Create a nop instruction placed at the first index of the method.
             * All instructions will be added before this, then the nop will be
             * removed afterwards. This ensures the newer instructions will
             * be above the previous. This let's the IL jump to a previously
             * created read instruction when the latest one fails conditions. */
            Instruction nopPlaceHolderInst = processor.Create(OpCodes.Nop);

            readSyncMethodDef.Body.Instructions.Insert(0, nopPlaceHolderInst);

            /* If there was a previously made read then set jmp goal to the first
             * condition for it. Otherwise set it to the last instruction, which would
             * be a ret. Keep in mind if ret has a value we must go back 2 index
             * rather than one. */
            jmpGoalInst = (_lastReadInstruction != null) ? _lastReadInstruction :
                readSyncMethodDef.Body.Instructions[readSyncMethodDef.Body.Instructions.Count - 2];

            //Check index first. if (index != syncIndex) return
            Instruction nextLastReadInstruction = processor.Create(OpCodes.Ldarg, indexParameterDef);
            processor.InsertBefore(jmpGoalInst, nextLastReadInstruction);

            uint hash = (uint)syncIndex;
            //uint hash = originalFieldDef.FullName.GetStableHash32();
            processor.InsertBefore(jmpGoalInst, processor.Create(OpCodes.Ldc_I4, (int)hash));
            //processor.InsertBefore(jmpGoalInst, processor.Create(OpCodes.Ldc_I4, syncIndex));
            processor.InsertBefore(jmpGoalInst, processor.Create(OpCodes.Bne_Un, jmpGoalInst));
            //PooledReader.ReadXXXX()
            readInsts = CodegenSession.ReaderHelper.CreateReadInstructions(processor, readSyncMethodDef, pooledReaderParameterDef,
                 originalFieldDef.FieldType, out nextValueVariableDef);
            if (readInsts == null)
                return null;
            //Add each instruction from CreateRead.
            foreach (Instruction i in readInsts)
                processor.InsertBefore(jmpGoalInst, i);

            //Call accessor with new value and false for asServer
            processor.InsertBefore(jmpGoalInst, processor.Create(OpCodes.Ldarg_0)); //this.
            processor.InsertBefore(jmpGoalInst, processor.Create(OpCodes.Ldloc, nextValueVariableDef));
            processor.InsertBefore(jmpGoalInst, processor.Create(OpCodes.Ldc_I4_0));
            processor.InsertBefore(jmpGoalInst, processor.Create(OpCodes.Call, accessorSetMethodRef));
            //Return true when able to process.
            processor.InsertBefore(jmpGoalInst, processor.Create(OpCodes.Ldc_I4_1));
            processor.InsertBefore(jmpGoalInst, processor.Create(OpCodes.Ret));

            _lastReadInstruction = nextLastReadInstruction;
            processor.Remove(nopPlaceHolderInst);

            return readSyncMethodDef;
        }

        /// <summary>
        /// Returns methods which may be modified by code generation.
        /// </summary>
        /// <param name="typeDef"></param>
        /// <returns></returns>
        private List<MethodDefinition> GetModifiableMethods(TypeDefinition typeDef)
        {
            List<MethodDefinition> results = new List<MethodDefinition>();
            //Typical methods.
            foreach (MethodDefinition methodDef in typeDef.Methods)
            {
                if (methodDef.Name == ".cctor")
                    continue;
                if (methodDef.IsConstructor)
                    continue;

                results.Add(methodDef);
            }
            //Accessors.
            foreach (PropertyDefinition propertyDef in typeDef.Properties)
            {
                if (propertyDef.GetMethod != null)
                    results.Add(propertyDef.GetMethod);
                if (propertyDef.SetMethod != null)
                    results.Add(propertyDef.SetMethod);
            }

            return results;
        }

        /// <summary>
        /// Returns the ProcessedSync entry for resolvedOpField.
        /// </summary>
        /// <param name="resolvedOpField"></param>
        /// <param name="psLst"></param>
        /// <returns></returns>
        private ProcessedSync GetProcessedSync(FieldReference resolvedOpField, List<ProcessedSync> psLst)
        {
            for (int i = 0; i < psLst.Count; i++)
            {
                if (psLst[i].OriginalFieldReference == resolvedOpField)
                    return psLst[i];
            }

            /* Fall through, not found. */
            CodegenSession.LogError($"Unable to find user referenced field for {resolvedOpField.Name}.");
            return null;
        }
    }
}