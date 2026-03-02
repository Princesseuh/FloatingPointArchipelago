using System;
using System.IO;
using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace BepInExFix
{
    /// <summary>
    /// Custom entrypoint that patches BepInEx to remove TraceLogSource before it initializes
    /// This must be set as the doorstop target_assembly
    /// </summary>
    public static class Entrypoint
    {
        public static void Start()
        {
            try
            {
                Console.WriteLine("[BepInExFix] Starting BepInEx patch...");

                string bepinexPath = Path.Combine(
                    Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),
                    "BepInEx", "core", "BepInEx.dll"
                );

                // Load BepInEx assembly with Cecil
                var assembly = AssemblyDefinition.ReadAssembly(bepinexPath);

                // Find TraceLogSource type
                TypeDefinition traceLogSourceType = null;
                foreach (var type in assembly.MainModule.Types)
                {
                    if (type.FullName == "BepInEx.Logging.TraceLogSource")
                    {
                        traceLogSourceType = type;
                        break;
                    }
                }

                if (traceLogSourceType != null)
                {
                    Console.WriteLine("[BepInExFix] Found TraceLogSource, patching...");

                    // Find and patch CreateSource method
                    foreach (var method in traceLogSourceType.Methods)
                    {
                        if (method.Name == "CreateSource" && method.HasBody)
                        {
                            var processor = method.Body.GetILProcessor();
                            method.Body.Instructions.Clear();
                            method.Body.Variables.Clear();
                            
                            // Return null
                            processor.Emit(OpCodes.Ldnull);
                            processor.Emit(OpCodes.Ret);
                            
                            Console.WriteLine("[BepInExFix] Patched CreateSource method");
                        }
                    }
                }

                // Save patched assembly to memory
                var memoryStream = new MemoryStream();
                assembly.Write(memoryStream);
                memoryStream.Position = 0;

                // Load the patched assembly
                var patchedAssembly = Assembly.Load(memoryStream.ToArray());

                Console.WriteLine("[BepInExFix] Patch complete, starting BepInEx preloader...");

                // Find and invoke BepInEx's Preloader.Run method
                var preloaderType = patchedAssembly.GetType("BepInEx.Preloader.Preloader");
                if (preloaderType != null)
                {
                    var runMethod = preloaderType.GetMethod("Run", BindingFlags.Public | BindingFlags.Static);
                    if (runMethod != null)
                    {
                        runMethod.Invoke(null, null);
                    }
                    else
                    {
                        Console.WriteLine("[BepInExFix] Could not find Preloader.Run method");
                    }
                }
                else
                {
                    Console.WriteLine("[BepInExFix] Could not find Preloader type");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BepInExFix] Error: {ex}");
            }
        }
    }
}
