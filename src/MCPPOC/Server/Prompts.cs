using System.ComponentModel;
using ModelContextProtocol.Server;

namespace MCPPOC.Server;

[McpServerPromptType]
public sealed class Prompts
{
    [McpServerPrompt(Name = "triage_assembly", Title = "Triage a .NET assembly")]
    [Description("Step-by-step first look at an unknown .NET assembly: identity, entry points, structure, interesting strings and APIs, then a summary.")]
    public static string TriageAssembly([Description("Absolute path of the assembly to analyse.")] string path) => $"""
        Analyse the .NET assembly at `{path}` with the mcp-dotnet tools and produce a concise report.

        1. `load_assembly` with the path. Note target framework, IL-only vs mixed-mode, strong name and any obfuscation hints.
        2. `get_entry_points`: decompile the entry point and the module initializer / static constructors (`decompile`).
        3. `list_namespaces`, then `list_types` (includeCompilerGenerated=false) to map the main components.
        4. `scan_suspicious_apis` and `list_pinvokes` to find security-relevant behaviour; decompile the most relevant callers.
        5. `find_strings` with distinct=true; look for URLs, IPs, file paths, registry keys, commands, credentials and keys.
        6. `list_resources`; inspect suspicious ones with `get_resource`.

        Report: purpose of the program, main components, notable behaviours (with method tokens as evidence),
        indicators (strings/URLs/hashes), and open questions. Only state what the tools showed you.
        """;

    [McpServerPrompt(Name = "malware_triage", Title = "Malware triage of a .NET sample")]
    [Description("Security-focused triage of a suspicious .NET sample: capabilities, persistence, C2, payload staging and IOCs. Static analysis only, nothing is executed.")]
    public static string MalwareTriage([Description("Absolute path of the sample.")] string path) => $"""
        Perform static malware triage of the .NET sample at `{path}`. Never execute it; use only the mcp-dotnet tools.

        1. `load_assembly`; record obfuscation hints and whether it is mixed-mode.
        2. `get_entry_points` and decompile them: packers typically decrypt and load a payload from the module initializer or Main.
        3. `scan_suspicious_apis`. Prioritise DynamicCode (Assembly.Load(byte[])), Injection, Process, Network, Persistence, AntiAnalysis, Credentials.
           For each high-severity finding, `decompile` the caller and follow data with `find_callers` / `find_field_references`.
        4. `list_resources` → `get_resource`: embedded PE/ZIP/high-entropy blobs suggest staged payloads. Save them with 'saveTo'
           and `load_assembly` them if they are .NET.
        5. `find_strings` (distinct) for C2 URLs/IPs, mutex names, user agents, file and registry paths; look for base64/XOR decoders.
        6. If names are obfuscated, use `rename_member` as you understand functions so later decompilation reads well.

        Deliver: verdict and confidence, capability list mapped to MITRE ATT&CK techniques where clear, execution flow,
        IOCs (domains, IPs, paths, registry keys, mutexes, hashes of extracted payloads if computed), and recommended next steps.
        """;

    [McpServerPrompt(Name = "find_secrets", Title = "Hunt for hard-coded secrets")]
    [Description("Look for hard-coded credentials, API keys, connection strings, certificates and crypto keys in an assembly.")]
    public static string FindSecrets([Description("Absolute path of the assembly.")] string path) => $$"""
        Find hard-coded secrets in `{{path}}` using the mcp-dotnet tools.

        - `load_assembly`, then `find_strings` with isRegex=true for patterns like
          `(?i)(password|pwd|secret|token|apikey|api_key|bearer|connectionstring|server=|data source=)`,
          `AKIA[0-9A-Z]{16}`, `eyJ[A-Za-z0-9_-]+\.`, `-----BEGIN`, and long base64/hex strings.
        - `find_members` with pattern `(?i)key|iv|salt|secret|password` and `get_static_field_values` on the declaring types
          (FieldRVA initial values often hold byte[] keys).
        - `scan_suspicious_apis` with categories=Crypto,Credentials and decompile the callers to see where keys come from.
        - `list_resources` / `get_resource` for embedded config files and certificates.

        For each finding give: value (redact the middle of real-looking secrets), location (method token + IL offset), and how it is used.
        """;
}
