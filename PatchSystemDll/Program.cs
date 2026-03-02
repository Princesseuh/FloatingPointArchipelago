using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

/// <summary>
/// Patches the Unity 4.3 game's System.dll to fix broken static constructors
/// for System.Net.Sockets.Socket and System.Net.Dns on Windows.
///
/// The Socket..cctor tries to create IPv4/IPv6 sockets to detect support,
/// which fails on this old Mono. We replace it with a hardcoded stub:
///   ipv4Supported = 1 (yes)
///   ipv6Supported = 0 (no, keep it simple)
///
/// The Dns..cctor similarly fails. We replace it with a no-op.
/// </summary>
class Program
{
    static void Main(string[] args)
    {
        string gameManaged = @"C:\Program Files (x86)\Steam\steamapps\common\Floating Point\Floating Point_Data\Managed";
        string input  = args.Length > 0 ? args[0] : System.IO.Path.Combine(gameManaged, "System.dll");
        string output = args.Length > 1 ? args[1] : input;

        Console.WriteLine("=== System.dll Static Constructor Patcher ===");
        Console.WriteLine($"Input:  {input}");
        Console.WriteLine($"Output: {output}");
        Console.WriteLine();

        // Read with write access; use a resolver that finds mscorlib next to it
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(System.IO.Path.GetDirectoryName(input));

        var readerParams = new ReaderParameters
        {
            ReadWrite           = true,
            AssemblyResolver    = resolver,
            ReadSymbols         = false,
        };

        var asm = AssemblyDefinition.ReadAssembly(input, readerParams);
        int totalChanges = 0;

        // ── Patch Socket..cctor ──────────────────────────────────────────────
        var socketType = asm.MainModule.Types
            .FirstOrDefault(t => t.FullName == "System.Net.Sockets.Socket");

        if (socketType == null)
        {
            Console.WriteLine("WARNING: System.Net.Sockets.Socket not found in assembly!");
        }
        else
        {
            var cctor = socketType.Methods.FirstOrDefault(m => m.IsConstructor && m.IsStatic);
            if (cctor == null)
            {
                Console.WriteLine("WARNING: Socket..cctor not found!");
            }
            else
            {
                Console.WriteLine($"  Found Socket..cctor with {cctor.Body.Instructions.Count} instructions");

                // Find the static fields ipv4Supported and ipv6Supported
                var ipv4Field = socketType.Fields.FirstOrDefault(f => f.Name == "ipv4Supported");
                var ipv6Field = socketType.Fields.FirstOrDefault(f => f.Name == "ipv6Supported");

                if (ipv4Field == null || ipv6Field == null)
                {
                    Console.WriteLine($"WARNING: Could not find ipv4Supported ({ipv4Field != null}) or ipv6Supported ({ipv6Field != null})");
                    Console.WriteLine("  Replacing entire cctor with just ret...");
                    StubVoid(cctor);
                }
                else
                {
                    Console.WriteLine($"  Replacing Socket..cctor with hardcoded ipv4=1, ipv6=0");
                    var body = cctor.Body;
                    body.Instructions.Clear();
                    body.Variables.Clear();
                    body.ExceptionHandlers.Clear();
                    body.InitLocals = false;

                    var proc = body.GetILProcessor();
                    // ipv4Supported = 1
                    proc.Emit(OpCodes.Ldc_I4_1);
                    proc.Emit(OpCodes.Stsfld, ipv4Field);
                    // ipv6Supported = 0
                    proc.Emit(OpCodes.Ldc_I4_0);
                    proc.Emit(OpCodes.Stsfld, ipv6Field);
                    proc.Emit(OpCodes.Ret);
                    Console.WriteLine("  Socket..cctor patched OK");
                }
                totalChanges++;
            }
        }

        // ── Patch Dns..cctor ────────────────────────────────────────────────
        var dnsType = asm.MainModule.Types
            .FirstOrDefault(t => t.FullName == "System.Net.Dns");

        if (dnsType == null)
        {
            Console.WriteLine("NOTE: System.Net.Dns not found (may be in a nested namespace) — skipping");
        }
        else
        {
            var dnsCctor = dnsType.Methods.FirstOrDefault(m => m.IsConstructor && m.IsStatic);
            if (dnsCctor == null)
            {
                Console.WriteLine("NOTE: Dns has no static constructor — nothing to patch");
            }
            else
            {
                Console.WriteLine($"  Replacing Dns..cctor with ret (was {dnsCctor.Body.Instructions.Count} instructions)");
                StubVoid(dnsCctor);
                Console.WriteLine("  Dns..cctor patched OK");
                totalChanges++;
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
            Console.WriteLine($"Total types patched: {totalChanges}");
            string temp = output + ".tmp";
            asm.Write(temp);
            asm.Dispose();
            System.IO.File.Copy(temp, output, overwrite: true);
            System.IO.File.Delete(temp);
            Console.WriteLine($"Written to: {output}");
        }

        Console.WriteLine("Done.");
    }

    static void StubVoid(MethodDefinition method)
    {
        var body = method.Body;
        body.Instructions.Clear();
        body.Variables.Clear();
        body.ExceptionHandlers.Clear();
        body.InitLocals = false;
        var proc = body.GetILProcessor();
        proc.Emit(OpCodes.Ret);
    }
}
