using System;
using System.Text.Json;
namespace TcAutomation.Commands
{
    public static class ScopeExportCommand
    {
        public static int Execute(string inputPath, string outputPath, string format)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { success = false, code = "unsupported_integration",
                errorMessage = "Scope export is disabled pending a qualified native data-capture/export implementation. No data or sample counts were generated." }));
            return 1;
        }
    }
}
