using System.Collections.Generic;

namespace TcAutomation.Models
{
    /// <summary>
    /// Result of a build operation.
    /// </summary>
    public class BuildResult
    {
        public bool Success { get; set; }
        public int FailedProjectCount { get; set; }
        public string BuildOutput { get; set; } = "";
        public string BuildTime { get; set; } = "";
        public int ErrorCount { get; set; }
        public int WarningCount { get; set; }
        public List<BuildError> Errors { get; set; } = new List<BuildError>();
        public List<BuildWarning> Warnings { get; set; } = new List<BuildWarning>();
        public string Summary { get; set; } = "";
        public string? ErrorMessage { get; set; }
    }

    /// <summary>
    /// A build error.
    /// </summary>
    public class BuildError
    {
        public string Code { get; set; } = "";
        public string Description { get; set; } = "";
        public string FileName { get; set; } = "";
        public int Line { get; set; }
        public int Column { get; set; }
        public string Project { get; set; } = "";
    }

    /// <summary>
    /// A build warning.
    /// </summary>
    public class BuildWarning
    {
        public string Code { get; set; } = "";
        public string Description { get; set; } = "";
        public string FileName { get; set; } = "";
        public int Line { get; set; }
        public int Column { get; set; }
        public string Project { get; set; } = "";
    }
}
