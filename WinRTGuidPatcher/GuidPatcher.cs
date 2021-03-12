using Mono.Cecil;
using Mono.Cecil.Cil;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        private readonly TypeDefinition guidAttributeType;
        private readonly MethodDefinition getTypeFromHandleMethod;
        private readonly TypeDefinition guidGeneratorType;
        private readonly MethodDefinition getIidMethod;
        private readonly MethodDefinition createIidMethod;
        private readonly MethodDefinition getHelperTypeMethod;
        private readonly Dictionary<TypeReference, MethodDefinition> ClosedTypeGuidDataMapping = new Dictionary<TypeReference, MethodDefinition>();
        private readonly TypeDefinition guidImplementationDetailsType;
        private readonly TypeDefinition guidDataBlockType;

        public GuidPatcher(string assemblyPath)
        {
            assembly = AssemblyDefinition.ReadAssembly(assemblyPath, new ReaderParameters(ReadingMode.Deferred)
            {
                ReadWrite = true,
                InMemory = true,
                AssemblyResolver = new FolderAssemblyResolver(new DirectoryInfo(Path.GetDirectoryName(assemblyPath))),
                ThrowIfSymbolsAreNotMatching = false,
                SymbolReaderProvider = new DefaultSymbolReaderProvider(false),
                ApplyWindowsRuntimeProjections = false
            });

            guidImplementationDetailsType = new TypeDefinition(null, "<GuidPatcherImplementationDetails>", TypeAttributes.AutoClass | TypeAttributes.Sealed, assembly.MainModule.TypeSystem.Object);

            guidDataBlockType = new TypeDefinition(null, "<GuidDataBlock>", TypeAttributes.AutoClass | TypeAttributes.Sealed | TypeAttributes.NestedAssembly | TypeAttributes.SequentialLayout | TypeAttributes.AnsiClass, new TypeReference("System", "ValueType", assembly.MainModule, assembly.MainModule.TypeSystem.CoreLibrary))
            {
                PackingSize = 1,
                ClassSize = 16
            };

            guidImplementationDetailsType.NestedTypes.Add(guidDataBlockType);

            assembly.MainModule.Types.Add(guidImplementationDetailsType);

            var systemType = new TypeReference("System", "Type", assembly.MainModule, assembly.MainModule.TypeSystem.CoreLibrary).Resolve();

            guidType = new TypeReference("System", "Guid", assembly.MainModule, assembly.MainModule.TypeSystem.CoreLibrary).Resolve();

            readOnlySpanOfByte = new GenericInstanceType(new TypeReference("System", "ReadOnlySpan`1", assembly.MainModule, assembly.MainModule.TypeSystem.CoreLibrary))
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

            TypeDefinition typeExtensionsType = null;

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

            getIidMethod = guidGeneratorType.Methods.First(m => m.Name == "GetIID");
            createIidMethod = guidGeneratorType.Methods.First(m => m.Name == "CreateIID");
            getHelperTypeMethod = typeExtensionsType.Methods.First(m => m.Name == "GetHelperType");
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
                numPatches += ProcessMethodBody(method.Body, getTypeFromHandleMethod, getIidMethod, createIidMethod);
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
            TypeReference type = null;
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
                        break;
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
                                bool didPatch = false;
                                if (type.IsGenericInstance && !type.HasGenericParameters)
                                {
                                    didPatch = PatchInstantiatedGenericTypeIID(body, startIlIndex, type, numberOfInstructionsToOverwrite);
                                }
                                else if (type.IsGenericInstance)
                                {
                                    didPatch = PatchUninstantiatedGenericTypeIID(body, startIlIndex, type, numberOfInstructionsToOverwrite);
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
                Guid? guidValue = ReadGuidFromAttribute(type);
                if (guidValue == null)
                {
                    return false;
                }
                guidDataMethod = CreateIIDDataGetter(type, guidValue.Value);

                guidImplementationDetailsType.Methods.Add(guidDataMethod);
                ClosedTypeGuidDataMapping[type] = guidDataMethod;
            }

            var il = body.GetILProcessor();
            il.Replace(startILIndex, Instruction.Create(OpCodes.Call, guidDataMethod));
            il.Replace(startILIndex + 1, Instruction.Create(OpCodes.Newobj, guidCtor));
            for (int i = 2; i < numberOfInstructionsToOverwrite; i++)
            {
                il.Replace(startILIndex + i, Instruction.Create(OpCodes.Nop));
            }

            return true;
        }

        private Guid? ReadGuidFromAttribute(TypeReference type)
        {
            TypeDefinition def = type.Resolve();
            var guidAttr = def.CustomAttributes.FirstOrDefault(attr => attr.AttributeType.Resolve() == guidAttributeType);
            if (guidAttr is null)
            {
                return null;
            }
            return new Guid((string)guidAttr.ConstructorArguments[0].Value);
        }

        private MethodDefinition CreateIIDDataGetter(TypeReference type, Guid iidValue)
        {
            MethodDefinition guidDataMethod;
            // TODO: Figure out how to get Mono.Cecil to write out the InitialValue field.
            var guidDataField = new FieldDefinition($"<IIDDataField>{type.FullName}", FieldAttributes.Private | FieldAttributes.Static | FieldAttributes.InitOnly, guidDataBlockType)
            {
                InitialValue = iidValue.ToByteArray()
            };
            guidImplementationDetailsType.Fields.Add(guidDataField);

            guidDataMethod = new MethodDefinition($"<IIDData>{type.FullName}", MethodAttributes.Assembly | MethodAttributes.Static, readOnlySpanOfByte);

            var ilProcessor = guidDataMethod.Body.GetILProcessor();
            ilProcessor.Append(Instruction.Create(OpCodes.Ldflda, guidDataField));
            ilProcessor.Append(Instruction.Create(OpCodes.Ldc_I4, 16));
            ilProcessor.Append(Instruction.Create(OpCodes.Newobj, readOnlySpanOfByteCtor));
            ilProcessor.Append(Instruction.Create(OpCodes.Ret));
            return guidDataMethod;
        }

        private bool PatchUninstantiatedGenericTypeIID(MethodBody body, int startILIndex, TypeReference type, int numberOfInstructionsToOverwrite)
        {
            return false;
        }

        private bool PatchInstantiatedGenericTypeIID(MethodBody body, int startILIndex, TypeReference type, int numberOfInstructionsToOverwrite)
        {
            return false;
        }
    }
}
