using System.Globalization;
using System.Text.RegularExpressions;
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

Dictionary<string, string> colours = new()
{
    ["invoice"] = "0082c9",
    ["receipt"] = "31cc7c",
    ["contract"] = "f1db50",
    ["Needs review"] = "ff7a66"
};

async Task RunPipeline()
{
    NextcloudClient nextcloud = await NextcloudClient.Connect();

    int boardId = await nextcloud.GetOrCreateBoard("Documents");

    foreach (string stack in new[] { "invoice", "receipt", "contract", "Needs review", "Completed" })
    {
        await nextcloud.GetOrCreateStack(boardId, stack);
    }

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

        try
        {
            foreach (string name in new[] { original.Name, pdf.Name, sidecar }.Distinct())
            {
                await nextcloud.Move($"{incoming}/{name}", $"{destination}/{name}");
            }
        }
        catch (HttpRequestException error) when (error.StatusCode == System.Net.HttpStatusCode.Locked)
        {
            Console.WriteLine($"{original.Name} -> skipped, file locked, retry next run");
            continue;
        }

        if (result != DocType.NeedsReview)
        {
            await nextcloud.TagFile(original.FileId, await nextcloud.GetOrCreateTag(result.Name));
        }

        DateTime? dueDate = FindDate(text);
        string summary = string.Join(", ", scores.Select(score => $"{score.DocType.Name}={score.Value}"));
        string stackTitle = result == DocType.NeedsReview ? "Needs review" : result.Name;
        string dateLine = dueDate == null ? "no date found" : $"date: {dueDate:d MMM yyyy}";
        string description = $"[{original.Name}]({nextcloud.FileLink(original.FileId)})\n\n{summary}\n\n{dateLine}";

        await nextcloud.AddCard(boardId, stackTitle, original.Name, description, colours[stackTitle], dueDate);

        Console.WriteLine($"{original.Name} -> {result.Name} ({summary}, {dateLine})");
    }
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

static DateTime? FindDate(string text)
{
    string[] labels =
    [
        "due date", "date due", "payment due", "due by",
        "start date", "commencement date", "with effect from", "wef",
        "expiry date", "renewal date"
    ];

    string[] formats =
    [
        "d/M/yyyy", "d/M/yy", "d-M-yyyy", "d-M-yy", "d.M.yyyy",
        "yyyy-M-d", "yyyy/M/d",
        "d MMM yyyy", "d MMMM yyyy", "d MMM yy",
        "MMM d yyyy", "MMMM d yyyy"
    ];

    const string pattern = @"\d{1,4}[/\-.]\d{1,2}[/\-.]\d{2,4}|\d{1,2}(?:st|nd|rd|th)?\s+[A-Za-z]{3,9}\.?\s+\d{2,4}|[A-Za-z]{3,9}\.?\s+\d{1,2}(?:st|nd|rd|th)?,?\s+\d{4}";

    foreach (string label in labels)
    {
        int index = text.IndexOf(label, StringComparison.OrdinalIgnoreCase);

        if (index < 0)
        {
            continue;
        }

        int start = index + label.Length;
        string window = text.Substring(start, Math.Min(60, text.Length - start));

        foreach (Match match in Regex.Matches(window, pattern))
        {
            string candidate = Regex.Replace(match.Value, @"(?<=\d)(st|nd|rd|th)", "", RegexOptions.IgnoreCase)
                .Replace(",", "")
                .Replace(".  ", " ")
                .Trim();

            if (DateTime.TryParseExact(candidate, formats, new CultureInfo("en-GB"), DateTimeStyles.None, out DateTime date))
            {
                return date;
            }
        }
    }

    return null;
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

string port = Environment.GetEnvironmentVariable("APP_PORT") ?? "9000";

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

WebApplication app = builder.Build();

app.MapGet("/heartbeat", () => Results.Json(new { status = "ok" }));
app.MapPut("/enabled", () => Results.Json(new { error = "" }));

_ = Task.Run(async () =>
{
    while (true)
    {
        try
        {
            await RunPipeline();
        }
        catch (Exception error)
        {
            Console.WriteLine($"pipeline error: {error.Message}");
        }

        await Task.Delay(TimeSpan.FromMinutes(5));
    }
});

app.Run();

public record Marker(string Pattern, int Weight);

public record Score(DocType DocType, int Value);

public record DocType(string Name, List<Marker> Markers)
{
    public static DocType NeedsReview { get; } = new("needs-review", []);
}
