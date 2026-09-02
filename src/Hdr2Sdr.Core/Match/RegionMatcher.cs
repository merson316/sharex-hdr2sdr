using Hdr2Sdr.Core.Imaging;

namespace Hdr2Sdr.Core.Match;

/// <summary>Where a template was found. Coverage is the fraction of the template that agreed on that position (1 = whole template).</summary>
public readonly record struct MatchResult(int X, int Y, float Score, float Coverage = 1f);

/// <summary>
/// Locates a template inside a larger image by normalized cross-correlation (NCC), which is invariant
/// to brightness/contrast changes. Coarse-to-fine: exhaustive at 1/16 (or 1/4, or 1/1 for small
/// templates), then refined around the best candidates at each finer level.
/// Flat templates (zero variance) fall back to 1 - mean absolute difference.
/// </summary>
public static class RegionMatcher
{
    public const float AcceptThreshold = 0.9f;
    private const float FlatVariance = 1e-6f;
    private const int MinTile = 48;           // smallest tile edge for tile voting, px
    private const int TilesPerAxis = 4;
    private const float TileAccept = 0.8f;    // a tile below this is treated as changed content
    private const int VoteRadius = 2;         // px; votes closer than this agree
    private const int MinVotes = 3;
    private const int CoarsePerPhase = 8;     // candidates kept per grid phase at the coarse level
    private const int CoarseCandidates = 40;  // candidates carried from the coarse level (after proximity dedupe)
    private const int MidCandidates = 3;      // candidates carried between the finer levels

    /// <summary>
    /// Like <see cref="Find"/>, but when the whole template does not match (part of the region changed between
    /// captures: video, animation, a progress bar) it splits the template into tiles, locates each tile on its
    /// own and takes the position most tiles agree on. Score is then the mean score of the agreeing tiles and
    /// Coverage their share of all tiles.
    /// </summary>
    public static MatchResult FindRobust(GrayImage template, GrayImage candidate)
    {
        MatchResult whole = Find(template, candidate);
        if (whole.Score >= AcceptThreshold || whole.Score < 0f) return whole;
        if (template.Width < 2 * MinTile || template.Height < 2 * MinTile) return whole;
        MatchResult voted = TileVote(template, candidate);
        return voted.Score > whole.Score ? voted : whole;
    }

    private static MatchResult TileVote(GrayImage template, GrayImage candidate)
    {
        int tileW = Math.Max(MinTile, template.Width / TilesPerAxis);
        int tileH = Math.Max(MinTile, template.Height / TilesPerAxis);
        int cols = template.Width / tileW, rows = template.Height / tileH;
        int total = cols * rows;
        var votes = new List<(int X, int Y, float Score)>(total);
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
            {
                int tx = c * tileW, ty = r * tileH;
                GrayImage tile = Sub(template, tx, ty, tileW, tileH);
                if (Stats(tile).Variance < 1e-4f) continue;      // flat tile: cannot locate anything
                MatchResult m = Find(tile, candidate);
                if (m.Score < TileAccept) continue;               // changed content
                int ox = m.X - tx, oy = m.Y - ty;
                if (ox < 0 || oy < 0 || ox + template.Width > candidate.Width || oy + template.Height > candidate.Height) continue;
                votes.Add((ox, oy, m.Score));
            }
        if (votes.Count < MinVotes) return new MatchResult(0, 0, 0f, 0f);

        // Cluster: pick the vote with the most neighbours within VoteRadius (ties: higher total score).
        int bestCount = -1;
        float bestWeight = 0f;
        List<(int X, int Y, float Score)> bestMembers = new();
        foreach (var v in votes)
        {
            var members = votes.Where(o => Math.Abs(o.X - v.X) <= VoteRadius && Math.Abs(o.Y - v.Y) <= VoteRadius).ToList();
            float weight = members.Sum(o => o.Score);
            if (members.Count > bestCount || (members.Count == bestCount && weight > bestWeight))
            {
                bestCount = members.Count;
                bestWeight = weight;
                bestMembers = members;
            }
        }
        if (bestCount < MinVotes) return new MatchResult(0, 0, 0f, 0f);
        int x = (int)MathF.Round(bestMembers.Sum(o => o.X * o.Score) / bestWeight);
        int y = (int)MathF.Round(bestMembers.Sum(o => o.Y * o.Score) / bestWeight);
        return new MatchResult(x, y, bestWeight / bestCount, (float)bestCount / total);
    }

    private static GrayImage Sub(GrayImage src, int x, int y, int w, int h)
    {
        var t = new GrayImage(w, h);
        for (int j = 0; j < h; j++) Array.Copy(src.Data, (y + j) * src.Width + x, t.Data, j * w, w);
        return t;
    }

    public static MatchResult Find(GrayImage template, GrayImage candidate)
    {
        if (template.Width > candidate.Width || template.Height > candidate.Height)
            return new MatchResult(0, 0, -1f);
        if (template.Width == candidate.Width && template.Height == candidate.Height)
            return new MatchResult(0, 0, Score(template, Stats(template), candidate, 0, 0));

        int minDim = Math.Min(template.Width, template.Height);
        int[] levels = minDim >= 64 ? new[] { 16, 4, 1 } : minDim >= 16 ? new[] { 4, 1 } : new[] { 1 };
        int coarse = levels[0];
        if (coarse == 1) return Exhaustive(template, candidate, 1)[0];

        // Coarse level. A box-downsampled template only correlates with the box-downsampled candidate when
        // the template's origin lies on the candidate's box grid, so search four grid phases per axis and
        // keep the candidate positions in full-resolution coordinates.
        GrayImage t = template.Downsample(coarse);
        int[] phases = { 0, coarse / 4, coarse / 2, coarse * 3 / 4 };
        var cands = new List<MatchResult>();
        foreach (int py in phases)
            foreach (int px in phases)
            {
                if (candidate.Width - px < t.Width * coarse || candidate.Height - py < t.Height * coarse) continue;
                GrayImage c = candidate.Downsample(coarse, px, py);
                if (c.Width < t.Width || c.Height < t.Height) continue;
                foreach (MatchResult m in Exhaustive(t, c, CoarsePerPhase))
                    cands.Add(new MatchResult(m.X * coarse + px, m.Y * coarse + py, m.Score));
            }
        cands = DedupeByProximity(cands.OrderByDescending(r => r.Score), coarse, CoarseCandidates);

        for (int li = 1; li < levels.Length; li++)
        {
            int g = levels[li];
            // A candidate from the previous level is at most half a previous-level cell off; +1 for rounding.
            int radius = levels[li - 1] / g / 2 + 1;
            t = template.Downsample(g);
            GrayImage c = candidate.Downsample(g);
            TemplateStats stats = Stats(t);
            var refined = new List<MatchResult>(cands.Count);
            foreach (MatchResult m in cands)
            {
                MatchResult local = Local(t, stats, c, m.X / g, m.Y / g, radius);
                refined.Add(new MatchResult(local.X * g, local.Y * g, local.Score));
            }
            cands = refined.OrderByDescending(r => r.Score).Take(MidCandidates).ToList();
        }
        return cands.OrderByDescending(r => r.Score).First();
    }

    /// <summary>Keeps the best-scoring candidates, dropping any within minDistance of an already kept one.</summary>
    private static List<MatchResult> DedupeByProximity(IEnumerable<MatchResult> ordered, int minDistance, int keep)
    {
        var kept = new List<MatchResult>(keep);
        foreach (MatchResult m in ordered)
        {
            bool near = false;
            foreach (MatchResult k in kept)
                if (Math.Abs(k.X - m.X) < minDistance && Math.Abs(k.Y - m.Y) < minDistance) { near = true; break; }
            if (near) continue;
            kept.Add(m);
            if (kept.Count >= keep) break;
        }
        return kept;
    }

    private readonly record struct TemplateStats(float Mean, float Variance);

    private static TemplateStats Stats(GrayImage t)
    {
        double s = 0, ss = 0;
        foreach (float v in t.Data) { s += v; ss += v * v; }
        int n = t.Data.Length;
        double mean = s / n;
        return new TemplateStats((float)mean, (float)(ss / n - mean * mean));
    }

    private static List<MatchResult> Exhaustive(GrayImage t, GrayImage c, int keep)
    {
        TemplateStats stats = Stats(t);
        int cols = c.Width - t.Width + 1, rows = c.Height - t.Height + 1;
        var scores = new float[cols * rows];
        Parallel.For(0, rows, y =>
        {
            for (int x = 0; x < cols; x++) scores[y * cols + x] = Score(t, stats, c, x, y);
        });
        var best = new List<MatchResult>(keep);
        var used = new bool[scores.Length];
        for (int k = 0; k < keep; k++)
        {
            int bi = -1;
            float bs = float.NegativeInfinity;
            for (int i = 0; i < scores.Length; i++)
                if (!used[i] && scores[i] > bs) { bs = scores[i]; bi = i; }
            if (bi < 0) break;
            used[bi] = true;
            best.Add(new MatchResult(bi % cols, bi / cols, bs));
        }
        return best;
    }

    private static MatchResult Local(GrayImage t, TemplateStats stats, GrayImage c, int cx, int cy, int radius)
    {
        int x0 = Math.Max(0, cx - radius), x1 = Math.Min(c.Width - t.Width, cx + radius);
        int y0 = Math.Max(0, cy - radius), y1 = Math.Min(c.Height - t.Height, cy + radius);
        if (x1 < x0 || y1 < y0) return new MatchResult(Math.Clamp(cx, 0, c.Width - t.Width), Math.Clamp(cy, 0, c.Height - t.Height), -1f);
        int cols = x1 - x0 + 1, rows = y1 - y0 + 1;
        var scores = new float[cols * rows];
        Parallel.For(0, rows, j =>
        {
            for (int i = 0; i < cols; i++) scores[j * cols + i] = Score(t, stats, c, x0 + i, y0 + j);
        });
        int bi = 0;
        for (int i = 1; i < scores.Length; i++) if (scores[i] > scores[bi]) bi = i;
        return new MatchResult(x0 + bi % cols, y0 + bi / cols, scores[bi]);
    }

    private static float Score(GrayImage t, TemplateStats stats, GrayImage c, int x, int y)
    {
        int tw = t.Width, th = t.Height, cw = c.Width, n = tw * th;
        float[] td = t.Data, cd = c.Data;
        if (stats.Variance < FlatVariance)
        {
            double mad = 0;
            for (int j = 0; j < th; j++)
            {
                int ti = j * tw, ci = (y + j) * cw + x;
                for (int i = 0; i < tw; i++) mad += Math.Abs(td[ti + i] - cd[ci + i]);
            }
            return (float)(1.0 - mad / n);
        }
        double sw = 0, sww = 0, stw = 0;
        float tm = stats.Mean;
        for (int j = 0; j < th; j++)
        {
            int ti = j * tw, ci = (y + j) * cw + x;
            for (int i = 0; i < tw; i++)
            {
                float w = cd[ci + i];
                sw += w; sww += w * w; stw += (td[ti + i] - tm) * w;
            }
        }
        double wm = sw / n, wv = sww / n - wm * wm;
        if (wv < FlatVariance) return 0f;
        return (float)(stw / (n * Math.Sqrt(stats.Variance) * Math.Sqrt(wv)));
    }
}
