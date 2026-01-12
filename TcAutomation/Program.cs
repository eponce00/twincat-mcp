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

            // === LIST-PLCS COMMAND ===
            var listPlcsCommand = new Command("list-plcs", "List all PLC projects in a TwinCAT solution");
            var listPlcsSolutionOpt = CreateSolutionOption();
            var listPlcsTcVersionOpt = CreateTcVersionOption();
            listPlcsCommand.AddOption(listPlcsSolutionOpt);
            listPlcsCommand.AddOption(listPlcsTcVersionOpt);
            
            listPlcsCommand.SetHandler((string solution, string? tcVersion) =>
            {
                ListPlcsCommand.Execute(solution, tcVersion);
            }, listPlcsSolutionOpt, listPlcsTcVersionOpt);

            // === SET-BOOT-PROJECT COMMAND ===
            var setBootProjectCommand = new Command("set-boot-project", "Configure boot project settings for PLC projects");
            var setBootSolutionOpt = CreateSolutionOption();
            var setBootTcVersionOpt = CreateTcVersionOption();
            var setBootPlcOpt = new Option<string?>(
                aliases: new[] { "--plc", "-p" },
                description: "Target only this PLC project (by name)");
            var setBootAutostartOpt = new Option<bool>(
                aliases: new[] { "--autostart" },
                description: "Enable boot project autostart",
                getDefaultValue: () => true);
            var setBootGenerateOpt = new Option<bool>(
                aliases: new[] { "--generate" },
                description: "Generate boot project on target",
                getDefaultValue: () => true);
            setBootProjectCommand.AddOption(setBootSolutionOpt);
            setBootProjectCommand.AddOption(setBootTcVersionOpt);
            setBootProjectCommand.AddOption(setBootPlcOpt);
            setBootProjectCommand.AddOption(setBootAutostartOpt);
            setBootProjectCommand.AddOption(setBootGenerateOpt);
            
            setBootProjectCommand.SetHandler((string solution, string? tcVersion, string? plc, bool autostart, bool generate) =>
            {
                SetBootProjectCommand.Execute(solution, tcVersion, plc, autostart, generate);
            }, setBootSolutionOpt, setBootTcVersionOpt, setBootPlcOpt, setBootAutostartOpt, setBootGenerateOpt);

            // === DISABLE-IO COMMAND ===
            var disableIoCommand = new Command("disable-io", "Disable or enable I/O devices (useful for running without physical hardware)");
            var disableIoSolutionOpt = CreateSolutionOption();
            var disableIoTcVersionOpt = CreateTcVersionOption();
            var disableIoEnableOpt = new Option<bool>(
                aliases: new[] { "--enable" },
                description: "Enable I/O devices instead of disabling",
                getDefaultValue: () => false);
            disableIoCommand.AddOption(disableIoSolutionOpt);
            disableIoCommand.AddOption(disableIoTcVersionOpt);
            disableIoCommand.AddOption(disableIoEnableOpt);
            
            disableIoCommand.SetHandler((string solution, string? tcVersion, bool enable) =>
            {
                DisableIoCommand.Execute(solution, tcVersion, enable);
            }, disableIoSolutionOpt, disableIoTcVersionOpt, disableIoEnableOpt);

            // === SET-VARIANT COMMAND ===
            var setVariantCommand = new Command("set-variant", "Get or set the TwinCAT project variant (requires TwinCAT 4024+)");
            var setVariantSolutionOpt = CreateSolutionOption();
            var setVariantTcVersionOpt = CreateTcVersionOption();
            var setVariantNameOpt = new Option<string?>(
                aliases: new[] { "--variant", "-n" },
                description: "Name of the variant to set (omit to just get current variant)");
            var setVariantGetOnlyOpt = new Option<bool>(
                aliases: new[] { "--get" },
                description: "Only get current variant, don't set",
                getDefaultValue: () => false);
            setVariantCommand.AddOption(setVariantSolutionOpt);
            setVariantCommand.AddOption(setVariantTcVersionOpt);
            setVariantCommand.AddOption(setVariantNameOpt);
            setVariantCommand.AddOption(setVariantGetOnlyOpt);
            
            setVariantCommand.SetHandler((string solution, string? tcVersion, string? variant, bool getOnly) =>
            {
                SetVariantCommand.Execute(solution, tcVersion, variant, getOnly);
            }, setVariantSolutionOpt, setVariantTcVersionOpt, setVariantNameOpt, setVariantGetOnlyOpt);

            // === ADD COMMANDS TO ROOT ===
            rootCommand.AddCommand(buildCommand);
            rootCommand.AddCommand(infoCommand);
            rootCommand.AddCommand(cleanCommand);
            rootCommand.AddCommand(setTargetCommand);
            rootCommand.AddCommand(activateCommand);
            rootCommand.AddCommand(restartCommand);
            rootCommand.AddCommand(deployCommand);
            rootCommand.AddCommand(listPlcsCommand);
            rootCommand.AddCommand(setBootProjectCommand);
            rootCommand.AddCommand(disableIoCommand);
            rootCommand.AddCommand(setVariantCommand);

            // === GET-STATE COMMAND (ADS) ===
            var getStateCommand = new Command("get-state", "Get TwinCAT runtime state from a PLC via ADS (no VS required)");
            var getStateAmsOpt = CreateAmsNetIdOption(required: true);
            var getStatePortOpt = new Option<int>(
                aliases: new[] { "--port", "-p" },
                description: "AMS port (default: 851 for PLC runtime)",
                getDefaultValue: () => 851);
            getStateCommand.AddOption(getStateAmsOpt);
            getStateCommand.AddOption(getStatePortOpt);
            
            getStateCommand.SetHandler((string amsNetId, int port) =>
            {
                GetStateCommand.Execute(amsNetId, port);
            }, getStateAmsOpt, getStatePortOpt);

            // === READ-VAR COMMAND (ADS) ===
            var readVarCommand = new Command("read-var", "Read a PLC variable via ADS (no VS required)");
            var readVarAmsOpt = CreateAmsNetIdOption(required: true);
            var readVarPortOpt = new Option<int>(
                aliases: new[] { "--port", "-p" },
                description: "AMS port (default: 851)",
                getDefaultValue: () => 851);
            var readVarSymbolOpt = new Option<string>(
                aliases: new[] { "--symbol", "--var" },
                description: "Symbol/variable name (e.g., 'MAIN.bMyBool', 'GVL.nCounter')");
            readVarSymbolOpt.IsRequired = true;
            readVarCommand.AddOption(readVarAmsOpt);
            readVarCommand.AddOption(readVarPortOpt);
            readVarCommand.AddOption(readVarSymbolOpt);
            
            readVarCommand.SetHandler((string amsNetId, int port, string symbol) =>
            {
                ReadVariableCommand.Execute(amsNetId, port, symbol);
            }, readVarAmsOpt, readVarPortOpt, readVarSymbolOpt);

            // === WRITE-VAR COMMAND (ADS) ===
            var writeVarCommand = new Command("write-var", "Write a value to a PLC variable via ADS (no VS required)");
            var writeVarAmsOpt = CreateAmsNetIdOption(required: true);
            var writeVarPortOpt = new Option<int>(
                aliases: new[] { "--port", "-p" },
                description: "AMS port (default: 851)",
                getDefaultValue: () => 851);
            var writeVarSymbolOpt = new Option<string>(
                aliases: new[] { "--symbol", "--var" },
                description: "Symbol/variable name (e.g., 'MAIN.bMyBool')");
            writeVarSymbolOpt.IsRequired = true;
            var writeVarValueOpt = new Option<string>(
                aliases: new[] { "--value" },
                description: "Value to write (e.g., 'TRUE', '42', '3.14')");
            writeVarValueOpt.IsRequired = true;
            writeVarCommand.AddOption(writeVarAmsOpt);
            writeVarCommand.AddOption(writeVarPortOpt);
            writeVarCommand.AddOption(writeVarSymbolOpt);
            writeVarCommand.AddOption(writeVarValueOpt);
            
            writeVarCommand.SetHandler((string amsNetId, int port, string symbol, string value) =>
            {
                WriteVariableCommand.Execute(amsNetId, port, symbol, value);
            }, writeVarAmsOpt, writeVarPortOpt, writeVarSymbolOpt, writeVarValueOpt);

            // === LIST-TASKS COMMAND ===
            var listTasksCommand = new Command("list-tasks", "List all real-time tasks in a TwinCAT solution");
            var listTasksSolutionOpt = CreateSolutionOption();
            var listTasksTcVersionOpt = CreateTcVersionOption();
            listTasksCommand.AddOption(listTasksSolutionOpt);
            listTasksCommand.AddOption(listTasksTcVersionOpt);
            
            listTasksCommand.SetHandler((string solution, string? tcVersion) =>
            {
                ListTasksCommand.Execute(solution, tcVersion);
            }, listTasksSolutionOpt, listTasksTcVersionOpt);

            // === CONFIGURE-TASK COMMAND ===
            var configureTaskCommand = new Command("configure-task", "Configure a real-time task (enable/disable, autostart)");
            var cfgTaskSolutionOpt = CreateSolutionOption();
            var cfgTaskTcVersionOpt = CreateTcVersionOption();
            var cfgTaskNameOpt = new Option<string>(
                aliases: new[] { "--task", "-t" },
                description: "Task name to configure");
            cfgTaskNameOpt.IsRequired = true;
            var cfgTaskEnableOpt = new Option<bool?>(
                aliases: new[] { "--enable" },
                description: "Enable the task (false to disable)");
            var cfgTaskAutostartOpt = new Option<bool?>(
                aliases: new[] { "--autostart" },
                description: "Set autostart for the task");
            configureTaskCommand.AddOption(cfgTaskSolutionOpt);
            configureTaskCommand.AddOption(cfgTaskTcVersionOpt);
            configureTaskCommand.AddOption(cfgTaskNameOpt);
            configureTaskCommand.AddOption(cfgTaskEnableOpt);
            configureTaskCommand.AddOption(cfgTaskAutostartOpt);
            
            configureTaskCommand.SetHandler((string solution, string taskName, bool? enable, bool? autostart, string? tcVersion) =>
            {
                ConfigureTaskCommand.Execute(solution, taskName, enable, autostart, tcVersion);
            }, cfgTaskSolutionOpt, cfgTaskNameOpt, cfgTaskEnableOpt, cfgTaskAutostartOpt, cfgTaskTcVersionOpt);

            // === CONFIGURE-RT COMMAND ===
            var configureRtCommand = new Command("configure-rt", "Configure real-time CPU settings (cores, load limit)");
            var cfgRtSolutionOpt = CreateSolutionOption();
            var cfgRtTcVersionOpt = CreateTcVersionOption();
            var cfgRtMaxCpusOpt = new Option<int?>(
                aliases: new[] { "--max-cpus" },
                description: "Maximum number of CPUs for real-time (e.g., 1 for single core)");
            var cfgRtLoadLimitOpt = new Option<int?>(
                aliases: new[] { "--load-limit" },
                description: "CPU load limit percentage for real-time (e.g., 80 for 80%)");
            configureRtCommand.AddOption(cfgRtSolutionOpt);
            configureRtCommand.AddOption(cfgRtTcVersionOpt);
            configureRtCommand.AddOption(cfgRtMaxCpusOpt);
            configureRtCommand.AddOption(cfgRtLoadLimitOpt);
            
            configureRtCommand.SetHandler((string solution, int? maxCpus, int? loadLimit, string? tcVersion) =>
            {
                ConfigureRtCommand.Execute(solution, maxCpus, loadLimit, tcVersion);
            }, cfgRtSolutionOpt, cfgRtMaxCpusOpt, cfgRtLoadLimitOpt, cfgRtTcVersionOpt);

            // === SET-STATE COMMAND (ADS) ===
            var setStateCommand = new Command("set-state", "Set TwinCAT runtime state (Run, Stop, Config) via ADS (no VS required)");
            var setStateAmsOpt = CreateAmsNetIdOption(required: true);
            var setStatePortOpt = new Option<int>(
                aliases: new[] { "--port", "-p" },
                description: "AMS port (default: 851 for PLC runtime)",
                getDefaultValue: () => 851);
            var setStateTargetOpt = new Option<string>(
                aliases: new[] { "--state", "-t" },
                description: "Target state: Run, Stop, Config, or Reset");
            setStateTargetOpt.IsRequired = true;
            setStateCommand.AddOption(setStateAmsOpt);
            setStateCommand.AddOption(setStatePortOpt);
            setStateCommand.AddOption(setStateTargetOpt);
            
            setStateCommand.SetHandler((string amsNetId, int port, string state) =>
            {
                SetStateCommand.Execute(amsNetId, port, state);
            }, setStateAmsOpt, setStatePortOpt, setStateTargetOpt);

            // === ADD NEW COMMANDS TO ROOT ===
            rootCommand.AddCommand(getStateCommand);
            rootCommand.AddCommand(setStateCommand);
            rootCommand.AddCommand(readVarCommand);
            rootCommand.AddCommand(writeVarCommand);
            rootCommand.AddCommand(listTasksCommand);
            rootCommand.AddCommand(configureTaskCommand);
            rootCommand.AddCommand(configureRtCommand);

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
