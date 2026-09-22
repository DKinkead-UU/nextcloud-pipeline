using UglyToad.PdfPig;

const string incoming = "Processing/Incoming";

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
    new("cash", 2),
    new("total amount", 1)
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

NextcloudClient nextcloud = await NextcloudClient.Connect();
List<RemoteFile> files = await nextcloud.ListFolder(incoming);
HashSet<string> names = files.Select(file => file.Name).ToHashSet();

foreach (RemoteFile pdf in files.Where(file => file.Name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)))
{
    string sidecar = Path.GetFileNameWithoutExtension(pdf.Name) + ".txt";

    if (!names.Contains(sidecar))
    {
        Console.WriteLine($"{pdf.Name} -> not ready");
        continue;
    }

    RemoteFile original = FindOriginal(pdf, files);
    string text = ReadPdfText(await nextcloud.Download($"{incoming}/{pdf.Name}"));
    List<Score> scores = KeywordScan(text, docTypes);
    DocType result = ClassifyDocument(scores);

    string destination = result == DocType.NeedsReview
        ? "Processing/Needs review"
        : $"Processing/Documents/{result.Name}";

    await nextcloud.MakeFolder(destination);

    foreach (string name in new[] { original.Name, pdf.Name, sidecar }.Distinct())
    {
        await nextcloud.Move($"{incoming}/{name}", $"{destination}/{name}");
    }

    if (result != DocType.NeedsReview)
    {
        await nextcloud.TagFile(original.FileId, await nextcloud.GetOrCreateTag(result.Name));
    }

    string summary = string.Join(", ", scores.Select(score => $"{score.DocType.Name}={score.Value}"));
    Console.WriteLine($"{original.Name} -> {result.Name} ({summary})");
}

static RemoteFile FindOriginal(RemoteFile pdf, List<RemoteFile> files)
{
    string stem = Path.GetFileNameWithoutExtension(pdf.Name);

    return files.FirstOrDefault(file => file.Name == stem)
        ?? files.FirstOrDefault(file => file != pdf
            && Path.GetExtension(file.Name) != ".txt"
            && Path.GetFileNameWithoutExtension(file.Name) == stem)
        ?? pdf;
}

static string ReadPdfText(byte[] pdf)
{
    using PdfDocument document = PdfDocument.Open(pdf);

    return string.Join(" ", document.GetPages()
        .SelectMany(page => page.GetWords())
        .Select(word => word.Text));
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

public record Marker(string Pattern, int Weight);

public record Score(DocType DocType, int Value);

public record DocType(string Name, List<Marker> Markers)
{
    public static DocType NeedsReview { get; } = new("needs-review", []);
}
