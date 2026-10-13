// What GET /api/cameras returns for each RTSP camera.
// State: starting | running | retrying. LastError never contains the stream URL or credentials.
public sealed record CameraStatus(string Camera, string State, DateTimeOffset? LastDataAt, int Restarts, string? LastError);
