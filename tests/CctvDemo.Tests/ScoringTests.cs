using System.Text.Json;
using Xunit;

public class ScoringTests
{
    // ---- Decide: p_yes >= threshold ----

    [Theory]
    [InlineData(0.80, 0.5, "yes")]
    [InlineData(0.50, 0.5, "yes")]    // exactly on the threshold counts as flagged
    [InlineData(0.49, 0.5, "no")]
    [InlineData(0.00, 0.5, "no")]
    [InlineData(0.30, 0.25, "yes")]
    [InlineData(0.30, 0.90, "no")]
    public void Decide_flags_when_the_score_reaches_the_threshold(double score, double threshold, string expected)
    {
        var (decision, used) = Scoring.Decide("no", score, threshold);
        Assert.Equal(expected, decision);
        Assert.Equal(score, used);
    }

    [Fact]
    public void Decide_ignores_the_models_own_answer_when_a_score_exists()
    {
        Assert.Equal("yes", Scoring.Decide("no", 0.9, 0.5).Decision);   // model said no, probability says yes
        Assert.Equal("no", Scoring.Decide("yes", 0.1, 0.5).Decision);   // model said yes, probability says no
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("no")]
    public void Decide_falls_back_to_the_models_answer_without_a_score(string answer)
    {
        var (decision, score) = Scoring.Decide(answer, null, 0.5);
        Assert.Equal(answer, decision);
        Assert.Null(score);
    }

    [Fact]
    public void Threshold_zero_flags_everything_and_threshold_one_needs_certainty()
    {
        Assert.Equal("yes", Scoring.Decide("no", 0.0, 0.0).Decision);
        Assert.Equal("no", Scoring.Decide("yes", 0.999, 1.0).Decision);
        Assert.Equal("yes", Scoring.Decide("no", 1.0, 1.0).Decision);
    }

    [Fact]
    public void Effective_uses_the_score_or_maps_the_answer_to_zero_or_one()
    {
        Assert.Equal(0.37, Scoring.Effective("yes", 0.37));
        Assert.Equal(1d, Scoring.Effective("yes", null));
        Assert.Equal(0d, Scoring.Effective("no", null));
    }

    // ---- PYes: reading probabilities out of logprobs ----

    private static LogprobToken Tok(string token, double logProb, params (string Token, double LogProb)[] top) =>
        new(token, logProb, top);

    // {"activity_description":"x","suspicious":"<value>","weapon":"no"}, with the value as its own token.
    private static List<LogprobToken> Answer(LogprobToken value) =>
    [
        Tok("{\"activity_description\":\"x\",", -0.1),
        Tok("\"suspicious\"", -0.1), Tok(":", -0.1), Tok(" \"", -0.1),
        value,
        Tok("\",", -0.1), Tok("\"weapon\"", -0.1), Tok(":\"no\"}", -0.1)
    ];

    [Fact]
    public void PYes_is_the_yes_share_of_the_yes_and_no_alternatives()
    {
        var tokens = Answer(Tok("yes", Math.Log(0.8), ("yes", Math.Log(0.8)), ("no", Math.Log(0.2))));
        Assert.Equal(0.8, Scoring.PYes(tokens, "suspicious")!.Value, 6);
    }

    [Fact]
    public void PYes_renormalises_when_other_tokens_take_probability_mass()
    {
        var tokens = Answer(Tok("yes", Math.Log(0.3),
            ("yes", Math.Log(0.3)), ("no", Math.Log(0.1)), ("maybe", Math.Log(0.5))));
        Assert.Equal(0.75, Scoring.PYes(tokens, "suspicious")!.Value, 6);   // 0.3 / (0.3 + 0.1); "maybe" is ignored
    }

    [Fact]
    public void PYes_recognises_case_whitespace_and_quote_variants()
    {
        var tokens = Answer(Tok("Yes", Math.Log(0.5),
            ("Yes", Math.Log(0.3)), (" yes", Math.Log(0.2)), ("\"no", Math.Log(0.5))));
        Assert.Equal(0.5, Scoring.PYes(tokens, "suspicious")!.Value, 6);
    }

    [Fact]
    public void PYes_without_alternatives_uses_the_chosen_token_alone()
    {
        Assert.Equal(1d, Scoring.PYes(Answer(Tok("yes", -0.01)), "suspicious"));
        Assert.Equal(0d, Scoring.PYes(Answer(Tok("no", -0.01)), "suspicious"));
    }

    [Fact]
    public void PYes_reads_the_requested_key_and_not_a_neighbour()
    {
        var tokens = new List<LogprobToken>
        {
            Tok("{\"suspicious\":\"", -0.1), Tok("no", -0.1, ("no", Math.Log(0.9)), ("yes", Math.Log(0.1))),
            Tok("\",\"weapon\":\"", -0.1), Tok("yes", -0.1, ("yes", Math.Log(0.6)), ("no", Math.Log(0.4))),
            Tok("\"}", -0.1)
        };
        Assert.Equal(0.1, Scoring.PYes(tokens, "suspicious")!.Value, 6);
        Assert.Equal(0.6, Scoring.PYes(tokens, "weapon")!.Value, 6);
    }

    [Fact]
    public void PYes_is_not_fooled_by_the_key_name_appearing_inside_the_description()
    {
        var tokens = new List<LogprobToken>
        {
            Tok("{\"activity_description\":\"Orang membaca papan \\\"suspicious\\\" di dinding.\",\"suspicious\":\"", -0.1),
            Tok("no", -0.1, ("no", Math.Log(0.95)), ("yes", Math.Log(0.05))),
            Tok("\"}", -0.1)
        };
        Assert.Equal(0.05, Scoring.PYes(tokens, "suspicious")!.Value, 6);
    }

    [Fact]
    public void PYes_skips_nan_logprobs()
    {
        var tokens = Answer(Tok("yes", -0.1, ("yes", Math.Log(0.5)), ("no", double.NaN), ("no", Math.Log(0.5))));
        Assert.Equal(0.5, Scoring.PYes(tokens, "suspicious")!.Value, 6);
    }

    [Fact]
    public void PYes_is_null_when_it_cannot_be_determined()
    {
        Assert.Null(Scoring.PYes(new List<LogprobToken>(), "suspicious"));                                      // no tokens
        Assert.Null(Scoring.PYes(Answer(Tok("yes", -0.1)), "no_such_key"));                                     // key absent
        Assert.Null(Scoring.PYes(Answer(Tok("maybe", -0.1, ("maybe", -0.1), ("perhaps", -0.2))), "suspicious")); // nothing classifiable
        Assert.Null(Scoring.PYes(new List<LogprobToken> { Tok("{\"suspicious\"", -0.1) }, "suspicious"));       // cut off before the value
    }

    // ---- ReadTokens / PYes(JsonElement): the OpenAI response shape ----

    private static JsonElement Choice(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void A_choice_with_logprobs_yields_a_score()
    {
        var choice = Choice("""
        {"message":{"content":"x"},"logprobs":{"content":[
          {"token":"{\"suspicious\":\"","logprob":-0.1,"top_logprobs":[]},
          {"token":"yes","logprob":-0.22,"top_logprobs":[{"token":"yes","logprob":-0.2231},{"token":"no","logprob":-1.6094}]},
          {"token":"\"}","logprob":-0.1,"top_logprobs":[]}
        ]}}
        """);
        var p = Scoring.PYes(choice, "suspicious");
        Assert.NotNull(p);
        Assert.Equal(0.8, p!.Value, 2);
    }

    [Theory]
    [InlineData("""{"message":{"content":"x"}}""")]                                           // no logprobs at all
    [InlineData("""{"logprobs":null}""")]                                                      // provider returned null
    [InlineData("""{"logprobs":{"content":null}}""")]
    [InlineData("""{"logprobs":{"content":[{"logprob":-1}]}}""")]                              // token field missing
    public void A_choice_without_usable_logprobs_gives_no_score(string json)
    {
        Assert.Null(Scoring.ReadTokens(Choice(json)));
        Assert.Null(Scoring.PYes(Choice(json), "suspicious"));
    }
}
