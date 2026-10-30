public sealed record Detection(string[] Paths, string[] PlainPaths, double[] Timestamps, string Evidence, string Motion,
    PersonSample[] Samples, int LlmChecked = 0, int LlmRejected = 0);