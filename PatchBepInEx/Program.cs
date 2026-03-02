using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

class Program
{
    static void Main(string[] args)
    {
        string gamePath = args.Length > 0
            ? args[0]
            : @"C:\Program Files (x86)\Steam\steamapps\common\Floating Point";
        string preloaderDllPath = System.IO.Path.Combine(gamePath, "BepInEx", "core", "BepInEx.Preloader.dll");
        string bepinexDllPath   = System.IO.Path.Combine(gamePath, "BepInEx", "core", "BepInEx.dll");

        Console.WriteLine("=== BepInEx TraceLogSource Patcher v3 ===");
        Console.WriteLine();

        // Patch BepInEx.Preloader.dll — Preloader.Run
        PatchFile(preloaderDllPath, "BepInEx.Preloader.Preloader", "Run");

        // Patch BepInEx.dll — Chainloader.Initialize
        PatchFile(bepinexDllPath, "BepInEx.Bootstrap.Chainloader", "Initialize");

        Console.WriteLine();
        Console.WriteLine("All done!");
    }

    static void PatchFile(string dllPath, string typeName, string methodName)
    {
        Console.WriteLine($"--- Patching {System.IO.Path.GetFileName(dllPath)} :: {typeName}.{methodName} ---");
        try
        {
            var assembly = AssemblyDefinition.ReadAssembly(dllPath);
            var type = assembly.MainModule.Types.FirstOrDefault(t => t.FullName == typeName);
            if (type == null)
            {
                Console.WriteLine($"  ERROR: Type '{typeName}' not found.");
                return;
            }

            var method = type.Methods.FirstOrDefault(m => m.Name == methodName);
            if (method == null || !method.HasBody)
            {
                Console.WriteLine($"  ERROR: Method '{methodName}' not found or has no body.");
                return;
            }

            Console.WriteLine($"  Found {typeName}.{methodName}");
            RemoveTraceLogSourceSequences(method);

            string tempPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.IO.Path.GetFileName(dllPath) + "_patched.dll");
            assembly.Write(tempPath);
            assembly.Dispose();
            Console.WriteLine($"  Saved to: {tempPath}");

            try
            {
                System.IO.File.Copy(tempPath, dllPath, true);
                Console.WriteLine($"  Success! Patched {System.IO.Path.GetFileName(dllPath)} in place.");
            }
            catch (Exception copyEx)
            {
                Console.WriteLine($"  Could not copy automatically: {copyEx.Message}");
                Console.WriteLine($"  Please copy manually:");
                Console.WriteLine($"    copy \"{tempPath}\" \"{dllPath}\"");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ERROR: {ex}");
        }
        Console.WriteLine();
    }

    static void RemoveTraceLogSourceSequences(MethodDefinition method)
    {
        var instructions = method.Body.Instructions;
        var processor    = method.Body.GetILProcessor();
        var toRemove     = new List<Instruction>();
        var toRemoveSet  = new HashSet<Instruction>();

        for (int i = 0; i < instructions.Count; i++)
        {
            var instr = instructions[i];
            if (toRemoveSet.Contains(instr)) continue;

            // Look for a call to any TraceLogSource method (get_IsListening or CreateSource)
            bool isTraceCall = instr.OpCode == OpCodes.Call &&
                               instr.Operand is MethodReference mr &&
                               mr.DeclaringType.FullName.Contains("TraceLogSource");

            // Also match ldc.i4.0 that was a previous partial-patch replacement of get_IsListening,
            // when followed immediately by a brtrue/brfalse and then a get_Sources call.
            bool isPrevPatchReplacement = false;
            if (!isTraceCall &&
                (instr.OpCode == OpCodes.Ldc_I4_0 || instr.OpCode == OpCodes.Ldc_I4_1) &&
                i + 1 < instructions.Count)
            {
                var nextInstr = instructions[i + 1];
                bool isBranch = nextInstr.OpCode == OpCodes.Brtrue_S ||
                                nextInstr.OpCode == OpCodes.Brfalse_S ||
                                nextInstr.OpCode == OpCodes.Brtrue   ||
                                nextInstr.OpCode == OpCodes.Brfalse;
                if (isBranch && i + 2 < instructions.Count)
                {
                    var afterBranch = instructions[i + 2];
                    isPrevPatchReplacement = afterBranch.OpCode == OpCodes.Call &&
                                            afterBranch.Operand is MethodReference mr2 &&
                                            mr2.DeclaringType.FullName.Contains("Logger") &&
                                            mr2.Name.Contains("get_Sources");
                }
            }

            if (isTraceCall && ((MethodReference)instr.Operand).Name == "get_IsListening")
            {
                // Pattern:
                //   call get_IsListening()   <- bool result
                //   brtrue.s/brfalse.s       <- conditional branch
                //   call get_Sources()       <- load collection (only if IsListening)
                //   call CreateSource()
                //   callvirt Add()
                // Remove all 5 so the stack stays balanced.
                Console.WriteLine($"  Found get_IsListening at index {i} — removing full TraceLogSource block");
                toRemove.Add(instr); // get_IsListening

                int j = i + 1;
                // branch instruction
                if (j < instructions.Count)
                {
                    var branch = instructions[j];
                    if (branch.OpCode == OpCodes.Brtrue_S || branch.OpCode == OpCodes.Brfalse_S ||
                        branch.OpCode == OpCodes.Brtrue   || branch.OpCode == OpCodes.Brfalse)
                    {
                        Console.WriteLine($"  Removing branch at index {j}");
                        toRemove.Add(branch);
                        j++;
                    }
                }
                // get_Sources
                if (j < instructions.Count)
                {
                    var src = instructions[j];
                    if (src.OpCode == OpCodes.Call && src.Operand is MethodReference srcRef &&
                        srcRef.Name.Contains("get_Sources"))
                    {
                        Console.WriteLine($"  Removing get_Sources at index {j}");
                        toRemove.Add(src);
                        j++;
                    }
                }
                // CreateSource
                if (j < instructions.Count)
                {
                    var create = instructions[j];
                    if (create.OpCode == OpCodes.Call && create.Operand is MethodReference createRef &&
                        createRef.Name == "CreateSource")
                    {
                        Console.WriteLine($"  Removing CreateSource at index {j}");
                        toRemove.Add(create);
                        j++;
                    }
                }
                // Add
                if (j < instructions.Count)
                {
                    var add = instructions[j];
                    if (add.OpCode == OpCodes.Callvirt && add.Operand is MethodReference addRef &&
                        addRef.Name == "Add")
                    {
                        Console.WriteLine($"  Removing Add at index {j}");
                        toRemove.Add(add);
                    }
                }
                continue;
            }

            if (isPrevPatchReplacement)
            {
                // Previous partial patch left: ldc.i4.0, brtrue.s, get_Sources (orphaned)
                // Remove all three.
                Console.WriteLine($"  Found previous-patch remnant at index {i} — removing ldc.i4, branch, get_Sources");
                toRemove.Add(instr);                    // ldc.i4.0
                toRemove.Add(instructions[i + 1]);      // brtrue.s / brfalse.s
                toRemove.Add(instructions[i + 2]);      // get_Sources
                // Also check for CreateSource and Add right after
                int j = i + 3;
                if (j < instructions.Count)
                {
                    var create = instructions[j];
                    if (create.OpCode == OpCodes.Call && create.Operand is MethodReference createRef &&
                        createRef.Name == "CreateSource")
                    {
                        Console.WriteLine($"  Removing CreateSource at index {j}");
                        toRemove.Add(create);
                        j++;
                    }
                }
                if (j < instructions.Count)
                {
                    var add = instructions[j];
                    if (add.OpCode == OpCodes.Callvirt && add.Operand is MethodReference addRef &&
                        addRef.Name == "Add")
                    {
                        Console.WriteLine($"  Removing Add at index {j}");
                        toRemove.Add(add);
                    }
                }
                continue;
            }

            if (isTraceCall && ((MethodReference)instr.Operand).Name == "CreateSource")
            {
                // Standalone CreateSource (no IsListening guard) — remove it plus surrounding ldsfld/Add
                Console.WriteLine($"  Found standalone CreateSource at index {i}");
                toRemove.Add(instr);
                if (i > 0 && instructions[i - 1].OpCode == OpCodes.Ldsfld)
                {
                    Console.WriteLine($"  Removing preceding ldsfld at index {i - 1}");
                    toRemove.Add(instructions[i - 1]);
                }
                if (i + 1 < instructions.Count)
                {
                    var next = instructions[i + 1];
                    if (next.OpCode == OpCodes.Callvirt &&
                        next.Operand is MethodReference addRef && addRef.Name == "Add")
                    {
                        Console.WriteLine($"  Removing Add at index {i + 1}");
                        toRemove.Add(next);
                    }
                }
                continue;
            }
        }

        // Deduplicate (in case the loop added the same instruction via multiple paths)
        var seen = new HashSet<Instruction>();
        var dedupedRemove = new List<Instruction>();
        foreach (var instr in toRemove)
            if (seen.Add(instr))
                dedupedRemove.Add(instr);

        foreach (var instr in dedupedRemove)
            processor.Remove(instr);

        Console.WriteLine($"  Removed {dedupedRemove.Count} instructions.");
    }
}
