// What GET /api/cameras returns for each RTSP camera.
// State: starting | running | retrying. LastError never contains the stream URL or credentials.
// Mode: segments (fixed-length recordings) | live (event clips). The last four fields are only filled in live mode:
// how many people the detector sees right now, when one was last seen, and whether a clip is being recorded.
public sealed record CameraStatus(string Camera, string State, DateTimeOffset? LastDataAt, int Restarts, string? LastError,
    int? People = null, DateTimeOffset? LastPersonAt = null, bool Recording = false, string Mode = "segments");
