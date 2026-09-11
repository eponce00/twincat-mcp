param([Parameter(Mandatory = $true)][string]$AssemblyPath)
$ErrorActionPreference = 'Stop'
$assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $AssemblyPath).Path)
# Exercise selection failures without COM, XAE startup or an installed version change.
Add-Type -TypeDefinition @'
using System;
using System.Reflection;
public static class ExactXaeVersionTests {
    static void Reject(Func<object> action, string message) {
        try { action(); }
        catch (TargetInvocationException ex) {
            if (ex.InnerException.Message.Contains(message)) return;
            throw;
        }
        throw new Exception("Expected rejection: " + message);
    }
    public static void Run(Assembly assembly) {
        var type = assembly.GetType("TcAutomation.Core.VisualStudioInstance", true);
        var method = type.GetMethod("SelectExactTwinCATVersion", BindingFlags.NonPublic | BindingFlags.Static);
        string wanted = "3.1.4026.26";
        string[] available = { "3.1.4024.0", wanted, "3.1.4026.99" };
        string selected = null;
        Action<string> select = value => selected = value;
        Func<string> read = () => selected;
        var actual = method.Invoke(null, new object[] { wanted, available, select, read });
        if (!wanted.Equals(actual) || selected != wanted) throw new Exception("Exact selection failed.");
        selected = null;
        Reject(() => method.Invoke(null, new object[] { "3.1.4026.27", available, select, read }), "no fallback");
        if (selected != null) throw new Exception("Missing baseline changed selection.");
        Reject(() => method.Invoke(null, new object[] { "", available, select, read }), "no fallback");
        Action<string> brokenSetter = value => { throw new InvalidOperationException("setter unavailable"); };
        Reject(() => method.Invoke(null, new object[] { wanted, available, brokenSetter, read }), "setter unavailable");
        Func<string> wrongRead = () => "3.1.4026.99";
        Reject(() => method.Invoke(null, new object[] { wanted, available, select, wrongRead }), "effective version");
        Func<string> brokenRead = () => { throw new InvalidOperationException("version unreadable"); };
        Reject(() => method.Invoke(null, new object[] { wanted, available, select, brokenRead }), "version unreadable");
        foreach (var optional in new[] { "", " ", null }) {
            var instance = Activator.CreateInstance(type, new object[] { "unused.sln", wanted, optional });
            var storedOverride = type.GetField("_forceTcVersion", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(instance);
            var storedBaseline = type.GetField("_tcVersion", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(instance);
            if (storedOverride != null || !wanted.Equals(storedBaseline)) throw new Exception("Absent override hides baseline.");
            actual = method.Invoke(null, new object[] { storedOverride ?? storedBaseline, available, select, read });
            if (!wanted.Equals(actual)) throw new Exception("Constructor baseline selection failed.");
        }
        var explicitInstance = Activator.CreateInstance(type, new object[] { "unused.sln", wanted, "3.1.4026.99" });
        var explicitOverride = type.GetField("_forceTcVersion", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(explicitInstance);
        if (!"3.1.4026.99".Equals(explicitOverride)) throw new Exception("Explicit override was discarded.");
    }
}
'@
[ExactXaeVersionTests]::Run($assembly)
Write-Output 'PASS: 10 exact-version cases (6 selection/failure cases, 3 absent-override constructors, 1 explicit-override constructor). No XAE launched.'
