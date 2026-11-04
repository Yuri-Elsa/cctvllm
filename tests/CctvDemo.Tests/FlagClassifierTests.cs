using System.Text.Json;
using Xunit;

public class FlagClassifierTests
{
    [Fact]
    public void Any_yes_makes_the_video_yes_and_the_score_is_the_highest()
    {
        var v = FlagClassifier.Combine([new("no", 0.10, "no"), new("yes", 0.72, "no"), new("no", 0.30, "yes")]);
        Assert.Equal("yes", v.Suspicious);
        Assert.Equal(0.72, v.Score);
        Assert.Equal("yes", v.Weapon);
    }

    [Fact]
    public void All_no_stays_no_and_missing_scores_stay_missing()
    {
        var v = FlagClassifier.Combine([new("no", null, "no"), new("no", null, "no")]);
        Assert.Equal("no", v.Suspicious);
        Assert.Null(v.Score);
        Assert.Equal("no", v.Weapon);
    }

    [Fact]
    public void Question_carries_the_custom_rule_and_the_weapon_switch()
    {
        var q = FlagClassifier.Question(new ModelSettings { FlagActivity = "masuk area terlarang", WeaponEnabled = false }, false);
        Assert.Contains("masuk area terlarang", q);
        Assert.Contains("dinonaktifkan", q);
        Assert.DoesNotContain("MODE ADEGAN", q);
        Assert.Contains("MODE ADEGAN", FlagClassifier.Question(new ModelSettings(), true));
    }

    [Fact]
    public void Separate_classification_is_off_by_default_and_old_settings_still_load()
    {
        var old = JsonSerializer.Deserialize<ModelSettings>("""{"mode":"local"}""", new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.False(old.SeparateClassification);
    }
}