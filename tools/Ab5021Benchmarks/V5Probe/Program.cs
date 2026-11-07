using Mono.Cecil;
using Mono.Cecil.Cil;
using System.Security.Cryptography;
using System.Text.Json;

// Offline observers on the pinned released package; no original instruction is removed.
var path = Path.GetFullPath(args[0]);
var originalHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
using var assembly = AssemblyDefinition.ReadAssembly(path);
using var probe = AssemblyDefinition.ReadAssembly(args[1]);
var module = assembly.MainModule;
var probeType = probe.MainModule.GetType("LiteDB.Engine.CheckpointProfile");
MethodReference Hook(string name) => module.ImportReference(probeType.Methods.Single(m => m.Name == name));
TypeDefinition Type(string name) => module.GetType("LiteDB.Engine." + name);
void Before(MethodDefinition method, Instruction target, params Instruction[] instructions)
{
    var il = method.Body.GetILProcessor();
    foreach (var instruction in instructions) il.InsertBefore(target, instruction);
}
// Return branches must pass through the probe, including early no-op returns.
void AtReturns(MethodDefinition method, string hook)
{
    foreach (var ret in method.Body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToArray())
    {
        ret.OpCode = OpCodes.Dup;
        var il = method.Body.GetILProcessor();
        var call = Instruction.Create(OpCodes.Call, Hook(hook));
        il.InsertAfter(ret, call);
        il.InsertAfter(call, Instruction.Create(OpCodes.Ret));
    }
    method.Body.MaxStackSize += 2;
}
foreach (var name in new[] { "TryCheckpoint", "Checkpoint" })
{
    var method = Type("WalIndexService").Methods.Single(m => m.Name == name);
    Before(method, method.Body.Instructions[0], Instruction.Create(OpCodes.Ldstr, name == "TryCheckpoint" ? "auto" : "explicit-or-close"),
        Instruction.Create(OpCodes.Call, Hook("Begin")));
    AtReturns(method, "V5End");
}
var checkpoint = Type("WalIndexService").Methods.Single(m => m.Name == "CheckpointInternal");
Before(checkpoint, checkpoint.Body.Instructions[0], Instruction.Create(OpCodes.Ldstr, "copy-and-data-sync"), Instruction.Create(OpCodes.Call, Hook("Mark")));
foreach (var call in checkpoint.Body.Instructions.Where(i => i.Operand is MethodReference m && m.Name == "Clear").ToArray())
    Before(checkpoint, call, Instruction.Create(OpCodes.Ldstr, "reclaim"), Instruction.Create(OpCodes.Call, Hook("Mark")));
var write = Type("DiskService").Methods.Single(m => m.Name == "WriteDataDisk");
foreach (var call in write.Body.Instructions.Where(i => i.Operand is MethodReference m && m.Name == "Write" && m.DeclaringType.FullName == "System.IO.Stream").ToArray())
{
    var il = write.Body.GetILProcessor();
    var bytes = Instruction.Create(OpCodes.Ldc_I4, 8192);
    il.InsertAfter(call, bytes);
    il.InsertAfter(bytes, Instruction.Create(OpCodes.Call, Hook("DataWrite")));
}
foreach (var call in write.Body.Instructions.Where(i => i.Operand is MethodReference m && m.Name == "FlushToDisk").ToArray())
    Before(write, call, Instruction.Create(OpCodes.Ldstr, "data-sync"), Instruction.Create(OpCodes.Call, Hook("Mark")));
var flush = module.GetType("LiteDB.StreamExtensions").Methods.Single(m => m.Name == "FlushToDisk");
// Probe the actual FileStream.Flush(true) branch; custom stream Flush() is not a device sync.
foreach (var call in flush.Body.Instructions.Where(i => i.Operand is MethodReference m && m.Name == "Flush" && m.Parameters.Count == 1).ToArray())
{
    var file = flush.Body.Variables.Single(v => v.VariableType.FullName == "System.IO.FileStream");
    var getter = new MethodReference("get_Name", module.TypeSystem.String, file.VariableType) { HasThis = true };
    Before(flush, call, Instruction.Create(OpCodes.Ldloc, file), Instruction.Create(OpCodes.Callvirt, getter), Instruction.Create(OpCodes.Call, Hook("NativeSync")));
    flush.Body.MaxStackSize += 2;
}
// Insertions can grow a short branch beyond its signed-byte displacement.
var opcodes = typeof(OpCodes).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
    .Where(f => f.FieldType == typeof(OpCode)).Select(f => (OpCode)f.GetValue(null)!).ToDictionary(o => o.Name);
foreach (var type in module.Types)
    foreach (var method in type.Methods.Where(m => m.HasBody))
        foreach (var instruction in method.Body.Instructions)
            if (instruction.OpCode.OperandType == OperandType.ShortInlineBrTarget)
                instruction.OpCode = opcodes[instruction.OpCode.Name[..^2]];
var temporary = path + ".probe";
assembly.Write(temporary);
assembly.Dispose(); // Release the input handle before replacement on Windows.
File.Move(temporary, path, true);
File.WriteAllText(args[2], JsonSerializer.Serialize(new { originalHash, instrumentedHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
    package = "LiteDB [5.0.21]", note = "Observation-only IL insertions; timings include instrumentation overhead" }, new JsonSerializerOptions { WriteIndented = true }));
// Compile the observed methods without executing a workload; reject invalid probe IL.
System.Reflection.Assembly.LoadFrom(Path.GetFullPath(args[1]));
var observed = System.Reflection.Assembly.LoadFrom(path);
foreach (var pair in new[] { ("LiteDB.Engine.WalIndexService", "TryCheckpoint"),
    ("LiteDB.Engine.WalIndexService", "Checkpoint"), ("LiteDB.Engine.WalIndexService", "CheckpointInternal"),
    ("LiteDB.Engine.DiskService", "WriteDataDisk"), ("LiteDB.StreamExtensions", "FlushToDisk") })
{
    var method = observed.GetType(pair.Item1)!.GetMethod(pair.Item2,
        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static)!;
    System.Runtime.CompilerServices.RuntimeHelpers.PrepareMethod(method.MethodHandle);
}
Console.WriteLine("Probe IL passed JIT validation; no workload executed.");
