﻿using Mono.Cecil;
using Mono.Cecil.Cil;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace WinRTGuidPatcher
{
    class GuidPatcher
    {
        private readonly AssemblyDefinition assembly;
        private readonly TypeDefinition guidType;
        private readonly GenericInstanceType readOnlySpanOfByte;
        private readonly MethodReference readOnlySpanOfByteCtor;
        private readonly MethodReference guidCtor;
        private readonly TypeDefinition? guidAttributeType;
        private readonly MethodDefinition getTypeFromHandleMethod;
        private readonly TypeDefinition? guidGeneratorType;
        private readonly MethodDefinition? getIidMethod;
        private readonly MethodDefinition? createIidMethod;
        private readonly MethodDefinition? getHelperTypeMethod;

        private readonly Dictionary<TypeReference, MethodDefinition> ClosedTypeGuidDataMapping = new Dictionary<TypeReference, MethodDefinition>();
        private readonly TypeDefinition guidImplementationDetailsType;
        private readonly TypeDefinition guidDataBlockType;
        private SignatureGenerator signatureGenerator;

        public GuidPatcher(string assemblyPath, IAssemblyResolver assemblyResolver)
        {
            assembly = AssemblyDefinition.ReadAssembly(assemblyPath, new ReaderParameters(ReadingMode.Deferred)
            {
                ReadWrite = true,
                InMemory = true,
                AssemblyResolver = assemblyResolver,
                ThrowIfSymbolsAreNotMatching = false,
                SymbolReaderProvider = new DefaultSymbolReaderProvider(false),
                ApplyWindowsRuntimeProjections = false
            });

            guidImplementationDetailsType = new TypeDefinition(null, "<GuidPatcherImplementationDetails>", TypeAttributes.AutoClass | TypeAttributes.Sealed, assembly.MainModule.TypeSystem.Object);

            assembly.MainModule.Types.Add(guidImplementationDetailsType);

            guidDataBlockType = CecilExtensions.GetOrCreateDataBlockType(guidImplementationDetailsType, Unsafe.SizeOf<Guid>());

            var systemType = new TypeReference("System", "Type", assembly.MainModule, assembly.MainModule.TypeSystem.CoreLibrary).Resolve();

            guidType = new TypeReference("System", "Guid", assembly.MainModule, assembly.MainModule.TypeSystem.CoreLibrary).Resolve();

            readOnlySpanOfByte = new GenericInstanceType(new TypeReference("System", "ReadOnlySpan`1", assembly.MainModule, assembly.MainModule.TypeSystem.CoreLibrary, true))
            {
                GenericArguments =
                {
                    assembly.MainModule.TypeSystem.Byte
                }
            };

            readOnlySpanOfByteCtor = assembly.MainModule.ImportReference(new MethodReference(".ctor", assembly.MainModule.TypeSystem.Void, readOnlySpanOfByte)
            {
                Parameters =
                {
                    new ParameterDefinition(new PointerType(assembly.MainModule.TypeSystem.Void)),
                    new ParameterDefinition(assembly.MainModule.TypeSystem.Int32),
                }
            });

            guidCtor = assembly.MainModule.ImportReference(guidType.Methods.First(m => m.IsConstructor && m.Parameters.Count == 1 && m.Parameters[0].ParameterType.Resolve() == readOnlySpanOfByte.Resolve()));

            getTypeFromHandleMethod = systemType.Methods.First(m => m.Name == "GetTypeFromHandle");

            guidGeneratorType = null;

            TypeDefinition? typeExtensionsType = null;

            foreach (var asm in assembly.MainModule.AssemblyReferences)
            {
                if (asm.Name == "WinRT.Runtime")
                {
                    guidGeneratorType =
                        new TypeReference("WinRT", "GuidGenerator", assembly.MainModule, asm).Resolve();
                    typeExtensionsType = new TypeReference("WinRT", "TypeExtensions", assembly.MainModule, asm).Resolve();
                }
                else if (asm.Name == "System.Runtime.InteropServices")
                {
                    guidAttributeType = new TypeReference("System.Runtime.InteropServices", "GuidAttribute", assembly.MainModule, asm).Resolve();
                }
            }

            if (guidGeneratorType is not null && typeExtensionsType is not null)
            {
                getIidMethod = guidGeneratorType.Methods.First(m => m.Name == "GetIID");
                createIidMethod = guidGeneratorType.Methods.First(m => m.Name == "CreateIID");
                getHelperTypeMethod = typeExtensionsType.Methods.First(m => m.Name == "GetHelperType");
            }

            signatureGenerator = new SignatureGenerator(assembly, guidAttributeType!);
        }

        public int ProcessAssembly()
        {
            if (guidGeneratorType is null || guidAttributeType is null)
            {
                return 0;
            }
            int numPatches = 0;
            var methods = from module in assembly.Modules
                          from type in module.Types
                          from method in type.Methods
                          where method.HasBody
                          select method;

            foreach (var method in methods)
            {
                numPatches += ProcessMethodBody(method.Body, getTypeFromHandleMethod, getIidMethod!, createIidMethod!);
            }

            return numPatches;
        }

        public void SaveAssembly(string targetDirectory)
        {
            assembly.Write($"{targetDirectory}{Path.DirectorySeparatorChar}{assembly.Name.Name}.dll");
        }

        enum State
        {
            Start,
            Ldtoken,
            GetTypeFromHandle,
            GetHelperTypeOptional
        }

        private int ProcessMethodBody(MethodBody body, MethodDefinition getTypeFromHandleMethod, MethodDefinition getIidMethod, MethodDefinition createIidMethod)
        {
            int numberOfReplacements = 0;
            TypeReference? type = null;
            State state = State.Start;
            int startIlIndex = -1;
            int numberOfInstructionsToOverwrite = 3;
            for (int i = 0; i < body.Instructions.Count; i++)
            {
                var instruction = body.Instructions[i];
                switch (state)
                {
                    case State.Start:
                        if (instruction.OpCode.Code != Code.Ldtoken)
                        {
                            continue;
                        }
                        var typeMaybe = (TypeReference)instruction.Operand;
                        if (!typeMaybe.IsGenericParameter)
                        {
                            state = State.Ldtoken;
                            type = typeMaybe;
                            startIlIndex = i;
                        }
                        break;
                    case State.Ldtoken:
                        {
                            if (instruction.OpCode.Code != Code.Call)
                            {
                                state = State.Start;
                                type = null;
                                continue;
                            }
                            var method = ((MethodReference)instruction.Operand).Resolve();
                            if (method == getTypeFromHandleMethod)
                            {
                                state = State.GetTypeFromHandle;
                            }
                        }
                        break;
                    case State.GetTypeFromHandle:
                        {
                            if (instruction.OpCode.Code != Code.Call)
                            {
                                state = State.Start;
                                type = null;
                                continue;
                            }
                            var method = ((MethodReference)instruction.Operand).Resolve();
                            if (method == getHelperTypeMethod)
                            {
                                numberOfInstructionsToOverwrite++;
                                state = State.GetHelperTypeOptional;
                                continue;
                            }
                            else
                            {
                                goto case State.GetHelperTypeOptional;
                            }
                        }
                    case State.GetHelperTypeOptional:
                        {
                            if (instruction.OpCode.Code != Code.Call)
                            {
                                state = State.Start;
                                type = null;
                                continue;
                            }
                            var method = ((MethodReference)instruction.Operand).Resolve();
                            if (method == getIidMethod || method == createIidMethod)
                            {
                                try
                                {
                                    bool didPatch = false;
                                    if (type!.IsGenericInstance)
                                    {
                                        didPatch = PatchGenericTypeIID(body, startIlIndex, type, numberOfInstructionsToOverwrite);
                                    }
                                    else
                                    {
                                        didPatch = PatchNonGenericTypeIID(body, startIlIndex, type, numberOfInstructionsToOverwrite);
                                    }

                                    if (didPatch)
                                    {
                                        numberOfReplacements++;
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Debug.WriteLine($"Exception thrown during patching {body.Method.FullName}: {ex}");
                                }
                            }
                            else
                            {
                                state = State.Start;
                                type = null;
                                startIlIndex = -1;
                            }
                        }
                        break;
                    default:
                        throw new InvalidOperationException();
                }
            }
            return numberOfReplacements;
        }

        private bool PatchNonGenericTypeIID(MethodBody body, int startILIndex, TypeReference type, int numberOfInstructionsToOverwrite)
        {
            if (numberOfInstructionsToOverwrite < 2)
            {
                return false;
            }

            if (!ClosedTypeGuidDataMapping.TryGetValue(type, out var guidDataMethod))
            {
                Guid? guidValue = type.ReadGuidFromAttribute(guidAttributeType!);
                if (guidValue == null)
                {
                    return false;
                }

                guidDataMethod = CecilExtensions.CreateIIDDataGetter(type, guidValue.Value, guidDataBlockType, guidImplementationDetailsType, readOnlySpanOfByte, readOnlySpanOfByteCtor);

                guidImplementationDetailsType.Methods.Add(guidDataMethod);
                ClosedTypeGuidDataMapping[type] = guidDataMethod;
            }

            ReplaceWithCallToGuidDataGetter(body, startILIndex, numberOfInstructionsToOverwrite, guidDataMethod);

            return true;
        }

        private bool PatchGenericTypeIID(MethodBody body, int startILIndex, TypeReference type, int numberOfInstructionsToOverwrite)
        {
            SignaturePart rootSignaturePart = signatureGenerator.GetSignatureParts(type);

            var guidDataMethod = new MethodDefinition($"<IIDData>{type.FullName}", MethodAttributes.Assembly | MethodAttributes.Static, readOnlySpanOfByte);

            guidImplementationDetailsType.Methods.Add(guidDataMethod);

            var emitter = new SignatureEmitter(type, guidDataMethod);
            VisitSignature(rootSignaturePart, emitter);

            emitter.EmitGuidGetter(guidDataBlockType, guidImplementationDetailsType, readOnlySpanOfByte, readOnlySpanOfByteCtor, guidGeneratorType!);

            MethodReference guidDataMethodReference = guidDataMethod;
            if (guidDataMethodReference.HasGenericParameters)
            {
                var genericGuidDataMethodReference = new GenericInstanceMethod(guidDataMethodReference);
                foreach (var param in guidDataMethodReference.GenericParameters)
                {
                    genericGuidDataMethodReference.GenericArguments.Add(emitter.GenericParameterMapping[param]);
                }
                guidDataMethodReference = genericGuidDataMethodReference;
            }

            ReplaceWithCallToGuidDataGetter(body, startILIndex, numberOfInstructionsToOverwrite, guidDataMethodReference);
            return true;
        }

        private void ReplaceWithCallToGuidDataGetter(MethodBody body, int startILIndex, int numberOfInstructionsToOverwrite, MethodReference guidDataMethod)
        {
            var il = body.GetILProcessor();
            il.Replace(startILIndex, Instruction.Create(OpCodes.Call, guidDataMethod));
            il.Replace(startILIndex + 1, Instruction.Create(OpCodes.Newobj, guidCtor));
            for (int i = 2; i < numberOfInstructionsToOverwrite; i++)
            {
                il.Replace(startILIndex + i, Instruction.Create(OpCodes.Nop));
            }
        }

        private void VisitSignature(SignaturePart rootSignaturePart, SignatureEmitter emitter)
        {
            switch (rootSignaturePart)
            {
                case BasicSignaturePart basic:
                    {
                        emitter.PushString(basic.Type switch
                        {
                            SignatureType.@string => "string",
                            SignatureType.iinspectable => "cinterface(IInspectable)",
                            _ => basic.Type.ToString()
                        });
                    }
                    break;
                case SignatureWithChildren group:
                    {
                        emitter.PushString($"{group.GroupingName}(");
                        emitter.PushString(group.ThisEntitySignature);
                        foreach (var item in group.ChildrenSignatures)
                        {
                            emitter.PushString(";");
                            VisitSignature(item, emitter);
                        }
                        emitter.PushString(")");
                    }
                    break;
                case GuidSignature guid:
                    {
                        emitter.PushString(guid.IID.ToString("B"));
                    }
                    break;
                case NonGenericDelegateSignature del:
                    {
                        emitter.PushString($"delegate({del.DelegateIID:B}");
                    }
                    break;
                case UninstantiatedGeneric gen:
                    {
                        emitter.PushGenericParameter(gen.OriginalGenericParameter);
                    }
                    break;
                case CustomSignatureMethod custom:
                    {
                        emitter.PushCustomSignature(custom.Method);
                    }
                    break;
                default:
                    break;
            }
        }
    }
}
