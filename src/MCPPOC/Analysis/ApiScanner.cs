using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace MCPPOC.Analysis;

/// <summary>
/// Flags calls to APIs that matter during security review / malware triage (dynamic code loading, process
/// execution, injection-related P/Invokes, network, crypto, persistence, anti-debugging...).
/// </summary>
public static class ApiScanner
{
    private sealed record Rule(string Category, string Severity, string Prefix);

    // Prefix match against "Namespace.Type::Method" (or just "Namespace.Type" to match every member).
    private static readonly Rule[] Rules =
    [
        new("DynamicCode", "high", "System.Reflection.Assembly::Load"),
        new("DynamicCode", "high", "System.Reflection.Assembly::LoadFrom"),
        new("DynamicCode", "high", "System.Reflection.Assembly::LoadFile"),
        new("DynamicCode", "high", "System.Reflection.Assembly::UnsafeLoadFrom"),
        new("DynamicCode", "high", "System.AppDomain::Load"),
        new("DynamicCode", "high", "System.Runtime.Loader.AssemblyLoadContext::LoadFromStream"),
        new("DynamicCode", "high", "System.Reflection.Emit."),
        new("DynamicCode", "high", "System.CodeDom.Compiler."),
        new("DynamicCode", "high", "Microsoft.CSharp.CSharpCodeProvider"),
        new("DynamicCode", "medium", "System.Linq.Expressions.Expression::Compile"),
        new("Reflection", "medium", "System.Reflection.MethodBase::Invoke"),
        new("Reflection", "medium", "System.Reflection.MethodInfo::Invoke"),
        new("Reflection", "medium", "System.Type::InvokeMember"),
        new("Reflection", "low", "System.Type::GetMethod"),
        new("Reflection", "low", "System.Type::GetType"),
        new("Reflection", "low", "System.Activator::CreateInstance"),
        new("Reflection", "low", "System.Delegate::DynamicInvoke"),
        new("Reflection", "low", "System.Reflection.Assembly::GetManifestResourceStream"),
        new("Process", "high", "System.Diagnostics.Process::Start"),
        new("Process", "medium", "System.Diagnostics.ProcessStartInfo"),
        new("Process", "medium", "System.Diagnostics.Process::Kill"),
        new("Process", "low", "System.Diagnostics.Process::GetProcesses"),
        new("Process", "low", "System.Diagnostics.Process::GetProcessesByName"),
        new("Network", "medium", "System.Net.WebClient"),
        new("Network", "medium", "System.Net.Http.HttpClient"),
        new("Network", "medium", "System.Net.WebRequest"),
        new("Network", "medium", "System.Net.HttpWebRequest"),
        new("Network", "medium", "System.Net.Sockets."),
        new("Network", "medium", "System.Net.Dns"),
        new("Network", "low", "System.Net.Mail."),
        new("Network", "low", "System.Net.NetworkInformation."),
        new("Crypto", "medium", "System.Security.Cryptography.Aes"),
        new("Crypto", "medium", "System.Security.Cryptography.Rijndael"),
        new("Crypto", "medium", "System.Security.Cryptography.RijndaelManaged"),
        new("Crypto", "medium", "System.Security.Cryptography.TripleDES"),
        new("Crypto", "medium", "System.Security.Cryptography.DES"),
        new("Crypto", "medium", "System.Security.Cryptography.RC2"),
        new("Crypto", "medium", "System.Security.Cryptography.RSA"),
        new("Crypto", "low", "System.Security.Cryptography.ProtectedData"),
        new("Crypto", "low", "System.Security.Cryptography.Rfc2898DeriveBytes"),
        new("Crypto", "low", "System.Security.Cryptography.ICryptoTransform"),
        new("Encoding", "low", "System.Convert::FromBase64String"),
        new("Encoding", "low", "System.Convert::FromBase64CharArray"),
        new("Compression", "low", "System.IO.Compression.GZipStream"),
        new("Compression", "low", "System.IO.Compression.DeflateStream"),
        new("FileSystem", "medium", "System.IO.File::Delete"),
        new("FileSystem", "medium", "System.IO.Directory::Delete"),
        new("FileSystem", "low", "System.IO.File::WriteAllBytes"),
        new("FileSystem", "low", "System.IO.File::Copy"),
        new("FileSystem", "low", "System.IO.File::Move"),
        new("FileSystem", "low", "System.IO.File::SetAttributes"),
        new("Registry", "medium", "Microsoft.Win32.Registry"),
        new("Registry", "medium", "Microsoft.Win32.RegistryKey::SetValue"),
        new("Persistence", "high", "Microsoft.Win32.TaskScheduler."),
        new("Persistence", "medium", "System.ServiceProcess.ServiceInstaller"),
        new("Serialization", "high", "System.Runtime.Serialization.Formatters.Binary.BinaryFormatter"),
        new("Serialization", "high", "System.Runtime.Serialization.NetDataContractSerializer"),
        new("Serialization", "high", "System.Web.UI.LosFormatter"),
        new("Serialization", "high", "System.Web.UI.ObjectStateFormatter"),
        new("Serialization", "medium", "System.Runtime.Serialization.Formatters.Soap.SoapFormatter"),
        new("Serialization", "medium", "System.Web.Script.Serialization.JavaScriptSerializer"),
        new("Serialization", "medium", "System.Xml.Serialization.XmlSerializer"),
        new("Interop", "medium", "System.Runtime.InteropServices.Marshal::Copy"),
        new("Interop", "medium", "System.Runtime.InteropServices.Marshal::GetDelegateForFunctionPointer"),
        new("Interop", "medium", "System.Runtime.InteropServices.Marshal::AllocHGlobal"),
        new("Interop", "low", "System.Runtime.InteropServices.Marshal::ReadIntPtr"),
        new("AntiAnalysis", "medium", "System.Diagnostics.Debugger::get_IsAttached"),
        new("AntiAnalysis", "medium", "System.Diagnostics.Debugger::IsLogging"),
        new("AntiAnalysis", "low", "System.Environment::get_MachineName"),
        new("AntiAnalysis", "low", "System.Environment::get_UserName"),
        new("AntiAnalysis", "low", "System.Management.ManagementObjectSearcher"),
        new("Credentials", "medium", "System.Security.Cryptography.X509Certificates.X509Store"),
        new("Credentials", "medium", "System.Net.NetworkCredential"),
        new("Capture", "medium", "System.Windows.Forms.Clipboard"),
        new("Capture", "medium", "System.Drawing.Graphics::CopyFromScreen"),
        new("Shell", "medium", "System.Management.Automation.PowerShell"),
    ];

    // Native exports frequently abused for injection, hooking, evasion.
    private static readonly Dictionary<string, (string Category, string Severity)> NativeApis = new(StringComparer.OrdinalIgnoreCase)
    {
        ["VirtualAlloc"] = ("Injection", "high"), ["VirtualAllocEx"] = ("Injection", "high"),
        ["VirtualProtect"] = ("Injection", "high"), ["VirtualProtectEx"] = ("Injection", "high"),
        ["WriteProcessMemory"] = ("Injection", "high"), ["ReadProcessMemory"] = ("Injection", "medium"),
        ["CreateRemoteThread"] = ("Injection", "high"), ["NtCreateThreadEx"] = ("Injection", "high"),
        ["QueueUserAPC"] = ("Injection", "high"), ["NtUnmapViewOfSection"] = ("Injection", "high"),
        ["ZwUnmapViewOfSection"] = ("Injection", "high"), ["SetThreadContext"] = ("Injection", "high"),
        ["GetThreadContext"] = ("Injection", "medium"), ["ResumeThread"] = ("Injection", "medium"),
        ["OpenProcess"] = ("Injection", "medium"), ["CreateProcess"] = ("Process", "medium"),
        ["CreateProcessA"] = ("Process", "medium"), ["CreateProcessW"] = ("Process", "medium"),
        ["ShellExecute"] = ("Process", "medium"), ["ShellExecuteA"] = ("Process", "medium"), ["ShellExecuteW"] = ("Process", "medium"),
        ["WinExec"] = ("Process", "high"),
        ["LoadLibrary"] = ("DynamicCode", "medium"), ["LoadLibraryA"] = ("DynamicCode", "medium"), ["LoadLibraryW"] = ("DynamicCode", "medium"),
        ["GetProcAddress"] = ("DynamicCode", "medium"),
        ["SetWindowsHookEx"] = ("Capture", "high"), ["SetWindowsHookExA"] = ("Capture", "high"), ["SetWindowsHookExW"] = ("Capture", "high"),
        ["GetAsyncKeyState"] = ("Capture", "high"), ["GetKeyState"] = ("Capture", "medium"), ["GetForegroundWindow"] = ("Capture", "low"),
        ["IsDebuggerPresent"] = ("AntiAnalysis", "medium"), ["CheckRemoteDebuggerPresent"] = ("AntiAnalysis", "medium"),
        ["NtQueryInformationProcess"] = ("AntiAnalysis", "medium"), ["NtSetInformationThread"] = ("AntiAnalysis", "high"),
        ["AdjustTokenPrivileges"] = ("Privilege", "high"), ["RtlAdjustPrivilege"] = ("Privilege", "high"),
        ["CryptUnprotectData"] = ("Credentials", "high"), ["RtlSetProcessIsCritical"] = ("Persistence", "high"),
        ["URLDownloadToFile"] = ("Network", "high"), ["URLDownloadToFileA"] = ("Network", "high"), ["URLDownloadToFileW"] = ("Network", "high"),
        ["InternetOpen"] = ("Network", "medium"), ["InternetOpenUrl"] = ("Network", "medium"),
        ["AmsiScanBuffer"] = ("AntiAnalysis", "high"), ["EtwEventWrite"] = ("AntiAnalysis", "high"),
    };

    public static readonly IReadOnlyList<string> Categories =
        Rules.Select(r => r.Category).Concat(NativeApis.Values.Select(v => v.Category)).Distinct().Order().ToList();

    public static List<ApiFinding> Scan(IEnumerable<TypeDef> types, ISet<string>? categories)
    {
        var findings = new List<ApiFinding>();
        var typeList = types.ToList();

        bool Wanted(string category) => categories == null || categories.Count == 0 || categories.Contains(category);

        // P/Invoke declarations themselves.
        foreach (var method in typeList.SelectMany(t => t.Methods).Where(m => m.ImplMap != null))
        {
            var entry = method.ImplMap.Name.String;
            if (NativeApis.TryGetValue(entry, out var info) && Wanted(info.Category))
                findings.Add(new ApiFinding(info.Category, info.Severity, $"[{method.ImplMap.Module?.Name}] {entry} (P/Invoke declaration)", ModelFactory.Ref(method), null));
        }

        foreach (var method in typeList.SelectMany(t => t.Methods))
        {
            if (!method.HasBody)
                continue;
            foreach (var instr in method.Body.Instructions)
            {
                if (instr.Operand is not IMethod target || instr.OpCode.Code is not (Code.Call or Code.Callvirt or Code.Newobj or Code.Ldftn or Code.Ldvirtftn))
                    continue;

                // Calls to P/Invoke methods defined in this module.
                if (target is MethodDef { ImplMap: not null } pinv)
                {
                    if (NativeApis.TryGetValue(pinv.ImplMap.Name, out var n) && Wanted(n.Category))
                        findings.Add(new ApiFinding(n.Category, n.Severity, $"[{pinv.ImplMap.Module?.Name}] {pinv.ImplMap.Name}", ModelFactory.Ref(method), ModelFactory.Il(instr.Offset)));
                    continue;
                }

                var declaring = target.DeclaringType?.FullName ?? "";
                // Strip generic instantiation for matching.
                var tick = declaring.IndexOf('<');
                if (tick > 0) declaring = declaring[..tick];
                var api = $"{declaring}::{target.Name}";
                foreach (var rule in Rules)
                {
                    if (!Wanted(rule.Category))
                        continue;
                    if (api.StartsWith(rule.Prefix, StringComparison.Ordinal) ||
                        (!rule.Prefix.Contains("::") && declaring.StartsWith(rule.Prefix, StringComparison.Ordinal)))
                    {
                        findings.Add(new ApiFinding(rule.Category, rule.Severity, api, ModelFactory.Ref(method), ModelFactory.Il(instr.Offset)));
                        break;
                    }
                }
            }
        }

        return findings
            .OrderBy(f => f.Severity switch { "high" => 0, "medium" => 1, _ => 2 })
            .ThenBy(f => f.Category)
            .ThenBy(f => f.Api)
            .ToList();
    }
}
