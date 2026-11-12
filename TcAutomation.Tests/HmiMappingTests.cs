using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using TcAutomation.Core;

static class HmiMappingTests
{
    public static void Run()
    {
        int cases = 0;
        void Check(bool ok) { cases++; if (!ok) throw new Exception("HMI mapping assertion " + cases); }
        void Refuse(string code, Action action)
        {
            string got = null;
            try { action(); } catch (InvalidOperationException ex) { got = ex.Message; }
            Check(got == code);
        }
        var root = Path.Combine(Path.GetTempPath(), "hmi-mapping-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Server", "ADS"));
        Directory.CreateDirectory(Path.Combine(root, "Server", "ADS2"));
        File.WriteAllText(Path.Combine(root, "Server", "ADS", "ADS.Config.default.json"),
            "{\"RUNTIMES\":{\"PLC1\":{\"ENABLED\":true,\"NETID\":\"1.2.3.4.1.1\",\"PORT\":851},\"OFF\":{\"ENABLED\":false,\"NETID\":\"1.2.3.4.1.1\",\"PORT\":852}}}");
        File.WriteAllText(Path.Combine(root, "Server", "ADS2", "ADS2.Config.default.json"), "{\"RUNTIMES\":{}}");
        const string ext = "\"EXTENSIONS\":{\"ADS\":{\"ENABLED\":true,\"NAME\":\"TcHmiAds\"},\"ADS2\":{\"ENABLED\":false,\"NAME\":\"TcHmiAds\"},\"TcHmiLua\":{\"NAME\":\"TcHmiLua\"}}";
        const string symbols = "\"SYMBOLS\":{"
            + "\"PLC1.GVL.x\":{\"ACCESS\":3,\"DOMAIN\":\"ADS\",\"MAPPING\":\"PLC1::GVL::x\",\"SCHEMA\":{\"$ref\":\"r\"}},"
            + "\"PLC1.GVL.a\":{\"DOMAIN\":\"ADS\",\"MAPPING\":\"PLC1::GVL::a[2]\"},"
            + "\"ADS.Fn\":{\"DOMAIN\":\"ADS\",\"MAPPING\":\"AdsSyncReadReq\",\"HIDDEN\":true},"
            + "\"Tmc.x\":{\"DOMAIN\":\"TMC\",\"MAPPING\":\"Tmc::GVL::x\"},"
            + "\"Sub.x\":{\"DOMAIN\":\"ADS\",\"MAPPING\":\"PLC1::GVL::st\",\"SUBSYMBOL_MAPPING\":\"m\"},"
            + "\"Off.x\":{\"DOMAIN\":\"ADS2\",\"MAPPING\":\"PLC1::GVL::x\"},"
            + "\"Srv.x\":{\"DOMAIN\":\"TcHmiSrv\",\"MAPPING\":\"x\"}}";
        JsonElement Config(string body) => JsonDocument.Parse("{" + body + "}").RootElement;
        try
        {
            var all = HmiMappings.Select(root, Config(ext + "," + symbols), null);
            Check(all.Eligible.Select(m => m.Name).SequenceEqual(new[] { "PLC1.GVL.x", "PLC1.GVL.a" }));
            Check(all.Skipped.Count == 0);
            Check(all.Eligible[0].AdsPath == "GVL.x" && all.Eligible[1].AdsPath == "GVL.a[2]");
            Check(all.Eligible[0].Target == "PLC1::GVL::x" && all.Eligible[0].Domain == "ADS");
            Check(all.Runtimes[all.Eligible[0].Runtime].NetId == "1.2.3.4.1.1" && all.Runtimes[all.Eligible[0].Runtime].Port == 851);
            Check(HmiMappings.Same(all.Eligible[0].Entry, "{\"MAPPING\":\"PLC1::GVL::x\",\"SCHEMA\":{\"$ref\":\"r\"},\"DOMAIN\":\"ADS\",\"ACCESS\":3}"));

            var named = HmiMappings.Select(root, Config(ext + "," + symbols),
                new[] { "PLC1.GVL.x", "ADS.Fn", "Tmc.x", "Sub.x", "Off.x", "Srv.x", "Absent" });
            Check(named.Eligible.Select(m => m.Name).SequenceEqual(new[] { "PLC1.GVL.x" }));
            Check(named.Skipped.Select(s => s.Key + "=" + s.Value).SequenceEqual(new[]
                { "ADS.Fn=not_plc_symbol", "Tmc.x=tmc", "Sub.x=subsymbol", "Off.x=not_ads", "Srv.x=not_ads", "Absent=not_mapped" }));

            // A mapping the refresh cannot address is skipped and named, never touched.
            var off = HmiMappings.Select(root, Config(ext + ",\"SYMBOLS\":{\"y\":{\"DOMAIN\":\"ADS\",\"MAPPING\":\"OFF::GVL::y\"}}"), null);
            Check(off.Eligible.Count == 0 && off.Skipped.Single().Value == "runtime_not_configured");
            foreach (var target in new[] { "PLC1::GVL::x;y", "PLC1::GVL::", "PLC1[0]::GVL::x", "PLC1::GVL::x[a]", "PLC1::GVL.x" })
            {
                var bad = HmiMappings.Select(root, Config(ext + ",\"SYMBOLS\":{\"y\":{\"DOMAIN\":\"ADS\",\"MAPPING\":\"" + target + "\"}}"), null);
                Check(bad.Eligible.Count == 0 && bad.Skipped.Single().Key == "y" && bad.Skipped.Single().Value == "unsupported_target");
            }
            Refuse("hmi_mapping_config_invalid", () => HmiMappings.Select(root, Config(symbols), null));
            // Non-object members are ignored, not dereferenced.
            Check(HmiMappings.Select(root, Config("\"EXTENSIONS\":{\"ADS\":null}," + symbols), null).Eligible.Count == 0);
            Refuse("hmi_mapping_config_invalid", () => HmiMappings.Select(root,
                Config("\"EXTENSIONS\":{\"..\\\\x\":{\"NAME\":\"TcHmiAds\"}}," + symbols), null));
            Refuse("hmi_mapping_limit", () => HmiMappings.Select(root, Config(ext + "," + symbols),
                Enumerable.Range(0, HmiMappings.MaxMappings + 1).Select(i => "n" + i).ToArray()));

            // Merge keeps the new schema, restores each preserved field the original had, and keeps server values otherwise.
            var merged = HmiMappings.Merge(
                "{\"ACCESS\":1,\"HIDDEN\":true,\"OPTIONS\":{},\"DYNAMIC\":true,\"DOMAIN\":\"ADS\",\"SCHEMA\":{\"$ref\":\"old\"}}",
                "{\"ACCESS\":3,\"USEMAPPING\":true,\"DOMAIN\":\"ADS\",\"MAPPING\":\"PLC1::GVL::x\",\"SCHEMA\":{\"$ref\":\"new\"}}");
            Check(HmiMappings.Same(merged,
                "{\"ACCESS\":1,\"HIDDEN\":true,\"OPTIONS\":{},\"DYNAMIC\":true,\"USEMAPPING\":true,\"DOMAIN\":\"ADS\",\"MAPPING\":\"PLC1::GVL::x\",\"SCHEMA\":{\"$ref\":\"new\"}}"));
            var unchanged = "{\"ACCESS\":3,\"USEMAPPING\":true,\"MAPPING\":\"m\",\"SCHEMA\":{\"$ref\":\"r\"}}";
            Check(HmiMappings.Same(HmiMappings.Merge("{\"MAPPING\":\"m\"}", unchanged), unchanged));
            Check(HmiMappings.PreservedFields.Length == 6 && HmiMappings.PreservedFields.Contains("CUSTOMERDATA"));

            Check(HmiMappings.Same("{\"a\":1,\"b\":[1,2]}", "{\"b\":[1,2],\"a\":1.0}"));
            Check(!HmiMappings.Same("{\"b\":[1,2]}", "{\"b\":[2,1]}"));
            Check(!HmiMappings.Same("{\"a\":\"1\"}", "{\"a\":1}"));
            Check(!HmiMappings.Same("{\"a\":1}", "{\"a\":2}"));
            Check(!HmiMappings.Same("{\"a\":9007199254740992}", "{\"a\":9007199254740993}"));
            Check(!HmiMappings.Same("{\"x\":1,\"x\":1}", "{\"x\":1,\"y\":2}"));
            Check(!HmiMappings.Same("{\"x\":1,\"y\":2}", "{\"x\":1,\"x\":1}"));
            Check(!HmiMappings.Same("{\"a\":1e-29}", "{\"a\":2e-29}"));
            Check(!HmiMappings.Same("{\"a\":-1}", "{\"a\":1}"));
            Check(!HmiMappings.Same("{\"a\":0.1e-9223372036854775808}", "{\"a\":1e9223372036854775807}"));
            Check(!HmiMappings.Same("{\"a\":12345678901234567890123456789012}", "{\"a\":12345678901234567890123456789013}"));
            Check(HmiMappings.Same("{\"a\":-1.50e2}", "{\"a\":-150}") && HmiMappings.Same("{\"a\":0.0}", "{\"a\":-0}"));
            Check(HmiMappings.Canonical("120") == "12e1" && HmiMappings.Canonical("0.0120") == "12e-3");
            Check(!HmiMappings.Same("{\"a\":true}", "{\"a\":false}"));
            Check(!HmiMappings.Same("{\"a\":1}", "{\"a\":1,\"b\":null}"));
            Check(!HmiMappings.Same("{\"A\":1}", "{\"a\":1}"));

            var before = Config("\"SYMBOLS\":{\"a\":{\"x\":1},\"b\":{\"x\":1}},\"DEFINITIONS\":{\"T\":{\"p\":1}}");
            var after = Config("\"SYMBOLS\":{\"a\":{\"x\":2},\"c\":{\"x\":1}},\"DEFINITIONS\":{\"T\":{\"p\":1}}");
            Check(HmiMappings.Changed(before, after, "SYMBOLS").SequenceEqual(new[] { "a", "b", "c" }));
            Check(HmiMappings.Changed(before, after, "DEFINITIONS").Count == 0);
            Check(HmiMappings.Entry(before, "a") == "{\"x\":1}" && HmiMappings.Entry(before, "z") == null);
            // A duplicated name counts as changed instead of failing the comparison.
            var dup = Config("\"SYMBOLS\":{\"a\":{\"x\":1},\"a\":{\"x\":1}}");
            Check(HmiMappings.Changed(dup, dup, "SYMBOLS").SequenceEqual(new[] { "a" }));
            Check(HmiMappings.Entry(dup, "a") == null && HmiMappings.Occurrences(dup, "a") == 2 && HmiMappings.Occurrences(dup, "b") == 0);
            Check(HmiMappings.SameTarget("{\"DOMAIN\":\"ADS\",\"MAPPING\":\"PLC1::GVL::x\",\"ACCESS\":3}", "ADS", "PLC1::GVL::x"));
            Check(!HmiMappings.SameTarget("{\"DOMAIN\":\"ADS\",\"MAPPING\":\"PLC1::GVL::y\"}", "ADS", "PLC1::GVL::x"));
            Check(!HmiMappings.SameTarget("{\"DOMAIN\":\"ADS2\",\"MAPPING\":\"PLC1::GVL::x\"}", "ADS", "PLC1::GVL::x"));
            Check(!HmiMappings.SameTarget("{\"MAPPING\":\"PLC1::GVL::x\"}", "ADS", "PLC1::GVL::x"));
            Check(HmiMappings.Kind(dup, "SYMBOLS") == JsonValueKind.Object && HmiMappings.Kind(dup, "DEFINITIONS") == JsonValueKind.Undefined
                && HmiMappings.Kind(Config("\"DEFINITIONS\":null"), "DEFINITIONS") == JsonValueKind.Null);
            var file = Path.Combine(root, "config.json");
            File.WriteAllText(file, "﻿{\"SYMBOLS\":{\"x\":{}},\"EXTENSIONS\":{}}");
            using (var ok = HmiMappings.Read(file)) Check(HmiMappings.Occurrences(ok.RootElement, "x") == 1);
            foreach (var bad in new[] { "{\"SYMBOLS\":{\"x\":{}},\"SYMBOLS\":{}}", "[]", "{" })
            {
                File.WriteAllText(file, bad);
                Refuse("hmi_mapping_config_invalid", () => HmiMappings.Read(file));
            }

            // Recovery record: one per configuration, survives a reread, refuses another configuration's record.
            var state = Path.Combine(root, "state");
            var cfgA = Path.Combine(root, "A", "TcHmiSrv.Config.default.json");
            var cfgB = Path.Combine(root, "B", "TcHmiSrv.Config.default.json");
            const string entry = "{\"DOMAIN\":\"ADS\",\"MAPPING\":\"PLC1::G::s\",\"SCHEMA\":{\"$ref\":\"tchmi:server#/definitions/PLC1.ST_A\"}}";
            Check(HmiRecovery.Read(state, cfgA) == null);
            HmiRecovery.Write(state, new HmiRecovery.Record { ProjectPath = "p", ConfigPath = cfgA, Symbol = "s1", OriginalEntry = entry });
            HmiRecovery.Write(state, new HmiRecovery.Record { ProjectPath = "p", ConfigPath = cfgA.ToUpperInvariant(), Symbol = "s2", OriginalEntry = entry });
            var read = HmiRecovery.Read(state, cfgA);
            Check(read != null && read.Symbol == "s2" && read.OriginalEntry == entry && HmiRecovery.Read(state, cfgB) == null);
            Check(Directory.GetFiles(HmiRecovery.Folder(state)).Length == 1);
            File.Copy(HmiRecovery.PathFor(state, cfgA), HmiRecovery.PathFor(state, cfgB));
            Refuse("hmi_recovery_unreadable", () => HmiRecovery.Read(state, cfgB));
            File.WriteAllText(HmiRecovery.PathFor(state, cfgB), "{");
            Refuse("hmi_recovery_unreadable", () => HmiRecovery.Read(state, cfgB));
            // An entry that is not a JSON object is a corrupt record, not a server fault.
            foreach (var bad in new[] { "[]", "{", "" })
            {
                HmiRecovery.Write(state, new HmiRecovery.Record { ProjectPath = "p", ConfigPath = cfgB, Symbol = "s", OriginalEntry = bad });
                Refuse("hmi_recovery_unreadable", () => HmiRecovery.Read(state, cfgB));
            }
            HmiRecovery.Clear(state, cfgA);
            HmiRecovery.Clear(state, cfgA);
            Check(HmiRecovery.Read(state, cfgA) == null);

            Check(HmiRecovery.Decide(entry, null) == HmiRecovery.Verdict.Restore);
            Check(HmiRecovery.Decide(entry, "{\"MAPPING\":\"PLC1::G::s\",\"SCHEMA\":{\"$ref\":\"tchmi:server#/definitions/PLC1.ST_A\"},\"DOMAIN\":\"ADS\"}")
                == HmiRecovery.Verdict.AlreadyPresent);
            Check(HmiRecovery.Decide(entry, "{\"DOMAIN\":\"ADS\",\"MAPPING\":\"PLC1::G::t\"}") == HmiRecovery.Verdict.Conflict);
            Check(HmiRecovery.Decide(entry, "{\"DOMAIN\":\"ADS2\",\"MAPPING\":\"PLC1::G::s\"}") == HmiRecovery.Verdict.Conflict);
            // A finished remap: same target, refreshed schema, preserved fields still to be set back.
            Check(HmiRecovery.Decide(entry, "{\"DOMAIN\":\"ADS\",\"MAPPING\":\"PLC1::G::s\",\"SCHEMA\":{\"$ref\":\"tchmi:general#/definitions/INT\"}}")
                == HmiRecovery.Verdict.Reconcile);
            Check(HmiRecovery.Decide("{\"MAPPING\":\"PLC1::G::s\"}", "{\"DOMAIN\":\"ADS\",\"MAPPING\":\"PLC1::G::s\"}") == HmiRecovery.Verdict.Conflict);

            // A stopped refresh clears its record only with a certain outcome and the mapping present as it was or as verified.
            const string remapped = "{\"DOMAIN\":\"ADS\",\"MAPPING\":\"PLC1::G::s\",\"SCHEMA\":{\"$ref\":\"tchmi:general#/definitions/INT\"}}";
            Check(HmiRecovery.CanClear(entry, entry, null, true));
            Check(HmiRecovery.CanClear(entry, remapped, remapped, true));
            Check(!HmiRecovery.CanClear(entry, entry, null, false));
            Check(!HmiRecovery.CanClear(entry, remapped, remapped, false));
            Check(!HmiRecovery.CanClear(entry, null, entry, true));
            Check(!HmiRecovery.CanClear(entry, remapped, null, true));
            Check(!HmiRecovery.CanClear(entry, remapped, entry, true));

            // Only another live worker, identified by PID and start time, owns a record.
            Check(!HmiRecovery.OwnerRunning(new HmiRecovery.Record()));
            HmiRecovery.Write(state, new HmiRecovery.Record { ProjectPath = "p", ConfigPath = cfgB, Symbol = "s", OriginalEntry = entry });
            var mine = HmiRecovery.Read(state, cfgB);
            using (var self = System.Diagnostics.Process.GetCurrentProcess())
                Check(mine.WorkerPid == self.Id && !HmiRecovery.OwnerRunning(mine));
            HmiRecovery.Clear(state, cfgB);
            var ping = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ping", "-n 30 127.0.0.1")
                { UseShellExecute = false, CreateNoWindow = true });
            try
            {
                var owner = new HmiRecovery.Record { WorkerPid = ping.Id, WorkerStartUtc = ping.StartTime.ToUniversalTime() };
                Check(HmiRecovery.OwnerRunning(owner));
                Check(!HmiRecovery.OwnerRunning(new HmiRecovery.Record { WorkerPid = ping.Id, WorkerStartUtc = owner.WorkerStartUtc.AddMinutes(-5) }));
                ping.Kill();
                ping.WaitForExit(5000);
                Check(!HmiRecovery.OwnerRunning(owner));
            }
            finally { if (!ping.HasExited) ping.Kill(); ping.Dispose(); }

            // The lock is exclusive per configuration and released with its handle.
            using (var held = HmiRecovery.Lock(state, cfgA))
            {
                Check(held != null && HmiRecovery.Lock(state, cfgA.ToUpperInvariant()) == null);
                using (var other = HmiRecovery.Lock(state, cfgB)) Check(other != null);
            }
            using (var again = HmiRecovery.Lock(state, cfgA)) Check(again != null);
            Check(HmiRecovery.Read(state, cfgA) == null);

            // Definitions the entry needs, followed through "#/definitions/" references between them.
            JsonElement Defs(string body) => JsonDocument.Parse("{\"DEFINITIONS\":{" + body + "}}").RootElement;
            const string a = "\"PLC1.ST_A\":{\"properties\":{\"b\":{\"$ref\":\"#/definitions/PLC1.ST_B\"},\"n\":{\"$ref\":\"tchmi:general#/definitions/INT\"}}}";
            Check(HmiRecovery.MissingDefinitions(entry, Defs(a + ",\"PLC1.ST_B\":{}")).Length == 0);
            Check(HmiRecovery.MissingDefinitions(entry, Defs(a)).SequenceEqual(new[] { "PLC1.ST_B" }));
            Check(HmiRecovery.MissingDefinitions(entry, Defs("\"PLC1.ST_B\":{}")).SequenceEqual(new[] { "PLC1.ST_A" }));
            Check(HmiRecovery.MissingDefinitions(entry, JsonDocument.Parse("{}").RootElement).SequenceEqual(new[] { "PLC1.ST_A" }));
            var cyclic = "\"PLC1.ST_A\":{\"$ref\":\"#/definitions/PLC1.ST_B\"},\"PLC1.ST_B\":{\"items\":[{\"$ref\":\"#/definitions/PLC1.ST_A\"},{\"$ref\":\"#/definitions/PLC1.ST_C\"}]}";
            Check(HmiRecovery.MissingDefinitions(entry, Defs(cyclic)).SequenceEqual(new[] { "PLC1.ST_C" }));
            Check(HmiRecovery.MissingDefinitions("{\"SCHEMA\":{\"$ref\":\"tchmi:general#/definitions/BOOL\"}}", Defs("")).Length == 0);
            Check(HmiRecovery.MissingDefinitions("{\"DOMAIN\":\"ADS\"}", Defs("")).Length == 0);
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine("PASS: " + cases + " HMI mapping policy cases; no XAE, ADS or server access.");
    }
}
