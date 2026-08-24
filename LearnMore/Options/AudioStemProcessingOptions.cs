namespace LearnMore.Options;

public sealed class AudioStemProcessingOptions
{
    public bool Enabled { get; set; } = true;
    public string PythonPath { get; set; } = string.Empty;
    public string YtDlpPath { get; set; } = string.Empty;
    public string FfmpegPath { get; set; } = string.Empty;
    public string YtDlpCookiesPath { get; set; } = string.Empty;
    public string WorkRoot { get; set; } = string.Empty;
    public string ModelName { get; set; } = "htdemucs";
    public string Device { get; set; } = string.Empty;
    public bool UseRemoteApi { get; set; }
    public bool RemoteApiFallbackToLocal { get; set; } = true;
    public string RemoteApiBaseUrl { get; set; } = string.Empty;
    public string RemoteApiToken { get; set; } = string.Empty;
    public int RemoteApiTimeoutSeconds { get; set; } = 3600;
    public int RemoteApiDownloadTimeoutSeconds { get; set; } = 900;
    public double SegmentSeconds { get; set; } = 7.0;
    public int Jobs { get; set; } = 0;
    public int Shifts { get; set; } = 0;
    public int PollIntervalSeconds { get; set; } = 60;
    public int MaxAttempts { get; set; } = 3;
    public int LeaseMinutes { get; set; } = 90;
    public int RetryDelayMinutes { get; set; } = 30;
    public int DownloadTimeoutSeconds { get; set; } = 900;
    public int SeparationTimeoutSeconds { get; set; } = 3600;
    public int ConversionTimeoutSeconds { get; set; } = 600;
    public bool NormalizeLoudness { get; set; } = true;
    public double TargetIntegratedLufs { get; set; } = -12.5;
    public double TargetLoudnessRange { get; set; } = 11.0;
    public double TargetTruePeakDb { get; set; } = -1.5;
}
