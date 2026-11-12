#nullable enable
using System;
using System.Collections.Generic;
using TcAutomation.Core;

/// <summary>
/// What a deployment builds and when it refuses: target descriptions, variant parameters and the build planner.
/// No ADS connection, XAE or PLC is used; the target descriptions are recorded TcTargetDesc replies.
/// </summary>
static class DeployPlatformTests
{
    static int cases;
    static void Check(bool value, string message) { cases++; if (!value) throw new Exception(message); }

    // Recorded from a local user-mode runtime and a CP67xx IPC (Windows 10, TwinCAT RT).
    const string UserMode = "<TcTargetDesc><TargetType IsUsermodeRuntime=\"true\">PC-WIN-U-OS</TargetType><TargetVersion><Version>3</Version><Revision>1</Revision><Build>4026</Build></TargetVersion><TargetFeatures><NetId>192.168.1.108.1.1</NetId></TargetFeatures><Hardware><CPUArchitecture>9</CPUArchitecture></Hardware><OsImage><OsName>Windows 10 Pro</OsName><OsVersion>6.2</OsVersion></OsImage></TcTargetDesc>";
    const string WindowsRt = "<TcTargetDesc><TargetType>CB3273-WIN 10.0</TargetType><TargetVersion><Version>3</Version><Revision>1</Revision><Build>4026</Build></TargetVersion><TargetFeatures><Level>CP</Level><NetId>172.18.236.100.1.1</NetId></TargetFeatures><Hardware><Model>CP67xx-0060</Model><CPUArchitecture>9</CPUArchitecture></Hardware><OsImage><OsName>Windows 10 Enterprise LTSC 2021</OsName><OsVersion>10.0</OsVersion></OsImage></TcTargetDesc>";
    const string Bsd = "<TcTargetDesc><TargetType>CX-BSD</TargetType><Hardware><CPUArchitecture>9</CPUArchitecture></Hardware><OsImage><OsName>TwinCAT/BSD</OsName></OsImage></TcTargetDesc>";
    const string Arm = "<TcTargetDesc><TargetType>CX8190</TargetType><Hardware><CPUArchitecture>5</CPUArchitecture></Hardware><OsImage><OsName>Windows CE</OsName></OsImage></TcTargetDesc>";

    const string Tsproj = @"<TcSmProject TcProjectVariant=""Station""><Project>
  <System><Licenses><Target><LicenseDevice><ProjectVariant PvUseDisable=""true""><Variant><Name>LocalHil</Name><Disable>true</Disable></Variant></ProjectVariant></LicenseDevice></Target></Licenses>
    <ProjectVariant PvUseParameter=""true"" PvUseLink=""true"">
      <Variant><Name>LocalHil</Name><TargetNetId>192.168.1.108.1.1</TargetNetId><BuildConfig>Debug|TwinCAT OS (x64)</BuildConfig></Variant>
      <Variant><Name>Station</Name><TargetNetId>192.168.1.5.1.1</TargetNetId><BuildConfig>Debug|TwinCAT RT (x64)</BuildConfig></Variant>
    </ProjectVariant></System>
  <Variants><Variant><Name>Station</Name></Variant><Variant><Name>LocalHil</Name></Variant><Variant><Name>Spare</Name></Variant></Variants>
</Project></TcSmProject>";

    const string Local = "192.168.1.108.1.1";
    const string Station = "192.168.1.5.1.1";

    static BuildSelection Plan(TargetDescription target, string? variant = null, string? configuration = null, string? platform = null,
        string active = "Debug|TwinCAT OS (x64)", string context = Local, bool allowUnverified = false)
    {
        var parts = active.Split('|');
        return BuildPlanner.Resolve(ProjectVariants.Parse(Tsproj), variant, configuration, platform, parts[0], parts[1], context, target, allowUnverified);
    }

    static string Refusal(Func<BuildSelection> plan)
    {
        try { plan(); }
        catch (DeployRefusedException ex) { return ex.Message; }
        throw new Exception("expected a refusal");
    }

    public static void Run()
    {
        // Target descriptions
        var um = TargetPlatform.Parse(Local, UserMode);
        Check(um.Read && um.UserModeRuntime && um.ExpectedPlatform == "TwinCAT OS (x64)", "user-mode runtime builds for TwinCAT OS (x64)");
        Check(um.TwinCATVersion == "3.1.4026", "version read");
        var rt = TargetPlatform.Parse(Station, WindowsRt);
        Check(rt.Read && !rt.UserModeRuntime && rt.ExpectedPlatform == "TwinCAT RT (x64)", "Windows RT IPC builds for TwinCAT RT (x64)");
        Check(TargetPlatform.Parse(Station, Bsd).ExpectedPlatform == "TwinCAT OS (x64)", "TwinCAT/BSD builds for TwinCAT OS (x64)");
        Check(TargetPlatform.Parse(Station, Arm).ExpectedPlatform == null, "an ARM target is not told");
        var broken = TargetPlatform.Parse(Station, "<not xml");
        Check(!broken.Read && broken.ErrorMessage != null, "invalid XML is unreadable, not guessed");

        // Variants from the .tsproj: parameters, the variant list, per-object settings ignored
        var variants = ProjectVariants.Parse(Tsproj);
        Check(variants.Count == 3, "three variants");
        var localHil = variants.Find(v => v.Name == "LocalHil")!;
        Check(localHil.TargetNetId == Local && localHil.Configuration == "Debug" && localHil.Platform == "TwinCAT OS (x64)", "LocalHil parameters");
        Check(variants.Find(v => v.Name == "Spare")!.Platform == null, "a listed variant without parameters");
        Check(ProjectVariants.SavedVariant(Tsproj) == "Station", "saved variant");

        // The incident: the shared shell was left on the OS platform by a local deploy; the station is RT.
        var s = Plan(rt, context: Station, active: "Debug|TwinCAT OS (x64)");
        Check(s.Platform == "TwinCAT RT (x64)" && s.Source == "target" && s.PlatformVerified, "no arguments: the target decides, not the shell's leftover platform");
        s = Plan(rt, variant: "station", context: Station);
        Check(s.Variant == "Station" && s.Configuration == "Debug" && s.Platform == "TwinCAT RT (x64)" && s.Source == "variant", "variant decides (any case)");
        s = Plan(um, variant: "LocalHil");
        Check(s.Platform == "TwinCAT OS (x64)" && s.PlatformVerified && s.PlatformStatus.StartsWith("verified"), "LocalHil on the user-mode runtime");

        // Refusals happen before anything is built or downloaded
        string m = Refusal(() => Plan(um, platform: "TwinCAT RT (x64)"));
        Check(m.Contains("does not match") && m.Contains("TwinCAT OS (x64)") && m.Contains("Nothing was built"), "RT x64 against the user-mode runtime is refused");
        m = Refusal(() => Plan(rt, variant: "LocalHil", context: Station));
        Check(m.Contains("targets 192.168.1.108.1.1") && m.Contains("this context targets 192.168.1.5.1.1"), "variant for another target is refused");
        m = Refusal(() => Plan(um, variant: "LocalHil", platform: "TwinCAT RT (x64)"));
        Check(m.Contains("conflicts with variant"), "explicit platform conflicting with the variant is refused");
        m = Refusal(() => Plan(um, variant: "Nope"));
        Check(m.Contains("not in the project") && m.Contains("LocalHil"), "unknown variant lists the variants");
        m = Refusal(() => Plan(rt, context: Station, platform: "TwinCAT OS (x64)", allowUnverified: true));
        Check(m.Contains("does not match"), "allowUnverifiedPlatform never overrides a known mismatch");

        // Unreadable and unknown targets
        var unreadable = new TargetDescription { AmsNetId = Station, ErrorMessage = "The target description could not be read: timeout" };
        m = Refusal(() => Plan(unreadable, context: Station, platform: "TwinCAT RT (x64)"));
        Check(m.Contains("could not be checked") && m.Contains("allowUnverifiedPlatform"), "unreadable target refused without the flag");
        s = Plan(unreadable, context: Station, platform: "TwinCAT RT (x64)", allowUnverified: true);
        Check(!s.PlatformVerified && s.PlatformStatus.StartsWith("platform not verified"), "unreadable target with the flag: receipt says not verified");
        var arm = TargetPlatform.Parse(Station, Arm);
        m = Refusal(() => Plan(arm, context: Station, platform: "TwinCAT RT (x64)", allowUnverified: true));
        Check(m.Contains("not known"), "a readable target of unknown kind is refused even with the flag");

        Check(ProjectVariants.SplitBuildConfig("Release|TwinCAT RT (x86)") == ("Release", "TwinCAT RT (x86)"), "split build config");
        Console.WriteLine("PASS: " + cases + " deploy platform cases; no ADS, XAE or PLC used.");
    }
}
