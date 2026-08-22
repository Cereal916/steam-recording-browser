namespace SteamRecordingBrowser.Models;

public readonly record struct StartupProgress(
    double Percent,
    string Status);
