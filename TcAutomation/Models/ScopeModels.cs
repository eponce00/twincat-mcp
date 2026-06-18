using System.Collections.Generic;

namespace TcAutomation.Models
{
    public class ScopeCreateResult
    {
        public bool Success { get; set; }
        public string ConfigPath { get; set; } = "";
        public int ChannelCount { get; set; }
        public int SampleTimeMs { get; set; }
        public double? RecordTimeSec { get; set; }
        public List<string> Variables { get; set; } = new List<string>();
        public string? ErrorMessage { get; set; }
    }

    public class ScopeSessionResponse
    {
        public bool Success { get; set; }
        public string State { get; set; } = "";
        public string? ConfigPath { get; set; }
        public string? DataPath { get; set; }
        public string? Format { get; set; }
        public double ElapsedSeconds { get; set; }
        public int SamplesCollected { get; set; }
        public int ChannelCount { get; set; }
        public List<string> Columns { get; set; } = new List<string>();
        public string? StartedAt { get; set; }
        public long? FileSizeKB { get; set; }
        public string? ErrorMessage { get; set; }
    }

    public class ScopeExportResult
    {
        public bool Success { get; set; }
        public string InputPath { get; set; } = "";
        public string OutputPath { get; set; } = "";
        public string Format { get; set; } = "";
        public long FileSizeKB { get; set; }
        public string? ErrorMessage { get; set; }
    }

    public class AdsRecordResult
    {
        public bool Success { get; set; }
        public string OutputPath { get; set; } = "";
        public int ChannelCount { get; set; }
        public int SamplesCollected { get; set; }
        public double DurationSeconds { get; set; }
        public int SampleTimeMs { get; set; }
        public List<string> Variables { get; set; } = new List<string>();
        public long FileSizeKB { get; set; }
        public string? ErrorMessage { get; set; }
        public string? StartTrigger { get; set; }
        public string? StopTrigger { get; set; }
        public string? TriggerStatus { get; set; }
    }
}
