using System;
using System.IO;
using TcAutomation.Core;

static class HmiTests
{
    public static void Run()
    {
        int cases = 0;
        void Check(bool ok) { cases++; if (!ok) throw new Exception("HMI policy assertion " + cases); }
        void Refuse(Action action) { bool failed = false; try { action(); } catch (InvalidOperationException) { failed = true; } Check(failed); }
        Check(HmiPolicy.Target("https://LOCALHOST:443/") == "https://localhost/");
        foreach (var url in new[] { "http://host/", "https://u:secret@host/", "https://host/path", "https://host/?key=x", "https://host/#x", "https://host/\n" })
            Refuse(() => HmiPolicy.Target(url));
        HmiPolicy.Profile("https://host:1020/", "https://HOST:1020/", false, false, true);
        Refuse(() => HmiPolicy.Profile("https://other/", "https://host/", false, false, true));
        Refuse(() => HmiPolicy.Profile("https://host/", "https://host/", true, false, true));
        Refuse(() => HmiPolicy.Profile("https://host/", "https://host/", false, true, true));
        Refuse(() => HmiPolicy.Profile("https://host/", "https://host/", false, false, false));
        var root = Path.Combine(Path.GetTempPath(), "hmi-policy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Properties"));
        try
        {
            var project = Path.Combine(root, "Hmi.hmiproj");
            var profiles = Path.Combine(root, "Properties", "tchmipublish.config.json");
            File.WriteAllText(project, "<Project/>");
            File.WriteAllText(profiles, "[{\"profileName\":\"Local\",\"encryptedTcHmiServerPassword\":\"SECRET-A\"}]");
            Check(HmiPolicy.Profiles(root)[0] == "Local");
            var first = HmiPolicy.Fingerprint(project, "https://host/", "Local");
            Check(first.Length == 64);
            Check(first == HmiPolicy.Fingerprint(project, "https://host/", "Local"));
            Check(first != HmiPolicy.Fingerprint(project, "https://other/", "Local"));
            Check(first != HmiPolicy.Fingerprint(project, "https://host/", "Other"));
            Directory.CreateDirectory(Path.Combine(root, "bin"));
            File.WriteAllText(Path.Combine(root, "bin", "Default.html"), "generated");
            Check(first == HmiPolicy.Fingerprint(project, "https://host/", "Local"));
            File.WriteAllText(Path.Combine(root, "panel.js"), "source");
            var second = HmiPolicy.Fingerprint(project, "https://host/", "Local");
            Check(first != second);
            File.WriteAllText(profiles, "[{\"profileName\":\"Local\",\"encryptedTcHmiServerPassword\":\"SECRET-B\"}]");
            Check(second != HmiPolicy.Fingerprint(project, "https://host/", "Local"));
            File.WriteAllText(project, "<Project><ItemGroup><Content Include='../outside.js'/></ItemGroup></Project>");
            Refuse(() => HmiPolicy.Fingerprint(project, "https://host/", "Local"));
            File.WriteAllText(project, "<Project><ItemGroup><Content Include='*.js'/></ItemGroup></Project>");
            Refuse(() => HmiPolicy.Fingerprint(project, "https://host/", "Local"));
            File.WriteAllText(profiles, "[{\"profileName\":\"Local\"},{\"profileName\":\"Local\"}]");
            Refuse(() => HmiPolicy.Profiles(root));
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine("PASS: " + cases + " HMI policy cases; no XAE or server writes.");
    }
}
