using System;
using System.IO;
using System.Linq;
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

            const string ports = "<P><HmiCommunicationServerPort>3000</HmiCommunicationServerPort><X>1</X>"
                + "<HmiCommunicationServerAuthPort>13000</HmiCommunicationServerAuthPort></P>";
            bool OnlyPorts(string a, string b) =>
                HmiPolicy.OnlyEngineeringPortsChanged(System.Text.Encoding.UTF8.GetBytes(a), System.Text.Encoding.UTF8.GetBytes(b));
            Check(OnlyPorts(ports, ports.Replace(">3000<", ">3001<").Replace(">13000<", ">13001<")));
            Check(OnlyPorts(ports, ports.Replace(">13000<", ">13002<")));
            Check(!OnlyPorts(ports, ports));
            Check(!OnlyPorts(ports, ports.Replace(">3000<", ">3001<").Replace("<X>1", "<X>2")));
            Check(!OnlyPorts(ports, ports.Replace("<HmiCommunicationServerPort>3000</HmiCommunicationServerPort>", "")));
            Check(!OnlyPorts(ports, ports.Replace(">3000<", ">x<")));
            // Two different invalid UTF-8 bytes must not compare equal.
            var e8 = System.Text.Encoding.ASCII.GetBytes(ports.Replace("<X>1", "<X>\u0001"));
            var e9 = System.Text.Encoding.ASCII.GetBytes(ports.Replace("<X>1", "<X>\u0001").Replace(">3000<", ">3001<"));
            e8[Array.IndexOf(e8, (byte)1)] = 0xE8;
            e9[Array.IndexOf(e9, (byte)1)] = 0xE9;
            Check(!HmiPolicy.OnlyEngineeringPortsChanged(e8, e9));
            var u16 = System.Text.Encoding.Unicode;
            Check(!HmiPolicy.OnlyEngineeringPortsChanged(u16.GetBytes(ports), u16.GetBytes(ports.Replace(">3000<", ">3001<"))));
            var sln = Path.Combine(root, "Panel.sln");
            File.WriteAllText(sln, "Project(\"{A}\") = \"Panel\", \"Panel\\Panel.hmiproj\", \"{B}\"\r\n"
                + "Project(\"{A}\") = \"Plc\", \"Plc\\Plc.tsproj\", \"{C}\"\r\nProject(\"{A}\") = \"Two\", \"panel\\PANEL.hmiproj\", \"{D}\"\r\n");
            Check(HmiPolicy.HmiProjects(sln).SequenceEqual(new[] { Path.Combine(root, "Panel", "Panel.hmiproj") }));

            bool Replace(bool shellHmi, string shellSln, bool hmi, string next) =>
                TcAutomation.Commands.HostCommand.MustReplaceShell(shellHmi, shellSln, hmi, next);
            string a = Path.Combine(root, "A.sln"), b = Path.Combine(root, "B.sln");
            Check(Replace(true, a, true, b));
            Check(!Replace(true, a, true, a.ToUpperInvariant()));
            Check(Replace(true, a, false, a) && Replace(false, a, true, a));
            Check(!Replace(false, a, false, b));
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine("PASS: " + cases + " HMI policy cases; no XAE or server writes.");
    }
}
