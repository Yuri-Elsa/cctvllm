using System.Text.Json;
using Xunit;

public class ModelContextTests
{
    // Shape of a real failure: 8166 prompt tokens + 26 generated = 8192, the default Ollama context window.
    private const string ContextFullResponse =
        """{"choices":[{"finish_reason":"length"}],"ollama_response":{"prompt_eval_count":8166,"eval_count":26}}""";

    [Fact]
    public void Token_counts_are_read_from_the_ollama_response()
    {
        using var doc = JsonDocument.Parse(ContextFullResponse);
        Assert.Equal((8166, 26), DescriptionWorker.TokenCounts(doc));
    }

    [Fact]
    public void Token_counts_are_read_from_an_openai_style_usage_block()
    {
        using var doc = JsonDocument.Parse("""{"choices":[{"finish_reason":"length"}],"usage":{"prompt_tokens":100,"completion_tokens":5}}""");
        Assert.Equal((100, 5), DescriptionWorker.TokenCounts(doc));
    }

    [Fact]
    public void Unknown_token_counts_never_count_as_a_full_context()
    {
        using var doc = JsonDocument.Parse("""{"choices":[{"finish_reason":"length"}]}""");
        Assert.Equal(((int?)null, (int?)null), DescriptionWorker.TokenCounts(doc));
        Assert.False(DescriptionWorker.ContextFull(doc, 600));
    }

    [Fact]
    public void An_answer_cut_off_after_a_few_tokens_means_the_context_was_full()
    {
        using var doc = JsonDocument.Parse(ContextFullResponse);
        Assert.True(DescriptionWorker.ContextFull(doc, 600));
    }

    [Fact]
    public void An_answer_that_used_up_its_token_limit_is_not_a_full_context()
    {
        using var doc = JsonDocument.Parse("""{"choices":[{"finish_reason":"length"}],"ollama_response":{"prompt_eval_count":3000,"eval_count":600}}""");
        Assert.False(DescriptionWorker.ContextFull(doc, 600));
    }

    [Theory]
    [InlineData(8166, 600, 16384)]      // the case above
    [InlineData(100, 600, 4096)]
    [InlineData(30000, 600, 32768)]
    public void The_larger_context_is_the_smallest_size_that_fits(int prompt, int maxTokens, int expected) =>
        Assert.Equal(expected, DescriptionWorker.LargerContext(prompt, maxTokens));

    [Fact]
    public void No_context_is_suggested_beyond_32768()
        => Assert.Null(DescriptionWorker.LargerContext(32000, 600));

    [Fact]
    public void The_context_full_message_uses_the_wording_that_makes_a_batch_split_in_half()
    {
        // DescribeBatches looks for "context size" in the error to split a batch. Rewording must not break that.
        Assert.Contains("context size", DescriptionWorker.ContextFullMessage(8166), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("8166", DescriptionWorker.ContextFullMessage(8166));
    }
}
