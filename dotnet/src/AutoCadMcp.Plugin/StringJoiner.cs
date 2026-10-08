using System.Text;

namespace AutoCadMcp.Plugin;

/// <summary>
/// Concatenates runs of text values with the same group code. Applications store long text
/// (e.g. RailCOMPLETE's XML) as consecutive chunks because AutoCAD limits a single value's length.
/// </summary>
internal static class StringJoiner
{
    // Structural strings that must stay separate: control strings, XData app/layer names, handles.
    private static readonly HashSet<short> NeverJoined = [102, 1001, 1002, 1003, 1005];

    public static IEnumerable<(short Code, object? Value, int Parts)> Join(IEnumerable<(short Code, object? Value)> values)
    {
        StringBuilder? run = null;
        short runCode = 0;
        var parts = 0;

        foreach (var (code, value) in values)
        {
            if (run is not null && code == runCode && value is string more)
            {
                run.Append(more);
                parts++;
                continue;
            }

            if (run is not null)
            {
                yield return (runCode, run.ToString(), parts);
                run = null;
            }

            if (value is string text && !NeverJoined.Contains(code))
            {
                run = new StringBuilder(text);
                runCode = code;
                parts = 1;
            }
            else
            {
                yield return (code, value, 1);
            }
        }

        if (run is not null)
            yield return (runCode, run.ToString(), parts);
    }
}
