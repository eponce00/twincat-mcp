using System;
using System.Collections.Generic;
using System.Text.Json;
using TwinCAT;
using TwinCAT.Ads;
using TwinCAT.Ads.TypeSystem;

namespace TcAutomation.Commands
{
    /// <summary>
    /// List PLC symbols over ADS, optionally filtered by a prefix. No
    /// solution required — this reads the symbol table directly from the
    /// running PLC so the agent can verify a path exists before trying
    /// to read it.
    ///
    /// Common failure mode the agent hits: after a `run-tcunit` activation
    /// the runtime is technically up but the symbol table we want (the
    /// test project's globals / MAIN FBs) isn't the one currently loaded,
    /// so `read-var` returns `DeviceSymbolNotFound (0x710)` with no hint
    /// of what IS loaded. `list-symbols` with `prefix='MAIN.'` or
    /// `contains='fbStateMachine'` answers the question without guessing.
    /// </summary>
    public static class ListSymbolsCommand
    {
        public static int Execute(
            string amsNetId,
            int port,
            string? prefix,
            string? contains,
            int maxResults,
            bool includeTypes)
        {
            var result = new ListSymbolsResult
            {
                AmsNetId = amsNetId,
                Port = port,
                Prefix = prefix ?? "",
                Contains = contains ?? "",
                MaxResults = maxResults
            };

            try
            {
                using var adsClient = new AdsClient();
                adsClient.Connect(amsNetId, port);

                if (!adsClient.IsConnected)
                {
                    result.ErrorMessage = $"Failed to connect to {amsNetId}:{port}";
                    Emit(result);
                    return 1;
                }

                var stateInfo = adsClient.ReadState();
                result.TargetState = stateInfo.AdsState.ToString();

                // Symbol enumeration needs a live symbol table, which
                // requires the runtime to be in Run or Stop (not Config).
                // If the agent asked this while activating, surface the
                // state explicitly so they know to wait.
                if (stateInfo.AdsState != AdsState.Run
                    && stateInfo.AdsState != AdsState.Stop)
                {
                    result.ErrorMessage =
                        $"PLC is in {stateInfo.AdsState} state — symbol " +
                        "enumeration requires Run or Stop. If the target " +
                        "is rebooting after activation, retry in a few " +
                        "seconds.";
                    Emit(result);
                    return 1;
                }

                var loaderSettings = new SymbolLoaderSettings(
                    SymbolsLoadMode.Flat);
                var loader = SymbolLoaderFactory.Create(adsClient, loaderSettings);

                string prefixLower = (prefix ?? "").ToLowerInvariant();
                string containsLower = (contains ?? "").ToLowerInvariant();
                bool hasPrefix = !string.IsNullOrEmpty(prefixLower);
                bool hasContains = !string.IsNullOrEmpty(containsLower);

                int total = 0;
                int matched = 0;

                foreach (var sym in loader.Symbols)
                {
                    total++;
                    string name = sym.InstancePath ?? sym.InstanceName ?? "";
                    string nameLower = name.ToLowerInvariant();

                    if (hasPrefix && !nameLower.StartsWith(prefixLower))
                        continue;
                    if (hasContains && !nameLower.Contains(containsLower))
                        continue;

                    matched++;
                    if (result.Symbols.Count >= maxResults)
                    {
                        result.Truncated = true;
                        continue; // keep counting so we can report the
                                  // total match count, but don't append
                    }

                    var entry = new ListSymbolsEntry { Name = name };
                    if (includeTypes)
                    {
                        entry.TypeName = sym.TypeName ?? "";
                        entry.Size = sym.Size;
                        // IndexGroup/IndexOffset live on the concrete
                        // TwinCAT.Ads.TypeSystem.Symbol class, not on the
                        // ISymbol interface — cast opportunistically.
                        if (sym is Symbol adsSym)
                        {
                            entry.IndexGroup = adsSym.IndexGroup;
                            entry.IndexOffset = adsSym.IndexOffset;
                        }
                    }
                    result.Symbols.Add(entry);
                }

                result.TotalScanned = total;
                result.TotalMatched = matched;
                result.Success = true;
                Emit(result);
                return 0;
            }
            catch (AdsErrorException ex)
            {
                result.ErrorMessage = $"ADS Error 0x{(uint)ex.ErrorCode:X}: {ex.Message}";
                Emit(result);
                return 1;
            }
            catch (Exception ex)
            {
                result.ErrorMessage = ex.Message;
                Emit(result);
                return 1;
            }
        }

        private static void Emit(ListSymbolsResult r)
        {
            Console.WriteLine(JsonSerializer.Serialize(
                r, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    public class ListSymbolsResult
    {
        public string AmsNetId { get; set; } = "";
        public int Port { get; set; }
        public string Prefix { get; set; } = "";
        public string Contains { get; set; } = "";
        public int MaxResults { get; set; }
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
        public string? TargetState { get; set; }
        public int TotalScanned { get; set; }
        public int TotalMatched { get; set; }
        public bool Truncated { get; set; }
        public List<ListSymbolsEntry> Symbols { get; set; } = new List<ListSymbolsEntry>();
    }

    public class ListSymbolsEntry
    {
        public string Name { get; set; } = "";
        public string? TypeName { get; set; }
        public int Size { get; set; }
        public long IndexGroup { get; set; }
        public long IndexOffset { get; set; }
    }
}
