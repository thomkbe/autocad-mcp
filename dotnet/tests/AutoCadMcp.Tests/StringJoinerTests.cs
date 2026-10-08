using AutoCadMcp.Plugin;

namespace AutoCadMcp.Tests;

public sealed class StringJoinerTests
{
    private static List<(short Code, object? Value, int Parts)> Join(params (short Code, object? Value)[] values) =>
        StringJoiner.Join(values).ToList();

    [Fact]
    public void Joins_consecutive_chunks_with_the_same_code_in_order()
    {
        // The shape of RAILCOMPLETE_XMLDATA: one long text split into code 1 chunks.
        var joined = Join((1, "<tSignal>"), (1, "<name/>"), (1, "</tSignal>"));

        Assert.Equal([((short)1, (object?)"<tSignal><name/></tSignal>", 3)], joined);
    }

    [Fact]
    public void A_different_code_or_a_non_string_ends_the_run()
    {
        var joined = Join((1, "a"), (1, "b"), (3, "c"), (70, (short)5), (1, "d"));

        Assert.Equal(
            [((short)1, (object?)"ab", 2), ((short)3, "c", 1), ((short)70, (short)5, 1), ((short)1, "d", 1)],
            joined);
    }

    [Fact]
    public void Never_joins_control_strings_names_or_handles()
    {
        var joined = Join((1002, "{"), (1002, "{"), (1005, "1A"), (1005, "1B"), (1001, "APP"), (1001, "APP2"));

        Assert.Equal(6, joined.Count);
        Assert.All(joined, item => Assert.Equal(1, item.Parts));
    }

    [Fact]
    public void Passes_through_empty_input()
    {
        Assert.Empty(Join());
    }
}
