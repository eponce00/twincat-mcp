<#
.SYNOPSIS
  Generates the TCatSysManagerLib COM interop assembly WITHOUT the Windows SDK (no TlbImp.exe).

.DESCRIPTION
  On a machine with TwinCAT XAE / TcXaeShell but no Visual Studio / Windows SDK, MSBuild's
  ResolveComReference cannot run (it needs TlbImp.exe / AxImp.exe). This script produces the
  same interop using .NET Framework's in-process TypeLibConverter (the engine tlbimp.exe wraps),
  compiled via Add-Type with the in-box framework csc.

  MUST be run under Windows PowerShell 5.1 (powershell.exe) -- PowerShell 7+ runs on .NET Core,
  which does not include TypeLibConverter.

  Output: <scriptdir>\interop\Interop.TCatSysManagerLib.dll
  Used by Directory.Build.props / Directory.Build.targets under /p:NoVisualStudioBuild=true.

.PARAMETER TlbPath
  Path to TCatSysManager.tlb. Defaults to the highest version found under
  C:\TwinCAT\3.1\Components\Base\TypeLib.
#>
[CmdletBinding()]
param(
    [string]$TlbPath
)
$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSEdition -eq 'Core') {
    throw "Run this under Windows PowerShell 5.1 (powershell.exe). PowerShell 7+ (.NET Core) lacks TypeLibConverter."
}

if (-not $TlbPath) {
    $base = 'C:\TwinCAT\3.1\Components\Base\TypeLib'
    # Highest parseable version folder. TryParse, because a bare [version] cast
    # on a non-version folder name terminates under ErrorActionPreference Stop.
    $TlbPath = Get-ChildItem $base -Recurse -Filter 'TCatSysManager.tlb' -ErrorAction SilentlyContinue |
        ForEach-Object {
            $v = $null
            if ([version]::TryParse($_.Directory.Name, [ref]$v)) { [pscustomobject]@{ Version = $v; Path = $_.FullName } }
        } |
        Sort-Object Version | Select-Object -Last 1 -ExpandProperty Path
}
if (-not $TlbPath -or -not (Test-Path $TlbPath)) {
    throw "TCatSysManager.tlb not found. Install TwinCAT XAE, or pass -TlbPath explicitly."
}

$outDir = Join-Path $PSScriptRoot 'interop'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$src = @'
using System;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;

public class TlbGen : System.Runtime.InteropServices.ITypeLibImporterNotifySink {
    public string OutDir;
    public System.Collections.Generic.Dictionary<string, Assembly> Done =
        new System.Collections.Generic.Dictionary<string, Assembly>();

    [DllImport("oleaut32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void LoadTypeLibEx(string strTypeLibName, int regKind,
        [MarshalAs(UnmanagedType.Interface)] out object typeLib);

    public Assembly Convert(string tlbPath, string nsName) {
        object tlb;
        LoadTypeLibEx(tlbPath, 2, out tlb); // 2 = REGKIND_NONE (load by path; do NOT (re)register the typelib)
        return ConvertObj(tlb, nsName);
    }

    private Assembly ConvertObj(object tlb, string nsName) {
        string libName = Marshal.GetTypeLibName((System.Runtime.InteropServices.ComTypes.ITypeLib)tlb);
        if (Done.ContainsKey(libName)) return Done[libName];
        string ns = nsName ?? libName;
        string outFile = Path.Combine(OutDir, "Interop." + libName + ".dll");
        var converter = new TypeLibConverter();
        AssemblyBuilder ab = converter.ConvertTypeLibToAssembly(
            tlb, outFile, TypeLibImporterFlags.TransformDispRetVals, this, null, null, ns, null);
        ab.Save(Path.GetFileName(outFile));
        Done[libName] = ab;
        Console.WriteLine("  generated " + outFile);
        return ab;
    }

    public Assembly ResolveRef(object typeLib) { return ConvertObj(typeLib, null); }
    public void ReportEvent(ImporterEventKind eventKind, int eventCode, string eventMsg) { }
}
'@

# Guard against "type TlbGen already exists" if this script is dot-sourced/re-run in one session.
if (-not ([System.Management.Automation.PSTypeName]'TlbGen').Type) {
    Add-Type -TypeDefinition $src -Language CSharp | Out-Null
}

# outFile is absolute, so it sets the assembly directory.
Push-Location $outDir
try {
    $gen = New-Object TlbGen
    $gen.OutDir = $outDir
    Write-Host "Converting $TlbPath"
    [void]$gen.Convert($TlbPath, 'TCatSysManagerLib')
    Write-Host "Done. Output in $outDir"
    Get-ChildItem $outDir -Filter *.dll | Select-Object Name, Length
} finally { Pop-Location }
