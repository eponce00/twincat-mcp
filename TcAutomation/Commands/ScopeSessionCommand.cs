using System;
using System.Text.Json;
namespace TcAutomation.Commands
{
    public static class ScopeSessionCommand
    {
        public static int Execute()
        {
            Console.WriteLine(JsonSerializer.Serialize(new { success = false, code = "unsupported_integration",
                errorMessage = "Scope recording is disabled pending a qualified native data-capture/export implementation. No data or sample counts were generated." }));
            return 1;
        }
    }
}
