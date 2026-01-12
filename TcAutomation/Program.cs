using System;
using System.CommandLine;
using System.Text.Json;
using System.Threading.Tasks;
using TcAutomation.Commands;
using TcAutomation.Core;

namespace TcAutomation
{
    /// <summary>
    /// TcAutomation CLI - TwinCAT Automation Interface wrapper
    /// 
    /// This tool provides command-line access to TwinCAT automation features
    /// with JSON output for easy integration with MCP servers and other tools.
    /// 
    /// Usage:
    ///   TcAutomation.exe build --solution "C:\path\to\solution.sln"
    ///   TcAutomation.exe info --solution "C:\path\to\solution.sln"
    ///   TcAutomation.exe clean --solution "C:\path\to\solution.sln"
    ///   TcAutomation.exe set-target --solution "C:\path\to\solution.sln" --amsnetid "5.22.157.86.1.1"
    ///   TcAutomation.exe activate --solution "C:\path\to\solution.sln" --amsnetid "5.22.157.86.1.1"
    ///   TcAutomation.exe restart --solution "C:\path\to\solution.sln" --amsnetid "5.22.157.86.1.1"
    ///   TcAutomation.exe deploy --solution "C:\path\to\solution.sln" --amsnetid "5.22.157.86.1.1"
    /// </summary>
    class Program
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        [STAThread] // Required for COM STA thread
        static int Main(string[] args)
        {
            // Register COM message filter for retry logic
            MessageFilter.Register();
            
            try
            {
                return MainAsync(args).GetAwaiter().GetResult();
            }
            finally
            {
                MessageFilter.Revoke();
            }
        }

        static async Task<int> MainAsync(string[] args)
        {
            // Root command
            var rootCommand = new RootCommand("TwinCAT Automation CLI - Build, deploy, and manage TwinCAT projects");

            // Common options
            var solutionOption = new Option<string>(
                aliases: new[] { "--solution", "-s" },
                description: "Path to the TwinCAT solution file (.sln)");
            solutionOption.IsRequired = true;
            
            var tcVersionOption = new Option<string?>(
                aliases: new[] { "--tcversion", "-v" },
                description: "Force specific TwinCAT version (e.g., '3.1.4026.17')");
            
            var amsNetIdOption = new Option<string>(
                aliases: new[] { "--amsnetid", "-a" },
                description: "Target AMS Net ID (e.g., '5.22.157.86.1.1')");

            // === BUILD COMMAND ===
            var buildCommand = new Command("build", "Build a TwinCAT solution and return errors/warnings");
            
            var buildSolutionOpt = CreateSolutionOption();
            var buildTcVersionOpt = CreateTcVersionOption();
            var cleanOption = new Option<bool>(
                aliases: new[] { "--clean", "-c" },
                description: "Clean solution before building",
                getDefaultValue: () => true);
            
            buildCommand.AddOption(buildSolutionOpt);
            buildCommand.AddOption(cleanOption);
            buildCommand.AddOption(buildTcVersionOpt);
            
            buildCommand.SetHandler(async (string solution, bool clean, string? tcVersion) =>
            {
                var result = await BuildCommand.ExecuteAsync(solution, clean, tcVersion);
                Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
            }, buildSolutionOpt, cleanOption, buildTcVersionOpt);

            // === INFO COMMAND ===
            var infoCommand = new Command("info", "Get information about a TwinCAT solution");
            var infoSolutionOpt = CreateSolutionOption();
            infoCommand.AddOption(infoSolutionOpt);
            
            infoCommand.SetHandler(async (string solution) =>
            {
                var result = await InfoCommand.ExecuteAsync(solution);
                Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
            }, infoSolutionOpt);

            // === CLEAN COMMAND ===
            var cleanCommand = new Command("clean", "Clean a TwinCAT solution (remove build artifacts)");
            var cleanSolutionOpt = CreateSolutionOption();
            var cleanTcVersionOpt = CreateTcVersionOption();
            cleanCommand.AddOption(cleanSolutionOpt);
            cleanCommand.AddOption(cleanTcVersionOpt);
            
            cleanCommand.SetHandler((string solution, string? tcVersion) =>
            {
                CleanCommand.Execute(solution, tcVersion);
            }, cleanSolutionOpt, cleanTcVersionOpt);

            // === SET-TARGET COMMAND ===
            var setTargetCommand = new Command("set-target", "Set the target AMS Net ID for deployment");
            var setTargetSolutionOpt = CreateSolutionOption();
            var setTargetAmsOpt = CreateAmsNetIdOption(required: true);
            var setTargetTcVersionOpt = CreateTcVersionOption();
            setTargetCommand.AddOption(setTargetSolutionOpt);
            setTargetCommand.AddOption(setTargetAmsOpt);
            setTargetCommand.AddOption(setTargetTcVersionOpt);
            
            setTargetCommand.SetHandler((string solution, string amsNetId, string? tcVersion) =>
            {
                SetTargetCommand.Execute(solution, amsNetId, tcVersion);
            }, setTargetSolutionOpt, setTargetAmsOpt, setTargetTcVersionOpt);

            // === ACTIVATE COMMAND ===
            var activateCommand = new Command("activate", "Activate TwinCAT configuration on target PLC");
            var activateSolutionOpt = CreateSolutionOption();
            var activateAmsOpt = CreateAmsNetIdOption(required: false);
            var activateTcVersionOpt = CreateTcVersionOption();
            activateCommand.AddOption(activateSolutionOpt);
            activateCommand.AddOption(activateAmsOpt);
            activateCommand.AddOption(activateTcVersionOpt);
            
            activateCommand.SetHandler((string solution, string? amsNetId, string? tcVersion) =>
            {
                ActivateCommand.Execute(solution, amsNetId, tcVersion);
            }, activateSolutionOpt, activateAmsOpt, activateTcVersionOpt);

            // === RESTART COMMAND ===
            var restartCommand = new Command("restart", "Restart TwinCAT runtime on target PLC");
            var restartSolutionOpt = CreateSolutionOption();
            var restartAmsOpt = CreateAmsNetIdOption(required: false);
            var restartTcVersionOpt = CreateTcVersionOption();
            restartCommand.AddOption(restartSolutionOpt);
            restartCommand.AddOption(restartAmsOpt);
            restartCommand.AddOption(restartTcVersionOpt);
            
            restartCommand.SetHandler((string solution, string? amsNetId, string? tcVersion) =>
            {
                RestartCommand.Execute(solution, amsNetId, tcVersion);
            }, restartSolutionOpt, restartAmsOpt, restartTcVersionOpt);

            // === DEPLOY COMMAND ===
            var deployCommand = new Command("deploy", "Full deployment: build, activate boot project, activate config, restart TwinCAT");
            var deploySolutionOpt = CreateSolutionOption();
            var deployAmsOpt = CreateAmsNetIdOption(required: true);
            var deployTcVersionOpt = CreateTcVersionOption();
            deployCommand.AddOption(deploySolutionOpt);
            deployCommand.AddOption(deployAmsOpt);
            deployCommand.AddOption(deployTcVersionOpt);
            
            var plcOption = new Option<string?>(
                aliases: new[] { "--plc", "-p" },
                description: "Deploy only this PLC project (e.g., 'CoreExample')");
            deployCommand.AddOption(plcOption);
            
            var skipBuildOption = new Option<bool>(
                aliases: new[] { "--skip-build" },
                description: "Skip building the solution",
                getDefaultValue: () => false);
            deployCommand.AddOption(skipBuildOption);
            
            var dryRunOption = new Option<bool>(
                aliases: new[] { "--dry-run" },
                description: "Show what would be done without making changes",
                getDefaultValue: () => false);
            deployCommand.AddOption(dryRunOption);
            
            deployCommand.SetHandler((string solution, string amsNetId, string? tcVersion, string? plc, bool skipBuild, bool dryRun) =>
            {
                DeployCommand.Execute(solution, amsNetId, plc, tcVersion, skipBuild, dryRun);
            }, deploySolutionOpt, deployAmsOpt, deployTcVersionOpt, plcOption, skipBuildOption, dryRunOption);

            // === ADD COMMANDS TO ROOT ===
            rootCommand.AddCommand(buildCommand);
            rootCommand.AddCommand(infoCommand);
            rootCommand.AddCommand(cleanCommand);
            rootCommand.AddCommand(setTargetCommand);
            rootCommand.AddCommand(activateCommand);
            rootCommand.AddCommand(restartCommand);
            rootCommand.AddCommand(deployCommand);

            return await rootCommand.InvokeAsync(args);
        }

        // Factory methods to create fresh option instances (System.CommandLine requires unique instances)
        private static Option<string> CreateSolutionOption()
        {
            var opt = new Option<string>(
                aliases: new[] { "--solution", "-s" },
                description: "Path to the TwinCAT solution file (.sln)");
            opt.IsRequired = true;
            return opt;
        }

        private static Option<string?> CreateTcVersionOption()
        {
            return new Option<string?>(
                aliases: new[] { "--tcversion", "-v" },
                description: "Force specific TwinCAT version (e.g., '3.1.4026.17')");
        }

        private static Option<string> CreateAmsNetIdOption(bool required)
        {
            var opt = new Option<string>(
                aliases: new[] { "--amsnetid", "-a" },
                description: "Target AMS Net ID (e.g., '5.22.157.86.1.1')");
            opt.IsRequired = required;
            return opt;
        }
    }
}
