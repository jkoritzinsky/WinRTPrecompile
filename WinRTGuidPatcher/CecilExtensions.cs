﻿using Mono.Cecil;
using Mono.Cecil.Cil;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WinRTGuidPatcher
{
    static class CecilExtensions
    {
        internal static Guid? ReadGuidFromAttribute(this TypeReference type, TypeReference guidAttributeType)
        {
            TypeDefinition def = type.Resolve();
            var guidAttr = def.CustomAttributes.FirstOrDefault(attr => attr.AttributeType.Resolve() == guidAttributeType);
            if (guidAttr is null)
            {
                TypeDefinition abiType = def.GetCswinrtAbiTypeDefinition();
                if (abiType is not null)
                {
                    return abiType.ReadGuidFromAttribute(guidAttributeType);
                }
                return null;
            }
            return new Guid((string)guidAttr.ConstructorArguments[0].Value);
        }

        internal static TypeDefinition GetCswinrtAbiTypeDefinition(this TypeReference type)
        {
            var resolvedType = type.Resolve();

            return resolvedType.Module.GetType($"ABI.{resolvedType.FullName}");
        }

        internal static MethodDefinition CreateIIDDataGetter(TypeReference type, Guid iidValue, TypeDefinition dataBlockType, TypeDefinition parentType, TypeReference readOnlySpanOfByte, MethodReference readOnlySpanOfByteCtor)
        {
            var guidDataMethod = new MethodDefinition($"<IIDData>{type.FullName}", MethodAttributes.Assembly | MethodAttributes.Static, readOnlySpanOfByte);

            WriteIIDDataGetterBody(guidDataMethod, type, iidValue, dataBlockType, parentType, readOnlySpanOfByteCtor);
            return guidDataMethod;
        }

        internal static void WriteIIDDataGetterBody(MethodDefinition method, TypeReference type, Guid iidValue, TypeDefinition dataBlockType, TypeDefinition parentType, MethodReference readOnlySpanOfByteCtor)
        {
            // TODO: Figure out how to get Mono.Cecil to write out the InitialValue field.
            var guidDataField = new FieldDefinition($"<IIDDataField>{type.FullName}", FieldAttributes.Private | FieldAttributes.Static | FieldAttributes.InitOnly, dataBlockType)
            {
                InitialValue = iidValue.ToByteArray()
            };
            parentType.Fields.Add(guidDataField);

            var ilProcessor = method.Body.GetILProcessor();
            ilProcessor.Clear();
            ilProcessor.Append(Instruction.Create(OpCodes.Ldsflda, guidDataField));
            ilProcessor.Append(Instruction.Create(OpCodes.Ldc_I4, 16));
            ilProcessor.Append(Instruction.Create(OpCodes.Newobj, readOnlySpanOfByteCtor));
            ilProcessor.Append(Instruction.Create(OpCodes.Ret));
        }

        internal static TypeDefinition GetOrCreateDataBlockType(TypeDefinition parentType, int size)
        {
            if (size < 0 || size > ushort.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(size));
            }

            string typeName = $"__StaticDataBlock<>Size={size}";

            var typeRef = new TypeReference(null, typeName, parentType.Module, parentType.Module)
            {
                DeclaringType = parentType
            };

            if (typeRef.Resolve() is TypeDefinition td)
            {
                return td;
            }

            td = new TypeDefinition(null, "<GuidDataBlock>", TypeAttributes.AutoClass | TypeAttributes.Sealed | TypeAttributes.NestedAssembly | TypeAttributes.SequentialLayout | TypeAttributes.AnsiClass, new TypeReference("System", "ValueType", parentType.Module, parentType.Module.TypeSystem.CoreLibrary))
            {
                PackingSize = 1,
                ClassSize = 16
            };

            parentType.NestedTypes.Add(td);

            return td;
        }

        internal static TypeReference? FindTypeReference(ModuleDefinition module, string ns, string name, string basicAssemblyName)
        {
            foreach (var asm in module.AssemblyReferences)
            {
                if (asm.Name == basicAssemblyName || asm.Name.StartsWith($"{basicAssemblyName},"))
                {
                    TypeReference typeRef = new TypeReference(ns, name, module, asm);
                    if (typeRef.Resolve() != null)
                    {
                        return module.ImportReference(typeRef);
                    }
                    break;
                }
            }
            return null;
        }
    }
}