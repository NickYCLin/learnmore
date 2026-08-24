namespace LearnMore.Options;

public sealed class VocalOnsetDetectionOptions
{
    public string PythonPath { get; set; } = string.Empty;
    public string HuggingFaceCacheRoot { get; set; } = string.Empty;
    public string FfmpegPath { get; set; } = string.Empty;
    public string OpenAiApiKey { get; set; } = string.Empty;
    public string OpenAiMyApiKey { get; set; } = string.Empty;
    public string InitialSegmentationHighAccuracyModel { get; set; } = string.Empty;
    public string InitialSegmentationHighAccuracyFallbackModel { get; set; } = string.Empty;
    public string SecondaryAlignmentModel { get; set; } = string.Empty;
    public string SecondaryAlignmentPythonPath { get; set; } = string.Empty;
    public bool UseRemoteHighAccuracyApi { get; set; }
    public bool RemoteHighAccuracyApiFallbackToLocal { get; set; } = false;
    public string RemoteHighAccuracyApiBaseUrl { get; set; } = string.Empty;
    public string RemoteHighAccuracyApiToken { get; set; } = string.Empty;
    public int RemoteHighAccuracyApiTimeoutSeconds { get; set; } = 1800;
}
