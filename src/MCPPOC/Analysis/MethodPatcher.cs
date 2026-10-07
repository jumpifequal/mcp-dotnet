using System.Globalization;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Writer;

namespace MCPPOC.Analysis;

/// <summary>
/// Applies IL edits to a method body while keeping it consistent: branch targets and exception handler
/// boundaries that pointed at removed instructions are redirected, short branches are re-optimized after
/// offsets change, and MaxStack is recomputed.
/// </summary>
public static class MethodPatcher
{
    /// <summary>
    /// Replaces <paramref name="removeCount"/> instructions starting at IL offset <paramref name="offset"/> with
    /// the instructions parsed from <paramref name="ilText"/>. <paramref name="removeCount"/> = 0 inserts before
    /// the instruction at <paramref name="offset"/>; null means "overwrite as many instructions as are inserted".
    /// </summary>
    public static string Replace(MethodDef method, InstructionParser parser, uint offset, string ilText, int? removeCount)
    {
        var body = method.Body ?? throw new ArgumentException($"Method {method.FullName} has no IL body. Use 'replace_method_body' to create one.");
        body.UpdateInstructionOffsets();
        var start = IndexAt(body, offset);

        var newInstrs = parser.ParseInstructions(ilText, method);
        var count = removeCount ?? newInstrs.Count;
        if (count < 0 || start + count > body.Instructions.Count)
            throw new ArgumentException($"Cannot remove {count} instruction(s) from IL_{offset:X4}: the method only has {body.Instructions.Count - start} from there.");
        if (newInstrs.Count == 0 && count == 0)
            throw new ArgumentException("Nothing to do: no instructions to insert and none to remove.");

        Splice(method, start, count, newInstrs);
        return $"Replaced {count} instruction(s) at IL_{offset:X4} with {newInstrs.Count} new instruction(s).";
    }

    /// <summary>Replaces the entire body (locals and exception handlers of the old body are kept only if referenced by index).</summary>
    public static string ReplaceBody(MethodDef method, InstructionParser parser, string ilText, IEnumerable<string>? newLocals)
    {
        if (method.IsAbstract || method.IsPinvokeImpl || method.IsRuntime)
            throw new ArgumentException($"Method {method.FullName} cannot have an IL body (abstract, P/Invoke or runtime-implemented).");

        method.Body ??= new CilBody();
        var body = method.Body;
        if (newLocals != null)
        {
            body.Variables.Clear();
            foreach (var local in newLocals)
                body.Variables.Add(new Local(parser.ParseTypeSig(local)));
        }

        var newInstrs = parser.ParseInstructions(ilText, method);
        if (newInstrs.Count == 0)
            throw new ArgumentException("The new body must contain at least one instruction.");

        body.ExceptionHandlers.Clear();
        body.Instructions.Clear();
        ResolveLabels(method, newInstrs, removed: new HashSet<Instruction>());
        foreach (var i in newInstrs)
            body.Instructions.Add(i);
        Finish(method);
        return $"Body of {method.FullName} replaced with {newInstrs.Count} instruction(s).";
    }

    public static string Nop(MethodDef method, uint offset, int count)
    {
        var body = method.Body ?? throw new ArgumentException($"Method {method.FullName} has no IL body.");
        body.UpdateInstructionOffsets();
        var start = IndexAt(body, offset);
        if (count <= 0 || start + count > body.Instructions.Count)
            throw new ArgumentException($"Invalid count {count} at IL_{offset:X4}.");

        // Turn each instruction into a NOP in place so branch targets stay valid. Multi-byte instructions keep
        // their byte size only if padded; dnlib recomputes offsets anyway, so we simply nop them.
        for (var i = start; i < start + count; i++)
        {
            body.Instructions[i].OpCode = OpCodes.Nop;
            body.Instructions[i].Operand = null;
        }
        Finish(method);
        return $"Replaced {count} instruction(s) starting at IL_{offset:X4} with nop.";
    }

    private static void Splice(MethodDef method, int start, int removeCount, List<Instruction> newInstrs)
    {
        var body = method.Body;
        var removed = body.Instructions.Skip(start).Take(removeCount).ToList();
        var removedSet = new HashSet<Instruction>(removed);
        var follower = start + removeCount < body.Instructions.Count ? body.Instructions[start + removeCount] : null;
        var replacement = newInstrs.Count > 0 ? newInstrs[0] : follower;

        ResolveLabels(method, newInstrs, removedSet, replacement);

        if (removedSet.Count > 0)
        {
            if (replacement == null)
                throw new ArgumentException("Removing the trailing instructions would leave dangling branch targets; insert a replacement (e.g. 'ret').");

            // Redirect anything still pointing at removed instructions.
            foreach (var instr in body.Instructions.Where(i => !removedSet.Contains(i)))
            {
                if (instr.Operand is Instruction t && removedSet.Contains(t))
                    instr.Operand = replacement;
                else if (instr.Operand is Instruction[] targets)
                    for (var k = 0; k < targets.Length; k++)
                        if (removedSet.Contains(targets[k]))
                            targets[k] = replacement;
            }
            foreach (var eh in body.ExceptionHandlers)
            {
                if (eh.TryStart != null && removedSet.Contains(eh.TryStart)) eh.TryStart = replacement;
                if (eh.TryEnd != null && removedSet.Contains(eh.TryEnd)) eh.TryEnd = replacement;
                if (eh.HandlerStart != null && removedSet.Contains(eh.HandlerStart)) eh.HandlerStart = replacement;
                if (eh.HandlerEnd != null && removedSet.Contains(eh.HandlerEnd)) eh.HandlerEnd = replacement;
                if (eh.FilterStart != null && removedSet.Contains(eh.FilterStart)) eh.FilterStart = replacement;
            }
        }

        for (var k = 0; k < removeCount; k++)
            body.Instructions.RemoveAt(start);
        for (var k = 0; k < newInstrs.Count; k++)
            body.Instructions.Insert(start + k, newInstrs[k]);

        Finish(method);
    }

    private static void ResolveLabels(MethodDef method, List<Instruction> newInstrs, IReadOnlySet<Instruction> removed, Instruction? replacement = null)
    {
        var original = method.Body.Instructions;

        Instruction Resolve(BranchLabel label)
        {
            var text = label.Target;
            if (text.StartsWith('#'))
            {
                if (int.TryParse(text.AsSpan(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx) && idx >= 0 && idx < newInstrs.Count)
                    return newInstrs[idx];
                throw new ArgumentException($"Branch target '{text}' is out of range (snippet has {newInstrs.Count} instructions).");
            }
            if (!uint.TryParse(text.AsSpan(3), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var off))
                throw new ArgumentException($"Invalid branch target '{text}'.");
            var target = original.FirstOrDefault(i => i.Offset == off)
                         ?? throw new ArgumentException($"Branch target {text} does not match an instruction in the original body.");
            if (removed.Contains(target))
                return replacement ?? throw new ArgumentException($"Branch target {text} is being removed.");
            return target;
        }

        foreach (var instr in newInstrs)
        {
            instr.Operand = instr.Operand switch
            {
                BranchLabel l => Resolve(l),
                BranchLabel[] ls => ls.Select(Resolve).ToArray(),
                _ => instr.Operand,
            };
        }
    }

    private static void Finish(MethodDef method)
    {
        var body = method.Body;
        body.SimplifyBranches();
        body.OptimizeBranches();
        body.UpdateInstructionOffsets();

        foreach (var instr in body.Instructions)
        {
            if (instr.OpCode.OperandType is OperandType.InlineBrTarget or OperandType.ShortInlineBrTarget &&
                (instr.Operand is not Instruction t || !body.Instructions.Contains(t)))
                throw new InvalidOperationException($"Branch at IL_{instr.Offset:X4} has an invalid target.");
        }

        if (MaxStackCalculator.GetMaxStack(body.Instructions, body.ExceptionHandlers, out var maxStack))
            body.MaxStack = (ushort)maxStack;
        body.KeepOldMaxStack = false;
    }

    private static int IndexAt(CilBody body, uint offset)
    {
        for (var i = 0; i < body.Instructions.Count; i++)
            if (body.Instructions[i].Offset == offset)
                return i;
        throw new ArgumentException($"No instruction starts at IL_{offset:X4}. Use 'get_method_il' to see valid offsets.");
    }
}
