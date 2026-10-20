namespace SnapGlyph.Core;

public enum OcrMode
{
    Auto = 0,
    Number = 1,
    Text = 2,
}

public sealed class OcrDisplay
{
    public required OcrMode EffectiveMode { get; init; }
    public required string Text { get; init; }
    public OcrRow? SelectedRow { get; init; }
    public required IReadOnlyList<OcrRow> Rows { get; init; }
}

public static class OcrPresenter
{
    public static OcrDisplay Present(SnapGlyphResult result, OcrMode mode)
    {
        var ordered = result.KeptRows
            .OrderBy(row => row.Box.Top)
            .ThenBy(row => row.Box.Left)
            .ToArray();
        var bestNumber = result.Candidates.FirstOrDefault(IsObviousNumber);
        var effective = mode switch
        {
            OcrMode.Number => OcrMode.Number,
            OcrMode.Text => OcrMode.Text,
            _ => bestNumber is null ? OcrMode.Text : OcrMode.Number,
        };

        if (effective == OcrMode.Number)
        {
            return new OcrDisplay
            {
                EffectiveMode = OcrMode.Number,
                Text = bestNumber?.Digits ?? string.Empty,
                SelectedRow = bestNumber,
                Rows = result.Candidates,
            };
        }

        return new OcrDisplay
        {
            EffectiveMode = OcrMode.Text,
            Text = string.Join(Environment.NewLine, ordered.Select(row => row.Text)),
            SelectedRow = null,
            Rows = ordered,
        };
    }

    public static bool IsObviousNumber(OcrRow row) =>
        row.IsObviousNumber;
}
