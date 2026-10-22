using Lumi.Speech;

namespace Lumi.Speech.Tests;

/// <summary>
/// 解析器的 fixture 是依舊版整合說明（outter_sys_spec/whisper.md）的描述構造的，**不是真實錄製**。
/// 真實服務的回應到手後要補一份進來比對（M6 的 6F）。
/// </summary>
public class WhisperResponseParserTests
{
    // ---- 純文字 ----

    [Fact]
    public void Plain_text_is_used_trimmed()
    {
        var result = WhisperResponseParser.Parse("text/plain", "  你好世界\n", "zh");

        Assert.NotNull(result);
        Assert.Equal("你好世界", result.Text);
        Assert.Equal("zh", result.Language);   // 純文字沒有 language，採用請求的語言
        Assert.Null(result.Duration);
    }

    [Fact]
    public void Plain_text_that_looks_like_json_is_parsed_as_json()
    {
        var result = WhisperResponseParser.Parse("text/plain", """{"text":"x"}""", null);

        Assert.Equal("x", result!.Text);
    }

    [Fact]
    public void Non_text_media_type_with_a_plain_body_is_unrecognized()
    {
        Assert.Null(WhisperResponseParser.Parse("application/octet-stream", "hello", "zh"));
    }

    // ---- 頂層與包裝層 ----

    [Fact]
    public void Top_level_text_with_language_and_duration()
    {
        var result = WhisperResponseParser.Parse("application/json", """{"text":"你好","language":"zh","duration":2.25}""", "en");

        Assert.Equal("你好", result!.Text);
        Assert.Equal("zh", result.Language);   // 回應的語言優先於請求的語言
        Assert.Equal(TimeSpan.FromSeconds(2.25), result.Duration);
    }

    [Theory]
    [InlineData("""{"result":{"text":"a"}}""", "a")]
    [InlineData("""{"data":{"transcription":{"text":"b"}}}""", "b")]
    [InlineData("""{"result":{"data":{"output":{"text":"c"}}}}""", "c")]
    [InlineData("""{"result":{"transcript":{"text":"d"}}}""", "d")]
    public void Wrapper_layers_are_searched_up_to_three_levels(string json, string expected)
    {
        var result = WhisperResponseParser.Parse("application/json", json, "zh");

        Assert.Equal(expected, result!.Text);
    }

    [Fact]
    public void A_fourth_wrapper_layer_is_not_searched()
    {
        var json = """{"result":{"data":{"output":{"transcript":{"text":"d"}}}}}""";

        Assert.Null(WhisperResponseParser.Parse("application/json", json, "zh"));
    }

    [Fact]
    public void Text_is_preferred_over_segments_at_the_same_level()
    {
        var json = """{"text":"整段","segments":[{"text":"分段"}]}""";

        Assert.Equal("整段", WhisperResponseParser.Parse("application/json", json, "zh")!.Text);
    }

    [Fact]
    public void Non_string_text_is_not_used()
    {
        Assert.Null(WhisperResponseParser.Parse("application/json", """{"text":5}""", "zh"));
    }

    // ---- segments ----

    [Fact]
    public void Chinese_segments_are_joined_without_spaces()
    {
        var json = """{"segments":[{"text":" 你好"},{"text":"世界 "}],"language":"zh"}""";

        Assert.Equal("你好世界", WhisperResponseParser.Parse("application/json", json, null)!.Text);
    }

    [Fact]
    public void Japanese_segments_are_joined_without_spaces()
    {
        var json = """{"segments":[{"text":"こんにちは"},{"text":"世界"}],"language":"ja"}""";

        Assert.Equal("こんにちは世界", WhisperResponseParser.Parse("application/json", json, null)!.Text);
    }

    [Fact]
    public void English_segments_are_joined_with_a_single_space()
    {
        var json = """{"segments":[{"text":"Hello"},{"text":" world "}],"language":"en"}""";

        Assert.Equal("Hello world", WhisperResponseParser.Parse("application/json", json, null)!.Text);
    }

    [Fact]
    public void Segment_join_uses_the_request_language_when_the_response_has_none()
    {
        var json = """{"segments":[{"text":"你好"},{"text":"世界"}]}""";

        Assert.Equal("你好世界", WhisperResponseParser.Parse("application/json", json, "zh")!.Text);
        Assert.Equal("你好 世界", WhisperResponseParser.Parse("application/json", json, "en")!.Text);
    }

    [Fact]
    public void Segments_without_any_text_are_a_found_empty_result()
    {
        var result = WhisperResponseParser.Parse("application/json", """{"segments":[]}""", "zh");

        Assert.Equal("", result!.Text);
    }

    // ---- 空白與非預期形狀 ----

    [Theory]
    [InlineData("")]
    [InlineData("   \n")]
    public void Blank_body_is_a_found_empty_result(string body)
    {
        var result = WhisperResponseParser.Parse("application/json", body, "zh");

        Assert.Equal("", result!.Text);
        Assert.Equal("zh", result.Language);
    }

    [Fact]
    public void Empty_text_field_is_a_found_empty_result()
    {
        Assert.Equal("", WhisperResponseParser.Parse("application/json", """{"text":""}""", "zh")!.Text);
    }

    [Fact]
    public void Non_json_body_under_a_json_content_type_is_unrecognized()
    {
        Assert.Null(WhisperResponseParser.Parse("application/json", "not json at all", "zh"));
    }

    [Theory]
    [InlineData("""{"foo":{"bar":1}}""")]
    [InlineData("[1,2,3]")]
    [InlineData("\"quoted\"")]
    [InlineData("{\"text\":")]
    public void Unexpected_shapes_are_unrecognized(string body)
    {
        Assert.Null(WhisperResponseParser.Parse("application/json", body, "zh"));
    }

    [Fact]
    public void Missing_content_type_with_a_plain_body_is_unrecognized()
    {
        Assert.Null(WhisperResponseParser.Parse(null, "hello", "zh"));
    }

    // ---- language／duration ----

    [Fact]
    public void Request_language_is_used_when_the_response_has_none()
    {
        Assert.Equal("en", WhisperResponseParser.Parse("application/json", """{"text":"x"}""", "en")!.Language);
        Assert.Null(WhisperResponseParser.Parse("application/json", """{"text":"x"}""", "  ")!.Language);
    }

    [Theory]
    [InlineData("""{"text":"x","duration":"3"}""")]
    [InlineData("""{"text":"x","duration":-1}""")]
    [InlineData("""{"text":"x"}""")]
    public void Duration_is_read_only_when_it_is_a_non_negative_number(string json)
    {
        Assert.Null(WhisperResponseParser.Parse("application/json", json, "zh")!.Duration);
    }
}
