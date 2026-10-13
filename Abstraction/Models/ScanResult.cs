// Same shape the UI already reads from /api/folder and /api/folder/refresh:
// { linkedFolder, added, skipped, errors }.
public sealed record ScanResult(string LinkedFolder, int Added, int Skipped, List<string> Errors);
