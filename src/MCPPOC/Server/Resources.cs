using System.ComponentModel;
using System.Text.Json;
using MCPPOC.Analysis;
using MCPPOC.Tools;
using MCPPOC.Workspace;
using ModelContextProtocol.Server;

namespace MCPPOC.Server;

/// <summary>Read-only views over loaded assemblies, addressable by URI so clients can attach them as context.</summary>
[McpServerResourceType]
public sealed class Resources(AssemblyWorkspace workspace, DecompilerService decompiler)
{
    [McpServerResource(UriTemplate = "dotnet://assemblies", Name = "Loaded assemblies", MimeType = "application/json")]
    [Description("Assemblies currently loaded in the workspace.")]
    public string Assemblies() =>
        JsonSerializer.Serialize(new WorkspaceTools(workspace).ListAssemblies(), JsonOptions);

    [McpServerResource(UriTemplate = "dotnet://{assembly}/info", Name = "Assembly overview", MimeType = "application/json")]
    [Description("Overview of a loaded assembly (same as 'get_assembly_info').")]
    public string Info(string assembly) =>
        JsonSerializer.Serialize(workspace.Get(assembly).Read(a => WorkspaceTools.BuildInfo(a, workspace)), JsonOptions);

    [McpServerResource(UriTemplate = "dotnet://{assembly}/source/{member}", Name = "Decompiled C# source", MimeType = "text/x-csharp")]
    [Description("C# source of a type (full name, nested types as Outer+Inner) or of any member token (0x06000012), decompiled with ILSpy.")]
    public string Source(string assembly, string member, CancellationToken cancellationToken)
    {
        var asm = workspace.Get(assembly);
        var def = asm.Read(a => MemberResolver.ResolveMember(a, Uri.UnescapeDataString(member)));
        return decompiler.DecompileMember(asm, def, cancellationToken);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
}
