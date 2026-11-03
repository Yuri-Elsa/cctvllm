using System.Text.Json;

public sealed record FlagVerdict(string Suspicious, double? Score, string Weapon);

public static class FlagClassifier
{
    private const string System =
        "Anda adalah penyaring awal untuk kamera pengawas. Jawab hanya dengan JSON sesuai skema. " +
        "Alarm palsu jauh lebih baik daripada kejadian yang terlewat.";

    internal static string Question(ModelSettings config, bool scene)
    {
        var text =
            "Gambar-gambar ini adalah sampel kronologis dari satu video kamera pengawas. Putuskan dua tanda berikut langsung dari gambar.\n" +
            "\"suspicious\": jawab \"yes\" jika terlihat salah satu dari: berkeliaran atau berulang kali kembali ke tempat yang sama, aktivitas di tempat atau waktu yang tidak semestinya, " +
            "seseorang menangani, membuka, memaksa, atau membawa pergi barang, memanjat atau masuk dengan cara tidak wajar, pengejaran, melarikan diri atau berlari tiba-tiba, " +
            "konfrontasi atau kontak fisik, terjatuh, tabrakan, cedera, api, asap, atau tindakan tidak aman lainnya. Jika ragu, jawab \"yes\". Jawab \"no\" hanya jika semuanya jelas kegiatan rutin.\n";
        text += config.WeaponEnabled
            ? "\"weapon\": jawab \"yes\" jika ada senjata atau benda berbahaya yang terlihat (pistol, pisau, tongkat pemukul, linggis, atau serupa), dibawa maupun tergeletak; jika tidak ada, \"no\"."
            : "\"weapon\": pemeriksaan senjata dinonaktifkan, selalu jawab \"no\".";
        if (!string.IsNullOrWhiteSpace(config.FlagActivity))
            text += "\nAturan penandaan khusus yang wajib dipakai (menggantikan kriteria suspicious umum): " + JsonSerializer.Serialize(config.FlagActivity.Trim());
        if (config.SiteContextEnabled && !string.IsNullOrWhiteSpace(config.SiteContext))
            text += "\nKonteks lokasi (hanya latar belakang, bukan bukti): " + JsonSerializer.Serialize(config.SiteContext.Trim());
        if (scene) text += "\n" + DescriptionWorker.SceneNote;
        return text;
    }

    internal static FlagVerdict Combine(IEnumerable<FlagVerdict> verdicts)
    {
        var list = verdicts.ToList();
        var scores = list.Where(v => v.Score is not null).Select(v => v.Score!.Value).ToList();
        return new FlagVerdict(
            list.Any(v => v.Suspicious == "yes") ? "yes" : "no",
            scores.Count > 0 ? scores.Max() : null,
            list.Any(v => v.Weapon == "yes") ? "yes" : "no");
    }

    public static async Task<FlagVerdict> Run(HttpClient client, ModelSettings config, IReadOnlyList<string[]> groups, bool scene,
        Action<int, int> progress, CancellationToken ct)
    {
        var results = new FlagVerdict[groups.Count];
        var done = 0;
        await Parallel.ForEachAsync(Enumerable.Range(0, groups.Count),
            new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(config.LlmConcurrency, 1, 8), CancellationToken = ct },
            async (i, token) =>
            {
                results[i] = await Classify(client, config, groups[i], scene, token);
                progress(Interlocked.Increment(ref done), groups.Count);
            });
        return Combine(results);
    }

    internal static async Task<FlagVerdict> Classify(HttpClient client, ModelSettings config, string[] paths, bool scene, CancellationToken ct)
    {
        var content = new List<object>();
        foreach (var path in paths)
            content.Add(new { type = "image_url", image_url = new { url = "data:image/png;base64," + Convert.ToBase64String(await File.ReadAllBytesAsync(path, ct)) } });
        content.Add(new { type = "text", text = Question(config, scene) });
        object Request(bool scores) => new
        {
            model = config.Model,
            messages = new object[]
            {
                new { role = "system", content = System },
                new { role = "user", content }
            },
            response_format = new
            {
                type = "json_schema",
                json_schema = new
                {
                    name = "flags",
                    strict = true,
                    schema = new
                    {
                        type = "object",
                        additionalProperties = false,
                        properties = new
                        {
                            suspicious = new { type = "string", @enum = new[] { "yes", "no" } },
                            weapon = new { type = "string", @enum = new[] { "yes", "no" } }
                        },
                        required = new[] { "suspicious", "weapon" }
                    }
                }
            },
            max_tokens = 40,
            temperature = 0,
            seed = 7,
            reasoning_effort = "none",
            logprobs = scores,
            top_logprobs = scores ? Scoring.TopLogprobs : 0
        };
        using var doc = await ModelCalls.Ask(client, config, Request, true, ct);
        var choice = doc.RootElement.GetProperty("choices")[0];
        using var parsed = DescriptionWorker.ParseObject(ModelCalls.Content(choice.GetProperty("message")));
        string? Flag(string key) => parsed.RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()?.Trim().ToLowerInvariant() : null;
        var suspicious = Flag("suspicious");
        var weapon = Flag("weapon");
        if (suspicious is not ("yes" or "no") || weapon is not ("yes" or "no"))
            throw new UserError("The model returned an invalid flag answer. No flags were assumed; retry this video.", 502);
        return new FlagVerdict(suspicious, Scoring.PYes(choice, "suspicious"), config.WeaponEnabled ? weapon : "no");
    }
}