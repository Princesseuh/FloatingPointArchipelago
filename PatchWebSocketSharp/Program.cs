using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

/// <summary>
/// Patches websocket-sharp.dll to remove all SslStream usage and fix TcpClient(host,port)
/// calls so it can run on Unity 4.3's ancient Mono.
///
/// Fixes applied:
///   1. SslStream: find newobj SslStream, nop the guarding if(_secure) block entirely.
///   2. Dns: stub out any method that calls System.Net.Dns.* (return null/false/void).
///   3. TcpClient(host,port): replace every `newobj TcpClient::.ctor(string,int32)` with
///      a DNS-free sequence:
///        - store host/port into fresh locals
///        - new TcpClient()   [no-arg ctor, does not touch Dns]
///        - call IPAddress.Parse(host)
///        - new IPEndPoint(ip, port)
///        - ldflda _tcpClient (already on stack from preceding stfld target)
///        - ... actually: we rewrite the whole relevant sequence in-place.
/// </summary>
class Program
{
    static void Main(string[] args)
    {
        string input  = args.Length > 0 ? args[0]
            : @"C:\Users\Erika\Desktop\FloatingPointArchipelago\src\bin\Release\net35\websocket-sharp.dll";
        string output = args.Length > 1 ? args[1] : input;

        Console.WriteLine("=== websocket-sharp Patcher v3 ===");
        Console.WriteLine($"Input:  {input}");
        Console.WriteLine($"Output: {output}");
        Console.WriteLine();

        var asm = AssemblyDefinition.ReadAssembly(input, new ReaderParameters { ReadWrite = true });
        int totalChanges = 0;

        // ── Pass 1: SslStream removal ────────────────────────────────────────
        foreach (var type in asm.MainModule.Types.SelectMany(AllTypes))
        {
            foreach (var method in type.Methods.ToList())
            {
                if (!method.HasBody) continue;
                int changed = PatchSslStream(method);
                if (changed > 0)
                {
                    Console.WriteLine($"  [SSL] Patched {method.FullName}: {changed} changes");
                    totalChanges += changed;
                }
            }
        }

        // Rename SslStream TypeReference -> System.Object
        foreach (var r in asm.MainModule.GetTypeReferences()
                             .Where(r => r.FullName == "System.Net.Security.SslStream")
                             .ToList())
        {
            Console.WriteLine($"  [SSL] Renaming TypeRef {r.FullName} -> System.Object");
            r.Namespace = "System";
            r.Name      = "Object";
            r.Scope     = asm.MainModule.TypeSystem.Object.Scope;
            totalChanges++;
        }

        // ── Pass 2: Dns stub ─────────────────────────────────────────────────
        foreach (var type in asm.MainModule.Types.SelectMany(AllTypes))
        {
            foreach (var method in type.Methods)
            {
                if (!method.HasBody) continue;

                bool hasDns = method.Body.Instructions.Any(i =>
                    (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt) &&
                    i.Operand is MethodReference mr2 &&
                    mr2.DeclaringType.FullName == "System.Net.Dns");

                if (!hasDns) continue;

                Console.WriteLine($"  [DNS] Stubbing {method.FullName}");
                var ret  = method.ReturnType;
                var body = method.Body;
                body.Instructions.Clear();
                body.Variables.Clear();
                body.ExceptionHandlers.Clear();
                var proc = body.GetILProcessor();

                if (ret.FullName == "System.Void")
                    proc.Emit(OpCodes.Ret);
                else if (ret.IsValueType)
                { proc.Emit(OpCodes.Ldc_I4_0); proc.Emit(OpCodes.Ret); }
                else
                { proc.Emit(OpCodes.Ldnull); proc.Emit(OpCodes.Ret); }

                totalChanges++;
            }
        }

        // Rename Dns TypeReference -> System.Object
        foreach (var r in asm.MainModule.GetTypeReferences()
                              .Where(r2 => r2.FullName == "System.Net.Dns")
                              .ToList())
        {
            Console.WriteLine($"  [DNS] Renaming TypeRef {r.FullName} -> System.Object");
            r.Namespace = "System";
            r.Name      = "Object";
            r.Scope     = asm.MainModule.TypeSystem.Object.Scope;
            totalChanges++;
        }

        // ── Pass 3: TcpClient(host,port) -> TcpClient() + Connect(IPEndPoint) ─
        // Import required types/methods from mscorlib / System
        var systemAsmRef = asm.MainModule.AssemblyReferences
            .FirstOrDefault(a => a.Name == "System");
        if (systemAsmRef == null)
            throw new Exception("Cannot find 'System' assembly reference in websocket-sharp");

        var systemRef = new AssemblyNameReference("System", systemAsmRef.Version)
        {
            PublicKeyToken = systemAsmRef.PublicKeyToken,
            Culture        = systemAsmRef.Culture
        };

        // Helper: import a TypeReference from System.dll
        TypeReference ImportFromSystem(string ns, string name)
        {
            var tr = new TypeReference(ns, name, asm.MainModule, systemAsmRef);
            return asm.MainModule.ImportReference(tr);
        }

        var tcpClientType  = ImportFromSystem("System.Net.Sockets", "TcpClient");
        var ipAddressType  = ImportFromSystem("System.Net",         "IPAddress");
        var ipEndPointType = ImportFromSystem("System.Net",         "IPEndPoint");
        var socketType     = ImportFromSystem("System.Net.Sockets", "Socket");

        // TcpClient() no-arg ctor
        var tcpCtorNoArg = new MethodReference(".ctor", asm.MainModule.TypeSystem.Void, tcpClientType)
            { HasThis = true };
        var tcpCtorNoArgImported = asm.MainModule.ImportReference(tcpCtorNoArg);

        // IPAddress.Parse(string) -> IPAddress
        var ipParseMeth = new MethodReference("Parse", ipAddressType, ipAddressType)
            { HasThis = false };
        ipParseMeth.Parameters.Add(new ParameterDefinition(asm.MainModule.TypeSystem.String));
        var ipParseImported = asm.MainModule.ImportReference(ipParseMeth);

        // IPEndPoint(IPAddress, int) ctor
        var ipepCtor = new MethodReference(".ctor", asm.MainModule.TypeSystem.Void, ipEndPointType)
            { HasThis = true };
        ipepCtor.Parameters.Add(new ParameterDefinition(ipAddressType));
        ipepCtor.Parameters.Add(new ParameterDefinition(asm.MainModule.TypeSystem.Int32));
        var ipepCtorImported = asm.MainModule.ImportReference(ipepCtor);

        // TcpClient.get_Client() -> Socket
        var tcpGetClient = new MethodReference("get_Client", socketType, tcpClientType)
            { HasThis = true };
        var tcpGetClientImported = asm.MainModule.ImportReference(tcpGetClient);

        // Socket.Connect(EndPoint) — IPEndPoint inherits EndPoint
        var endPointType = ImportFromSystem("System.Net", "EndPoint");
        var socketConnect = new MethodReference("Connect", asm.MainModule.TypeSystem.Void, socketType)
            { HasThis = true };
        socketConnect.Parameters.Add(new ParameterDefinition(endPointType));
        var socketConnectImported = asm.MainModule.ImportReference(socketConnect);

        foreach (var type in asm.MainModule.Types.ToList().SelectMany(AllTypes))
        {
            foreach (var method in type.Methods.ToList())
            {
                if (!method.HasBody) continue;
                int changed = PatchTcpClientCtor(method,
                    tcpCtorNoArgImported, ipParseImported, ipepCtorImported,
                    tcpGetClientImported, socketConnectImported);
                if (changed > 0)
                {
                    Console.WriteLine($"  [TCP] Patched {method.FullName}: {changed} TcpClient(host,port) calls replaced");
                    totalChanges += changed;
                }
            }
        }

        // ── Write output ─────────────────────────────────────────────────────
        Console.WriteLine();
        if (totalChanges == 0)
        {
            Console.WriteLine("No changes made.");
        }
        else
        {
            Console.WriteLine($"Total changes: {totalChanges}");
            string temp = output + ".tmp";
            asm.Write(temp);
            asm.Dispose();
            System.IO.File.Copy(temp, output, overwrite: true);
            System.IO.File.Delete(temp);
            Console.WriteLine($"Written to: {output}");
        }

        Console.WriteLine("Done.");
    }

    // ── TcpClient(host, port) patching ──────────────────────────────────────
    //
    // The pattern we want to replace is:
    //
    //   ldarg.0          (this)
    //   <load host>      (string — DnsSafeHost from uri)
    //   <load port>      (int32  — Port from uri)
    //   newobj TcpClient::.ctor(string, int32)
    //   stfld  _tcpClient
    //
    // We replace it with:
    //
    //   ldarg.0          (this — for the stfld that follows)
    //   <load host>      (string)
    //   call IPAddress.Parse(string)
    //   <load port>      (int32)
    //   newobj IPEndPoint::.ctor(IPAddress, int32)
    //   stloc  <endpointLocal>
    //   newobj TcpClient::.ctor()       [no-arg, no Dns]
    //   stfld  _tcpClient               [original stfld, now stores the no-arg TcpClient]
    //
    //   [then immediately after the stfld we inject:]
    //   ldarg.0
    //   ldfld  _tcpClient
    //   callvirt TcpClient.get_Client()
    //   ldloc  <endpointLocal>
    //   callvirt Socket.Connect(EndPoint)
    //
    // Because Cecil's IL list doesn't support mid-list insert easily, we:
    //   1. Replace the newobj with newobj TcpClient() (no-arg).
    //   2. Insert IPAddress.Parse + IPEndPoint ctor before the newobj (replacing the
    //      original host/port loaders isn't needed — we leave them in place and wrap).
    //   Actually: the simplest approach that doesn't disturb branches is to:
    //   - Replace the  `newobj TcpClient::.ctor(string,int32)` instruction with a call
    //     to a helper method we INJECT into the assembly.
    //
    // The injected helper:
    //   static TcpClient CreateTcpClient(string host, int port)
    //   {
    //       var tc = new TcpClient();
    //       tc.Client.Connect(new IPEndPoint(IPAddress.Parse(host), port));
    //       return tc;
    //   }
    //
    // That keeps the stack discipline identical to newobj (consumes host+port, produces TcpClient).

    static MethodDefinition _helperMethod = null;

    static MethodDefinition InjectHelper(
        ModuleDefinition module,
        MethodReference tcpCtorNoArg,
        MethodReference ipParse,
        MethodReference ipepCtor,
        MethodReference tcpGetClient,
        MethodReference socketConnect)
    {
        if (_helperMethod != null) return _helperMethod;

        // Find or create a helper type
        var helperType = module.Types.FirstOrDefault(t => t.Name == "<WSPatch>");
        if (helperType == null)
        {
            helperType = new TypeDefinition(
                "",
                "<WSPatch>",
                TypeAttributes.Class | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.Public,
                module.TypeSystem.Object);
            module.Types.Add(helperType);
        }

        var tcpClientType  = tcpCtorNoArg.DeclaringType;
        var ipEndPointType = ipepCtor.DeclaringType;
        var ipAddressType  = ipParse.DeclaringType;

        // Import IPAddress.Loopback static field
        var ipLoopbackField    = new FieldReference("Loopback", ipAddressType, ipAddressType);
        var ipLoopbackImported = module.ImportReference(ipLoopbackField);

        // Import String.op_Equality(string, string) -> bool
        var stringEqMethod = new MethodReference("op_Equality", module.TypeSystem.Boolean, module.TypeSystem.String)
            { HasThis = false };
        stringEqMethod.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));
        stringEqMethod.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));
        var stringEqImported = module.ImportReference(stringEqMethod);

        var meth = new MethodDefinition(
            "CreateTcpClient",
            MethodAttributes.Static | MethodAttributes.HideBySig | MethodAttributes.Public,
            tcpClientType);
        meth.Parameters.Add(new ParameterDefinition("host", ParameterAttributes.None, module.TypeSystem.String));
        meth.Parameters.Add(new ParameterDefinition("port", ParameterAttributes.None, module.TypeSystem.Int32));

        var body = meth.Body;
        body.InitLocals = true;

        // locals: [0] TcpClient tc, [1] IPEndPoint ep, [2] IPAddress ip
        body.Variables.Add(new VariableDefinition(tcpClientType));   // [0] tc
        body.Variables.Add(new VariableDefinition(ipEndPointType));  // [1] ep
        body.Variables.Add(new VariableDefinition(ipAddressType));   // [2] ip

        var proc = body.GetILProcessor();

        // Create label instructions first so we can reference them in branches
        var loopbackBranch = proc.Create(OpCodes.Ldsfld, ipLoopbackImported); // ip = IPAddress.Loopback
        var afterResolve   = proc.Create(OpCodes.Ldloc_2);                    // load ip for IPEndPoint ctor

        // if (host == "localhost") goto loopbackBranch
        proc.Emit(OpCodes.Ldarg_0);
        proc.Emit(OpCodes.Ldstr, "localhost");
        proc.Emit(OpCodes.Call, stringEqImported);
        proc.Emit(OpCodes.Brtrue_S, loopbackBranch);

        // if (host == "127.0.0.1") goto loopbackBranch
        proc.Emit(OpCodes.Ldarg_0);
        proc.Emit(OpCodes.Ldstr, "127.0.0.1");
        proc.Emit(OpCodes.Call, stringEqImported);
        proc.Emit(OpCodes.Brtrue_S, loopbackBranch);

        // else: ip = IPAddress.Parse(host)
        proc.Emit(OpCodes.Ldarg_0);
        proc.Emit(OpCodes.Call, ipParse);
        proc.Emit(OpCodes.Stloc_2);
        proc.Emit(OpCodes.Br_S, afterResolve);

        // loopbackBranch: ip = IPAddress.Loopback; stloc_2
        proc.Append(loopbackBranch);              // ldsfld IPAddress.Loopback
        proc.Emit(OpCodes.Stloc_2);

        // afterResolve: ep = new IPEndPoint(ip, port)
        proc.Append(afterResolve);                // ldloc.2  (ip)
        proc.Emit(OpCodes.Ldarg_1);              // port
        proc.Emit(OpCodes.Newobj, ipepCtor);     // new IPEndPoint(ip, port)
        proc.Emit(OpCodes.Stloc_1);             // ep = ...

        // tc = new TcpClient()
        proc.Emit(OpCodes.Newobj, tcpCtorNoArg);
        proc.Emit(OpCodes.Stloc_0);

        // tc.Client.Connect(ep)
        proc.Emit(OpCodes.Ldloc_0);
        proc.Emit(OpCodes.Callvirt, tcpGetClient);
        proc.Emit(OpCodes.Ldloc_1);
        proc.Emit(OpCodes.Callvirt, socketConnect);

        // return tc
        proc.Emit(OpCodes.Ldloc_0);
        proc.Emit(OpCodes.Ret);

        helperType.Methods.Add(meth);
        _helperMethod = meth;
        Console.WriteLine($"  [TCP] Injected helper method {meth.FullName}");
        return meth;
    }

    static int PatchTcpClientCtor(
        MethodDefinition method,
        MethodReference tcpCtorNoArg,
        MethodReference ipParse,
        MethodReference ipepCtor,
        MethodReference tcpGetClient,
        MethodReference socketConnect)
    {
        var body = method.Body;
        var il   = body.Instructions;
        int count = 0;

        // Find all newobj TcpClient::.ctor(string, int32)
        var targets = il.Where(i =>
            i.OpCode == OpCodes.Newobj &&
            i.Operand is MethodReference mr &&
            mr.DeclaringType.FullName == "System.Net.Sockets.TcpClient" &&
            mr.Parameters.Count == 2 &&
            mr.Parameters[0].ParameterType.FullName == "System.String" &&
            mr.Parameters[1].ParameterType.FullName == "System.Int32")
            .ToList();

        if (targets.Count == 0) return 0;

        // Inject helper into the module (once)
        var helperMeth = InjectHelper(method.Module, tcpCtorNoArg, ipParse, ipepCtor, tcpGetClient, socketConnect);
        var helperRef  = method.Module.ImportReference(helperMeth);

        foreach (var instr in targets)
        {
            // Replace newobj TcpClient(string,int32) with call CreateTcpClient(string,int32)
            // Stack in:  ..., host (string), port (int32)
            // Stack out: ..., TcpClient
            // — identical to newobj, so no further changes needed.
            Console.WriteLine($"    Replacing newobj TcpClient(string,int32) in {method.Name} @ IL_{il.IndexOf(instr):x4}");
            instr.OpCode  = OpCodes.Call;
            instr.Operand = helperRef;
            count++;
        }

        return count;
    }

    // ── SslStream patching ───────────────────────────────────────────────────

    static IEnumerable<TypeDefinition> AllTypes(TypeDefinition t)
    {
        yield return t;
        foreach (var n in t.NestedTypes)
            foreach (var x in AllTypes(n))
                yield return x;
    }

    static int PatchSslStream(MethodDefinition method)
    {
        var body = method.Body;
        var il   = body.Instructions;
        int count = 0;

        var sslNewobjs = il
            .Where(i => i.OpCode == OpCodes.Newobj &&
                        i.Operand is MethodReference mr &&
                        mr.DeclaringType.FullName == "System.Net.Security.SslStream")
            .ToList();

        if (sslNewobjs.Count == 0) return 0;

        foreach (var newobj in sslNewobjs)
        {
            int newobjIdx = il.IndexOf(newobj);
            if (newobjIdx < 0) continue;

            Instruction guardBranch    = null;
            int         guardBranchIdx = -1;

            for (int i = 0; i < newobjIdx; i++)
            {
                var instr = il[i];
                if (instr.OpCode != OpCodes.Brfalse   && instr.OpCode != OpCodes.Brfalse_S &&
                    instr.OpCode != OpCodes.Brtrue    && instr.OpCode != OpCodes.Brtrue_S)
                    continue;

                var target    = (Instruction)instr.Operand;
                int targetIdx = il.IndexOf(target);

                if (targetIdx > newobjIdx)
                {
                    guardBranch    = instr;
                    guardBranchIdx = i;
                }
            }

            if (guardBranch == null)
            {
                Console.WriteLine($"    [SSL] WARNING: no guarding branch for newobj SslStream at {newobjIdx} in {method.Name}; nop'ing window");
                int fb = Math.Max(0, newobjIdx - 10);
                int fe = Math.Min(il.Count - 1, newobjIdx + 20);
                for (int i = fb; i <= fe; i++) { il[i].OpCode = OpCodes.Nop; il[i].Operand = null; count++; }
                continue;
            }

            var blockTarget    = (Instruction)guardBranch.Operand;
            int blockTargetIdx = il.IndexOf(blockTarget);
            int blockStart     = guardBranchIdx + 1;
            int blockEnd       = blockTargetIdx - 1;

            Console.WriteLine($"    [SSL] Block IL[{guardBranchIdx}]({guardBranch.OpCode})->IL[{blockTargetIdx}], nop [{blockStart}..{blockEnd}]");

            for (int i = blockStart; i <= blockEnd && i < il.Count; i++)
            {
                il[i].OpCode  = OpCodes.Nop;
                il[i].Operand = null;
                count++;
            }

            guardBranch.OpCode  = OpCodes.Nop;
            guardBranch.Operand = null;
            count++;

            if (guardBranchIdx > 0)
            {
                var condLoader = il[guardBranchIdx - 1];
                if (condLoader.OpCode == OpCodes.Ldfld  || condLoader.OpCode == OpCodes.Ldflda ||
                    condLoader.OpCode == OpCodes.Ldarg  || condLoader.OpCode == OpCodes.Ldarg_0 ||
                    condLoader.OpCode == OpCodes.Ldarg_1 || condLoader.OpCode == OpCodes.Ldarg_S ||
                    condLoader.OpCode == OpCodes.Ldloc  || condLoader.OpCode == OpCodes.Ldloc_0 ||
                    condLoader.OpCode == OpCodes.Ldloc_1 || condLoader.OpCode == OpCodes.Ldloc_2 ||
                    condLoader.OpCode == OpCodes.Ldloc_3 || condLoader.OpCode == OpCodes.Ldloc_S)
                {
                    condLoader.OpCode  = OpCodes.Nop;
                    condLoader.Operand = null;
                    count++;

                    if (guardBranchIdx >= 2)
                    {
                        var storer = il[guardBranchIdx - 2];
                        if (storer.OpCode == OpCodes.Stloc   || storer.OpCode == OpCodes.Stloc_0 ||
                            storer.OpCode == OpCodes.Stloc_1 || storer.OpCode == OpCodes.Stloc_2 ||
                            storer.OpCode == OpCodes.Stloc_3 || storer.OpCode == OpCodes.Stloc_S)
                        {
                            storer.OpCode  = OpCodes.Nop;
                            storer.Operand = null;
                            count++;
                        }
                    }
                }
            }
        }

        // Replace SslStream locals with System.Object
        foreach (var local in body.Variables)
        {
            if (local.VariableType.FullName == "System.Net.Security.SslStream")
            {
                Console.WriteLine($"    [SSL] Replacing local[{local.Index}] SslStream -> object in {method.Name}");
                local.VariableType = method.Module.TypeSystem.Object;
                count++;
            }
        }

        return count;
    }
}
