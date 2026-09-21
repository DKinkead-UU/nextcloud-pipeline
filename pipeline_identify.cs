using System;
using System.Collections.Generic;

List<Marker> invoiceMarkers = new List<Marker>
{
    new Marker("bill to", 2),
    new Marker("invoice", 3),
    new Marker("subtotal", 2),
    new Marker("due date", 1),
    new Marker("tax", 1)
};

List<Marker> receiptMarkers = new List<Marker>
{
    new Marker("receipt", 2),
    new Marker("total amount", 2),
    new Marker("cash", 1),
    new Marker("change", 1),
    new Marker("thank you", 1)
};

List<Marker> contractMarkers = new List<Marker>
{
    new Marker("whereas", 3),
    new Marker("parties", 1),
    new Marker("in witness whereof", 3),
    new Marker("party a", 2),
    new Marker("party b", 2),
    new Marker("scope of work", 2),
    new Marker("compensation", 1),
    new Marker("confidentiality", 1),
    new Marker("termination", 1),
    new Marker("contract", 1)
};

List<DocType> docTypes = new List<DocType>
{
    new DocType("invoice", invoiceMarkers),
    new DocType("receipt", receiptMarkers),
    new DocType("contract", contractMarkers)
};

static List<Score> KeywordScan(string text, List<DocType> docTypes)
{
    List<Score> scores = new List<Score>();

    foreach (DocType docType in docTypes)
    {
        int totalScore = 0;

        foreach (Marker marker in docType.Markers)
        {
            totalScore += CountOccurrences(text, marker.Pattern) * marker.Weight;
        }

        scores.Add(new Score(docType, totalScore));
    }

    return scores;
}

static DocType ClassifyDocument(string text, List<DocType> docTypes)
{
    const int minimumScore = 3;
    const int minimumGap = 1;

    if (docTypes.Count == 0)
    {
        throw new InvalidOperationException("At least one document type is required.");
    }

    List<Score> scores = KeywordScan(text, docTypes);
    Score bestScore = scores[0];

    foreach (Score score in scores)
    {
        if (score.Value > bestScore.Value)
        {
            bestScore = score;
        }
    }
    Score? runnerUp = null;

    foreach (Score score in scores)
    {
        if (ReferenceEquals(score, bestScore))
        {
            continue;
        }

        if (runnerUp == null || score.Value > runnerUp.Value)
        {
            runnerUp = score;
        }
    }

    if (bestScore.Value < minimumScore)
    {
        return DocType.NeedsReview;
    }
    if (runnerUp != null && bestScore.Value - runnerUp.Value <= minimumGap)
    {
        return DocType.NeedsReview;
    }
    return bestScore.DocType;
}

static string FindOriginalFile(string sidecarPath)
{
    string folder = Path.GetDirectoryName(sidecarPath);
    string stem = Path.GetFileNameWithoutExtension(sidecarPath);

    string exactMatch = Path.Combine(folder, stem);

    if (File.Exists(exactMatch))
    {
        return exactMatch;
    }

    foreach (string candidate in Directory.GetFiles(folder))
    {
        if (candidate == sidecarPath)
        {
            continue;
        }

        if (Path.GetFileNameWithoutExtension(candidate) == stem)
        {
            return candidate;
        }
    }

    return null;
}

static int CountOccurrences(string text, string pattern)
{
    if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(pattern))
    {
        return 0;
    }
    int count = 0;
    int startIndex = 0;
    while ((startIndex = text.IndexOf(pattern, startIndex, StringComparison.OrdinalIgnoreCase)) >= 0)
    {
        count++;
        startIndex += pattern.Length;
    }
    return count;
}

public class Marker(string pattern, int weight)
{
    public string Pattern { get; } = pattern;
    public int Weight { get; } = weight;
}

public class DocType(string name, List<Marker> markers)
{
    public static DocType NeedsReview { get; } = new DocType("needs-review", new List<Marker>());

    public string Name { get; } = name;
    public List<Marker> Markers { get; } = markers;
}

public class Score(DocType docType, int value)
{
    public DocType DocType { get; } = docType;
    public int Value { get; } = value;
}