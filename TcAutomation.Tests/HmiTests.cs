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
                HmiPolicy.PortRestore("x.hmiproj", System.Text.Encoding.UTF8.GetBytes(a), System.Text.Encoding.UTF8.GetBytes(b)) != null;
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
            Check(HmiPolicy.PortRestore("x.hmiproj", e8, e9) == null);
            var u16 = System.Text.Encoding.Unicode;
            Check(HmiPolicy.PortRestore("x.hmiproj", u16.GetBytes(ports), u16.GetBytes(ports.Replace(">3000<", ">3001<"))) == null);

            var utf8 = System.Text.Encoding.UTF8;
            var proj = Path.Combine(root, "Hmi.hmiproj");
            Check(HmiPolicy.PortRestore(proj, utf8.GetBytes(ports), utf8.GetBytes(ports.Replace(">3000<", ">3001<"))).SequenceEqual(utf8.GetBytes(ports)));
            Check(HmiPolicy.ServerConfig(proj) == Path.Combine(root, "Server", "TcHmiSrv", "TcHmiSrv.Config.default.json"));
            const string cfg = "{\n    \"SYMBOLS\": {\"s\": 1, \"n\": {\"ENDPOINTS\": [\"http://127.0.0.1:3000\"]}},\n"
                + "    \"ENDPOINTS\": [\n        \"http://127.0.0.1:13000\",\n        \"http://127.0.0.1:3000\"\n    ],\n    \"Z\": \"]\"\n}";
            string Bumped(string text) => text.Replace("1:13000\"", "1:13001\"").Replace("    \"http://127.0.0.1:3000\"", "    \"http://127.0.0.1:3001\"");
            string Restore(string original, string current, string path = null)
            {
                var r = HmiPolicy.PortRestore(path ?? HmiPolicy.ServerConfig(proj), utf8.GetBytes(original), utf8.GetBytes(current));
                return r == null ? null : utf8.GetString(r);
            }
            // A refresh changed SYMBOLS while the server bumped ENDPOINTS: only ENDPOINTS comes back.
            var refreshed = Bumped(cfg).Replace("\"s\": 1", "\"s\": 2");
            Check(Bumped(cfg) != cfg && Restore(cfg, refreshed) == cfg.Replace("\"s\": 1", "\"s\": 2"));
            Check(Restore(cfg, cfg.Replace("\"s\": 1", "\"s\": 2")) == null);
            Check(Restore(cfg, Bumped(cfg).Replace("127.0.0.1:13001", "127.0.0.2:13001")) == null);
            Check(Restore(cfg, Bumped(cfg).Replace("\"http://127.0.0.1:3001\"", "\"http://127.0.0.1:3001\", \"http://127.0.0.1:3002\"")) == null);
            Check(Restore(cfg.Replace("1:13000\"", "1:13000/x\""), Bumped(cfg)) == null);
            // The nested ENDPOINTS is not the server's: it stays as the current file has it.
            var nested = Bumped(cfg).Replace("[\"http://127.0.0.1:3000\"]", "[\"http://127.0.0.1:3005\"]");
            Check(Restore(cfg, nested) == cfg.Replace("[\"http://127.0.0.1:3000\"]", "[\"http://127.0.0.1:3005\"]"));
            var twice = cfg.Replace("{\n    \"SYMBOLS\"", "{\n    \"ENDPOINTS\": [],\n    \"SYMBOLS\"");
            Check(Restore(twice, Bumped(twice)) == null);
            var scalar = "{\"ENDPOINTS\": \"http://127.0.0.1:3000\"}";
            Check(Restore(scalar, scalar.Replace(":3000", ":3001")) == null);
            var noTop = cfg.Replace("\"ENDPOINTS\": [\n", "\"E\": [\n");
            Check(Restore(noTop, Bumped(noTop)) == null);
            // A "]" inside a string must not end the array early.
            var bracket = cfg.Replace("\"http://127.0.0.1:3000\"\n", "\"http://127.0.0.1:3000\", \"a]b\"\n");
            Check(Restore(bracket, Bumped(bracket)) == bracket);
            var commented = cfg.Replace("    \"ENDPOINTS\"", "    // note\n    \"ENDPOINTS\"");
            Check(Restore(commented, Bumped(commented)) == commented);
            Check(Restore(cfg, Bumped(cfg).Substring(0, cfg.Length - 2)) == null);
            Check(Restore(cfg, Bumped(cfg) + " {}") == null);
            var https = cfg.Replace("http://127.0.0.1:13000", "https://127.0.0.1:13000");
            Check(Restore(https, Bumped(https)) == null);
            // A comment in the array and a nested array are not masked.
            var inArray = cfg.Replace("\"http://127.0.0.1:3000\"\n", "\"http://127.0.0.1:3000\" /* \"http://127.0.0.1:4000\" */\n");
            Check(Restore(inArray, Bumped(inArray)) == null);
            var deep = cfg.Replace("\"http://127.0.0.1:3000\"\n", "[\"http://127.0.0.1:3000\"]\n");
            Check(Restore(deep, Bumped(deep).Replace("[\"http://127.0.0.1:3000\"]\n", "[\"http://127.0.0.1:3001\"]\n")) == null);
            Check(Restore(cfg, Bumped(cfg).Replace(",\n        \"http", ", \"http")) == null);

            // A server lock still held blocks the restore; a free or missing one does not.
            var lockDir = Path.Combine(root, ".engineering_servers", "Hmi");
            Directory.CreateDirectory(lockDir);
            var lockFile = Path.Combine(lockDir, "TcHmiSrv.lock");
            var sln0 = Path.Combine(root, "Hmi.sln");
            var none = HmiPolicy.HoldServerLocks(sln0, new[] { proj });
            Check(none != null && none.Count == 0);
            File.WriteAllText(lockFile, "");
            using (var server = new FileStream(lockFile, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
                Check(HmiPolicy.HoldServerLocks(sln0, new[] { proj }) == null
                    && HmiPolicy.HoldServerLocks(Path.Combine(root, "Other", "X.sln"), new[] { Path.Combine(root, "Hmi", "Hmi.hmiproj") }) == null
                    && HmiPolicy.HoldServerLocks(Path.Combine(root, "Other", "X.sln"), new[] { Path.Combine(root, "Sub", "Panel", "Panel.hmiproj") }).Count == 0);
            var free = HmiPolicy.HoldServerLocks(sln0, new[] { proj });
            Check(free != null && free.Count == 1);
            // The restore keeps the lock exclusive: a server cannot open it meanwhile.
            try { new FileStream(lockFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite).Dispose(); Check(false); } catch (IOException) { Check(true); }
            foreach (var held in free) held.Dispose();
            new FileStream(lockFile, FileMode.Open, FileAccess.Read, FileShare.Read).Dispose();
            var bom = new byte[] { 0xEF, 0xBB, 0xBF };
            var withBom = HmiPolicy.PortRestore("c.json", bom.Concat(utf8.GetBytes(cfg)).ToArray(), bom.Concat(utf8.GetBytes(Bumped(cfg))).ToArray());
            Check(withBom != null && withBom.SequenceEqual(bom.Concat(utf8.GetBytes(cfg))));
            Check(HmiPolicy.PortRestore("c.json", u16.GetBytes(cfg), u16.GetBytes(Bumped(cfg))) == null);
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
