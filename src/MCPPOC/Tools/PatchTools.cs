using System.ComponentModel;
using dnlib.DotNet;
using MCPPOC.Analysis;
using MCPPOC.Workspace;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace MCPPOC.Tools;

/// <summary>Tools that modify assemblies. Not registered when the server runs with --read-only.</summary>
[McpServerToolType]
public sealed class PatchTools(AssemblyWorkspace workspace, DecompilerService decompiler)
{
    private const string IlSyntax =
        "One instruction per line, ilasm/ILSpy syntax or the format printed by 'get_method_il'. Examples: 'ldstr \"hi\"', 'ldc.i4 42', " +
        "'ldarg.1', 'ldloc V_0' (or a local/parameter name), 'call void [System.Console]System.Console::WriteLine(string)', " +
        "'ldfld int32 Ns.Type::field', 'newobj instance void Ns.Type::.ctor()', 'brtrue IL_0010' (original offset) or 'br #3' (3rd new instruction, 0-based), " +
        "'switch (IL_0010, IL_0020)'. '//' starts a comment.";

    [McpServerTool(Name = "patch_method_il", Title = "Patch method IL", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Replace, insert or delete IL instructions in a method (in memory; call 'save_assembly' to write to disk). Branches and exception handlers that pointed at removed instructions are redirected to the replacement, branch sizes are re-optimized and MaxStack recomputed. Returns the IL around the patch.")]
    public PatchResult PatchMethodIl(
        [Description(P.Method)] string method,
        [Description("IL offset of the first instruction to replace/insert before (e.g. 'IL_000C', '0xC' or 12).")] string offset,
        [Description(IlSyntax + " Empty to only delete.")] string instructions,
        [Description("How many existing instructions to remove starting at 'offset'. Default: as many as are inserted (overwrite). 0 = pure insert.")] int? removeCount = null,
        [Description(P.Assembly)] string? assembly = null) =>
        Modify(assembly, method, (a, m) =>
            MethodPatcher.Replace(m, a.Parser, ParseOffset(offset), instructions ?? "", removeCount));

    [McpServerTool(Name = "replace_method_body", Title = "Replace whole method body", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Replace the entire IL body of a method (e.g. 'ldc.i4.1\\nret' to make a license check always succeed). Exception handlers are removed. Optionally redefine the local variables.")]
    public PatchResult ReplaceMethodBody(
        [Description(P.Method)] string method,
        [Description(IlSyntax)] string instructions,
        [Description("Optional new local variable types in order, e.g. ['int32','string']. Omit to keep the existing locals.")] string[]? locals = null,
        [Description(P.Assembly)] string? assembly = null) =>
        Modify(assembly, method, (a, m) => MethodPatcher.ReplaceBody(m, a.Parser, instructions, locals));

    [McpServerTool(Name = "nop_instructions", Title = "NOP out instructions", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Replace 'count' instructions starting at 'offset' with nop (keeps branch targets valid). Make sure the evaluation stack stays balanced.")]
    public PatchResult NopInstructions(
        [Description(P.Method)] string method,
        [Description("IL offset of the first instruction (e.g. 'IL_000C').")] string offset,
        [Description("Number of instructions to nop.")] int count = 1,
        [Description(P.Assembly)] string? assembly = null) =>
        Modify(assembly, method, (_, m) => MethodPatcher.Nop(m, ParseOffset(offset), count));

    [McpServerTool(Name = "rename_member", Title = "Rename member (deobfuscation)", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Rename a type, method, field, property or event to a meaningful name. Ideal while deobfuscating: decompiled output immediately uses the new name. Optionally change a type's namespace.")]
    public string RenameMember(
        [Description(P.Member)] string member,
        [Description("New name.")] string newName,
        [Description("For types only: optional new namespace.")] string? newNamespace = null,
        [Description(P.Assembly)] string? assembly = null)
    {
        if (string.IsNullOrWhiteSpace(newName))
            throw new McpException("newName must not be empty.");
        var asm = workspace.Get(assembly);
        var message = asm.Write(a =>
        {
            var def = MemberResolver.ResolveMember(a, member);
            var old = def.FullName;
            def.Name = newName;
            if (newNamespace != null)
            {
                if (def is not TypeDef t)
                    throw new McpException("newNamespace can only be used with types.");
                t.Namespace = newNamespace;
            }
            return $"Renamed {old} -> {def.FullName} ({ModelFactory.Hex(def.MDToken)}).";
        });
        decompiler.Invalidate(asm.Id);
        return message;
    }

    [McpServerTool(Name = "save_assembly", Title = "Save assembly to disk", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Write the (patched) assembly to disk. Metadata tokens are preserved. Mixed-mode assemblies are written with their native code. Refuses to overwrite an existing file unless 'overwrite' is true.")]
    public SaveResult SaveAssembly(
        [Description("Absolute output path, e.g. C:\\work\\app.patched.dll.")] string path,
        [Description("Allow overwriting an existing file (including the original).")] bool overwrite = false,
        [Description(P.Assembly)] string? assembly = null)
    {
        var full = Path.GetFullPath(path);
        if (File.Exists(full) && !overwrite)
            throw new McpException($"'{full}' already exists. Pass overwrite=true to replace it.");
        return workspace.Get(assembly).Read(a =>
        {
            using var ms = new MemoryStream();
            bool native;
            IReadOnlyList<string> warnings;
            try
            {
                native = ModuleWriting.Write(a.Module, ms, out warnings);
            }
            catch (Exception ex)
            {
                throw new McpException($"Failed to write assembly: {ex.Message}");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, ms.ToArray());
            return new SaveResult(full, ms.Length, native, warnings);
        });
    }

    private PatchResult Modify(string? assembly, string method, Func<LoadedAssembly, MethodDef, string> action)
    {
        var asm = workspace.Get(assembly);
        var result = asm.Write(a =>
        {
            var m = MemberResolver.ResolveMethod(a, method);
            string message;
            try
            {
                message = action(a, m);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or OverflowException or FormatException)
            {
                throw new McpException($"Patch failed: {ex.Message}");
            }
            var window = m.Body.Instructions.Take(200)
                .Select(i => new IlInstruction(ModelFactory.Il(i.Offset), null, null, i.OpCode.Name, ModelFactory.FormatOperand(i)))
                .ToList();
            return new PatchResult(m.FullName, message, m.Body.Instructions.Count, window);
        });
        decompiler.Invalidate(asm.Id);
        return result;
    }

    private static uint ParseOffset(string offset)
    {
        var t = offset.Trim();
        if (t.StartsWith("IL_", StringComparison.OrdinalIgnoreCase))
            t = "0x" + t[3..];
        if (!MemberResolver.TryParseToken(t, out var value))
            throw new McpException($"Invalid IL offset '{offset}'.");
        return value;
    }
}
