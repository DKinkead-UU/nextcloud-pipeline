using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UglyToad.PdfPig;

List<Marker> invoiceMarkers =
[
    new("bill to", 2),
    new("invoice", 3),
    new("subtotal", 2),
    new("due date", 1),
    new("tax", 1)
];

List<Marker> receiptMarkers =
[
    new("change due", 3),
    new("total amount", 1),
    new("payment method", 2),
    new("cashier", 1),
    new("cash", 2)
];

List<Marker> contractMarkers =
[
    new("whereas", 3),
    new("parties", 1),
    new("in witness whereof", 3),
    new("party a", 2),
    new("party b", 2),
    new("scope of work", 2),
    new("compensation", 1),
    new("confidentiality", 1),
    new("termination", 1),
    new("contract", 1)
];

List<DocType> docTypes =
[
    new("invoice", invoiceMarkers),
    new("receipt", receiptMarkers),
    new("contract", contractMarkers)
];

foreach (string pdfPath in Directory.GetFiles("samples", "*.pdf"))
{
    string originalPath = FindOriginalFile(pdfPath);
    string text = ReadPdfText(pdfPath);

    List<Score> scores = KeywordScan(text, docTypes);
    DocType result = ClassifyDocument(scores);

    Console.WriteLine($"{Path.GetFileName(originalPath)} -> {result.Name}");

    foreach (Score score in scores)
    {
        Console.WriteLine($"    {score.DocType.Name}: {score.Value}");
    }
}

static List<Score> KeywordScan(string text, List<DocType> docTypes)
{
    List<Score> scores = [];

    foreach (DocType docType in docTypes)
    {
        int total = docType.Markers.Sum(marker => CountOccurrences(text, marker.Pattern) * marker.Weight);
        scores.Add(new Score(docType, total));
    }

    return scores;
}

static DocType ClassifyDocument(List<Score> scores)
{
    const int minimumScore = 3;
    const int minimumGap = 1;

    List<Score> ranked = scores.OrderByDescending(score => score.Value).ToList();
    Score best = ranked[0];
    Score? runnerUp = ranked.Count > 1 ? ranked[1] : null;

    bool tooLow = best.Value < minimumScore;
    bool tooClose = runnerUp != null && best.Value - runnerUp.Value <= minimumGap;

    return tooLow || tooClose ? DocType.NeedsReview : best.DocType;
}

static string FindOriginalFile(string pdfPath)
{
    string folder = Path.GetDirectoryName(pdfPath) ?? ".";
    string stem = Path.GetFileNameWithoutExtension(pdfPath);
    string exactMatch = Path.Combine(folder, stem);

    if (File.Exists(exactMatch))
    {
        return exactMatch;
    }

    return Directory.GetFiles(folder)
            .FirstOrDefault(candidate => candidate != pdfPath && Path.GetFileNameWithoutExtension(candidate) == stem && Path.GetExtension(candidate) != ".txt" && Path.GetFileNameWithoutExtension(candidate) == stem) ?? pdfPath;
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

static string ReadPdfText(string pdfPath)
{
    using PdfDocument document = PdfDocument.Open(pdfPath);

    return string.Join(" ", document.GetPages()
        .SelectMany(page => page.GetWords())
        .Select(word => word.Text));
}

public record Marker(string Pattern, int Weight);

public record Score(DocType DocType, int Value);

public record DocType(string Name, List<Marker> Markers)
{
    public static DocType NeedsReview { get; } = new("needs-review", []);
}