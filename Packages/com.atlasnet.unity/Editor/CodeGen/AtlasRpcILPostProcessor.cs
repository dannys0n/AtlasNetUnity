using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Unity.CompilationPipeline.Common.Diagnostics;
using Unity.CompilationPipeline.Common.ILPostProcessing;

namespace AtlasNet.CodeGen
{
    /// <summary>Turns a direct call to an [Rpc] method into a send while retaining its body for receive.</summary>
    public sealed class AtlasRpcILPostProcessor : ILPostProcessor
    {
        private const string RuntimeAssembly = "AtlasNet.Unity";
        private const string RpcAttribute = "AtlasNet.RpcAttribute";
        private const string BehaviourType = "AtlasNet.NetworkBehaviour";
        private const string SessionType = "AtlasNet.SessionId";

        public override ILPostProcessor GetInstance() => this;

        public override bool WillProcess(ICompiledAssembly assembly) =>
            assembly.Name != RuntimeAssembly && assembly.Name != "Unity.AtlasNet.CodeGen" &&
            assembly.References.Any(reference =>
            {
                string name = Path.GetFileNameWithoutExtension(reference);
                return name == RuntimeAssembly || name == RuntimeAssembly + ".ref";
            });

        public override ILPostProcessResult Process(ICompiledAssembly compiled)
        {
            var diagnostics = new List<DiagnosticMessage>();
            if (!WillProcess(compiled)) return new ILPostProcessResult(compiled.InMemoryAssembly, diagnostics);

            try
            {
                using (var resolver = new DefaultAssemblyResolver())
                using (var peInput = new MemoryStream(compiled.InMemoryAssembly.PeData))
                using (var pdbInput = new MemoryStream(compiled.InMemoryAssembly.PdbData ?? Array.Empty<byte>()))
                {
                    foreach (string reference in compiled.References)
                    {
                        string directory = Path.GetDirectoryName(reference);
                        if (!string.IsNullOrEmpty(directory) && !resolver.GetSearchDirectories().Contains(directory))
                            resolver.AddSearchDirectory(directory);
                    }

                    bool symbols = pdbInput.Length > 0;
                    var reader = new ReaderParameters
                    {
                        AssemblyResolver = resolver,
                        ReadingMode = ReadingMode.Immediate,
                        ReadSymbols = symbols,
                        SymbolStream = symbols ? pdbInput : null,
                        SymbolReaderProvider = symbols ? new PortablePdbReaderProvider() : null
                    };
                    using (var assembly = AssemblyDefinition.ReadAssembly(peInput, reader))
                    {
                        ModuleDefinition module = assembly.MainModule;
                        AssemblyNameReference runtime = module.AssemblyReferences.FirstOrDefault(a => a.Name == RuntimeAssembly);
                        if (runtime == null) return new ILPostProcessResult(compiled.InMemoryAssembly, diagnostics);

                        bool changed = false;
                        foreach (var type in AllTypes(module.Types))
                        {
                            foreach (var method in type.Methods)
                            {
                                var attribute = method.CustomAttributes.FirstOrDefault(a => a.AttributeType.FullName == RpcAttribute);
                                if (attribute == null) continue;
                                if (!IsNetworkBehaviour(type))
                                    throw new InvalidOperationException($"{method.FullName}: [Rpc] requires NetworkBehaviour");
                                Weave(module, runtime, method, attribute);
                                changed = true;
                            }
                        }

                        if (!changed) return new ILPostProcessResult(compiled.InMemoryAssembly, diagnostics);
                        using (var peOutput = new MemoryStream())
                        using (var pdbOutput = new MemoryStream())
                        {
                            var writer = new WriterParameters
                            {
                                WriteSymbols = symbols,
                                SymbolStream = symbols ? pdbOutput : null,
                                SymbolWriterProvider = symbols ? new PortablePdbWriterProvider() : null
                            };
                            assembly.Write(peOutput, writer);
                            return new ILPostProcessResult(
                                new InMemoryAssembly(peOutput.ToArray(), symbols ? pdbOutput.ToArray() : Array.Empty<byte>()), diagnostics);
                        }
                    }
                }
            }
            catch (Exception error)
            {
                diagnostics.Add(new DiagnosticMessage
                {
                    DiagnosticType = DiagnosticType.Error,
                    MessageData = "AtlasNet RPC weaving failed: " + error
                });
                return new ILPostProcessResult(compiled.InMemoryAssembly, diagnostics);
            }
        }

        private static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> types)
        {
            foreach (var type in types)
            {
                yield return type;
                foreach (var nested in AllTypes(type.NestedTypes)) yield return nested;
            }
        }

        private static bool IsNetworkBehaviour(TypeDefinition type)
        {
            for (TypeReference current = type.BaseType; current != null; current = current.Resolve()?.BaseType)
                if (current.FullName == BehaviourType) return true;
            return false;
        }

        private static void Weave(ModuleDefinition module, AssemblyNameReference runtime,
            MethodDefinition method, CustomAttribute attribute)
        {
            if (method.IsStatic || method.IsAbstract || method.HasGenericParameters || method.ReturnType.FullName != "System.Void" ||
                !method.Name.EndsWith("Rpc", StringComparison.Ordinal) || !method.HasBody)
                throw new InvalidOperationException($"{method.FullName}: RPCs must be non-generic instance void methods ending in Rpc");
            if (method.DeclaringType.HasGenericParameters || method.Body.ExceptionHandlers.Count != 0)
                throw new InvalidOperationException($"{method.FullName}: generic behaviours and try/catch RPC bodies are not supported yet");
            if (attribute.ConstructorArguments.Count != 1)
                throw new InvalidOperationException($"{method.FullName}: [Rpc] needs one SendTo target");

            int target = Convert.ToInt32(attribute.ConstructorArguments[0].Value);
            if (target < 0 || target > 4) throw new InvalidOperationException($"{method.FullName}: invalid SendTo target");
            bool hasTarget = target == 3;
            bool hasSender = target == 0 && method.Parameters.Count > 0 &&
                method.Parameters[method.Parameters.Count - 1].ParameterType.FullName == SessionType;
            if (hasTarget && (method.Parameters.Count == 0 || method.Parameters[0].ParameterType.FullName != SessionType))
                throw new InvalidOperationException($"{method.FullName}: SpecifiedInParams requires a first SessionId target");

            int firstPayload = hasTarget ? 1 : 0;
            int payloadCount = method.Parameters.Count - firstPayload - (hasSender ? 1 : 0);
            for (int i = 0; i < payloadCount; i++)
            {
                var parameter = method.Parameters[i + firstPayload];
                if (parameter.ParameterType.IsByReference || !Supported(parameter.ParameterType.FullName))
                    throw new InvalidOperationException($"{method.FullName}: unsupported RPC parameter {parameter.Name}");
            }
            if (method.Body.Instructions.Count == 0)
                throw new InvalidOperationException($"{method.FullName}: RPC has no body");

            var behaviour = new TypeReference("AtlasNet", "NetworkBehaviour", module, runtime);
            var enter = new MethodReference("EnterReceivedRpc", module.TypeSystem.Boolean, behaviour) { HasThis = true };
            enter.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));
            var send = new MethodReference("SendRpc", module.TypeSystem.Void, behaviour) { HasThis = true };
            if (hasTarget)
                send.Parameters.Add(new ParameterDefinition(new TypeReference("AtlasNet", "SessionId", module, runtime) { IsValueType = true }));
            send.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));
            send.Parameters.Add(new ParameterDefinition(new ArrayType(module.TypeSystem.Object)));

            var il = method.Body.GetILProcessor();
            var original = method.Body.Instructions[0];
            void Insert(Instruction instruction) => il.InsertBefore(original, instruction);
            Insert(il.Create(OpCodes.Ldarg_0));
            Insert(il.Create(OpCodes.Ldstr, method.Name));
            Insert(il.Create(OpCodes.Call, module.ImportReference(enter)));
            Insert(il.Create(OpCodes.Brtrue, original));
            Insert(il.Create(OpCodes.Ldarg_0));
            if (hasTarget) Insert(il.Create(OpCodes.Ldarg, method.Parameters[0]));
            Insert(il.Create(OpCodes.Ldstr, method.Name));
            Insert(il.Create(OpCodes.Ldc_I4, payloadCount));
            Insert(il.Create(OpCodes.Newarr, module.TypeSystem.Object));
            for (int i = 0; i < payloadCount; i++)
            {
                var parameter = method.Parameters[i + firstPayload];
                Insert(il.Create(OpCodes.Dup));
                Insert(il.Create(OpCodes.Ldc_I4, i));
                Insert(il.Create(OpCodes.Ldarg, parameter));
                if (parameter.ParameterType.IsValueType)
                    Insert(il.Create(OpCodes.Box, module.ImportReference(parameter.ParameterType)));
                Insert(il.Create(OpCodes.Stelem_Ref));
            }
            Insert(il.Create(OpCodes.Call, module.ImportReference(send)));
            Insert(il.Create(OpCodes.Ret));
            method.Body.MaxStackSize = Math.Max(method.Body.MaxStackSize, 8);
        }

        private static bool Supported(string name) => name == "System.Int32" || name == "System.Single" ||
            name == "System.Boolean" || name == "System.String" || name == "UnityEngine.Vector2" ||
            name == "UnityEngine.Vector3" || name == "UnityEngine.Quaternion";
    }
}
