using System.ComponentModel;
using System.Text;
using dnlib.DotNet;
using MCPPOC.Analysis;
using MCPPOC.Workspace;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace MCPPOC.Tools;

[McpServerToolType]
public sealed class ResourceTools(AssemblyWorkspace workspace)
{
    [McpServerTool(Name = "list_resources", Title = "List manifest resources", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("List manifest resources (embedded, linked, in other assemblies) with size and detected content type (PE, ZIP, GZip, .resources, images, high-entropy blobs...). Embedded PEs/encrypted blobs are common in packed malware.")]
    public IReadOnlyList<ResourceSummary> ListResources([Description(P.Assembly)] string? assembly = null) =>
        workspace.Get(assembly).Read(a => a.Module.Resources.Select(r =>
        {
            long? size = null;
            string? detected = null;
            if (r is EmbeddedResource er)
            {
                var data = er.CreateReader().ToArray();
                size = data.Length;
                detected = CodeAnalysis.DetectFormat(data);
            }
            var kind = r switch
            {
                EmbeddedResource => "embedded",
                LinkedResource l => $"linked ({l.File?.Name})",
                AssemblyLinkedResource al => $"in assembly {al.Assembly?.FullName}",
                _ => r.ResourceType.ToString(),
            };
            return new ResourceSummary(r.Name, kind, r.Visibility.ToString().ToLowerInvariant(), size, detected);
        }).ToList());

    [McpServerTool(Name = "get_resource", Title = "Read/extract a resource", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Inspect an embedded resource: decodes .resources files into entries, returns text resources as text, otherwise a hex preview (and base64 on request). Pass 'saveTo' to write the raw bytes to disk for further analysis (e.g. an embedded assembly you then load with 'load_assembly').")]
    public ResourceContent GetResource(
        [Description("Resource name as shown by 'list_resources'.")] string name,
        [Description("Optional absolute file path to save the raw resource bytes to.")] string? saveTo = null,
        [Description("Include the full content as base64 (capped at 1 MB).")] bool includeBase64 = false,
        [Description(P.Assembly)] string? assembly = null) =>
        workspace.Get(assembly).Read(a =>
        {
            var res = a.Module.Resources.FirstOrDefault(r => r.Name == name)
                      ?? a.Module.Resources.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase))
                      ?? throw new McpException($"Resource '{name}' not found. Available: {string.Join(", ", a.Module.Resources.Select(r => r.Name.String).Take(30))}");
            if (res is not EmbeddedResource er)
                throw new McpException($"Resource '{name}' is not embedded ({res.ResourceType}); its data lives in another file.");

            var data = er.CreateReader().ToArray();
            string? savedTo = null;
            if (!string.IsNullOrWhiteSpace(saveTo))
            {
                savedTo = Path.GetFullPath(saveTo);
                Directory.CreateDirectory(Path.GetDirectoryName(savedTo)!);
                File.WriteAllBytes(savedTo, data);
            }

            var detected = CodeAnalysis.DetectFormat(data);
            List<ResourceEntry>? entries = null;
            try { entries = CodeAnalysis.ReadResourceSet(a.Module, data); }
            catch (Exception ex) { detected += $" (could not parse .resources: {ex.Message})"; }

            string? text = null;
            if (entries == null && CodeAnalysis.LooksLikeText(data))
            {
                text = Encoding.UTF8.GetString(data);
                if (text.Length > 100_000)
                    text = text[..100_000] + "\n... (truncated)";
            }

            return new ResourceContent(res.Name, data.Length, detected, savedTo, text,
                includeBase64 ? Convert.ToBase64String(data.AsSpan(0, Math.Min(data.Length, 1 << 20))) : null,
                text == null && entries == null ? CodeAnalysis.FormatHexDump(data.AsSpan(0, Math.Min(data.Length, 512)), 0) : null,
                entries);
        });
}
