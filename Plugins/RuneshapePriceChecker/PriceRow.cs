namespace RuneshapePriceChecker
{
    using System.Numerics;

    public sealed record PriceRow(string RawText, string ItemName, int Quantity, string Label, decimal ChaosValue, bool Matched, int SourceLineIndex, Vector2 HintPosition);
}
