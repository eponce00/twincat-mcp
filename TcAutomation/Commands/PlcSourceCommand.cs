using System;
using System.Security.Cryptography;
using System.Text;
using TCatSysManagerLib;
using TcAutomation.Core;

namespace TcAutomation.Commands
{
    public static class PlcSourceCommand
    {
        public static PlcSourceResult ExecuteInSession(VisualStudioInstance vs, string target,
            string plcName, string path, string section, string newText, string expectedSha256)
        {
            var result = new PlcSourceResult { Path = path, Section = section };
            try
            {
                var automation = new AutomationInterface(vs);
                if (string.IsNullOrWhiteSpace(target) || automation.TargetNetId != target ||
                    string.IsNullOrWhiteSpace(plcName) || plcName.Contains("^"))
                    throw new ArgumentException("Explicit matching target and PLC required.");
                var plc = ((ITcProjectRoot)automation.SystemManager.LookupTreeItem("TIPC^" + plcName)).NestedProject;
                if (path == null || !path.StartsWith(plc.PathName + "^", StringComparison.Ordinal) ||
                    (section != "declaration" && section != "implementation"))
                    throw new ArgumentException("Select a POU inside the requested PLC and its declaration or implementation section.");
                var pou = automation.SystemManager.LookupTreeItem(path);
                string current = section == "declaration" ? ((ITcPlcDeclaration)pou).DeclarationText : ((ITcPlcImplementation)pou).ImplementationText;
                if (newText != null)
                {
                    if (!MatchingLoginCommand.IsLoggedIn(plc))
                        throw new InvalidOperationException("Establish matching login before editing.");
                    if (!string.Equals(Hash(current), expectedSha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Source changed since it was read; expected SHA256 mismatch.");
                    result.EditAttempted = true;
                    if (section == "declaration") ((ITcPlcDeclaration)pou).DeclarationText = newText;
                    else ((ITcPlcImplementation)pou).ImplementationText = newText;
                    vs.Dte.ExecuteCommand("File.SaveAll", "");
                    current = section == "declaration" ? ((ITcPlcDeclaration)pou).DeclarationText : ((ITcPlcImplementation)pou).ImplementationText;
                }
                result.Text = current;
                result.Sha256 = Hash(current);
                result.Success = true;
            }
            catch (Exception ex) { result.ErrorMessage = ex.Message; }
            return result;
        }
        static string Hash(string text)
        {
            using (var hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant();
        }
    }
    public class PlcSourceResult
    {
        public bool Success { get; set; }
        public bool EditAttempted { get; set; }
        public string Path { get; set; }
        public string Section { get; set; }
        public string Text { get; set; }
        public string Sha256 { get; set; }
        public string ErrorMessage { get; set; }
    }
}
