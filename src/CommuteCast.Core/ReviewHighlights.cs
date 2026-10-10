namespace CommuteCast.Core;

public enum ReviewHighlightKind { Unchanged, Changed, Excluded }
public sealed record ReviewRun(string Text, ReviewHighlightKind Kind);

/// <summary>Nonoverlapping exact-text runs for review, independent of WPF document offsets.</summary>
public static class ReviewHighlights
{
    public static IReadOnlyList<ReviewRun> Original(PreparedText prepared, string source)
    {
        var highlights = prepared.Spans.Where(s => s.Kind == "explicit exclusion")
            .Select(s => (Start: s.Start, End: s.Start + s.Length, Kind: ReviewHighlightKind.Excluded))
            .Concat((prepared.ProfileReview?.Changes ?? []).Select(c => (Start: c.SourceStart, End: c.SourceStart + c.SourceLength, Kind: ReviewHighlightKind.Changed)))
            .Where(r => r.Start >= 0 && r.End > r.Start && r.End <= source.Length).ToArray();
        var events = highlights.SelectMany(r => new[] { (Position: r.Start, Kind: r.Kind, Delta: 1), (Position: r.End, Kind: r.Kind, Delta: -1) })
            .OrderBy(e => e.Position).GroupBy(e => e.Position);
        var runs = new List<ReviewRun>();
        var offset = 0; var changed = 0; var excluded = 0;
        foreach (var point in events)
        {
            var kind = excluded > 0 ? ReviewHighlightKind.Excluded : changed > 0 ? ReviewHighlightKind.Changed : ReviewHighlightKind.Unchanged;
            if (point.Key > offset) runs.Add(new(source[offset..point.Key], kind));
            foreach (var edge in point) { if (edge.Kind == ReviewHighlightKind.Excluded) excluded += edge.Delta; else changed += edge.Delta; }
            offset = point.Key;
        }
        if (offset < source.Length) runs.Add(new(source[offset..], ReviewHighlightKind.Unchanged));
        return runs;
    }
    public static IReadOnlyList<ReviewRun> Spoken(PreparedText prepared)
    {
        if (string.Concat(prepared.Spans.Select(s => s.Narration)) != prepared.Script) return [new(prepared.Script, ReviewHighlightKind.Changed)];
        return prepared.Spans.Select(s => new ReviewRun(s.Narration,
            s.Kind == "explicit exclusion" ? ReviewHighlightKind.Excluded : s.Original != s.Narration ? ReviewHighlightKind.Changed : ReviewHighlightKind.Unchanged)).ToArray();
    }
}
