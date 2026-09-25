using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

public record RemoteFile(string Name, string FileId);

public class NextcloudClient
{
    static readonly XNamespace Dav = "DAV:";
    static readonly XNamespace Oc = "http://owncloud.org/ns";

    readonly HttpClient http = new();
    readonly string baseUrl;
    string filesRoot = "";

    NextcloudClient(string url, string login, string password)
    {
        baseUrl = url.TrimEnd('/');

        string token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{login}:{password}"));
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
    }

    public static async Task<NextcloudClient> Connect()
    {
        string url = Require("NC_URL");

        if (!url.StartsWith("https://"))
        {
            throw new InvalidOperationException("NC_URL must start with https://");
        }

        NextcloudClient client = new(url, Require("NC_USER"), Require("NC_APP_PASSWORD"));
        string userId = await client.GetUserId();
        client.filesRoot = $"{client.baseUrl}/remote.php/dav/files/{Uri.EscapeDataString(userId)}";

        return client;
    }

    async Task<string> GetUserId()
    {
        HttpRequestMessage request = new(HttpMethod.Get, $"{baseUrl}/ocs/v2.php/cloud/user?format=json");
        request.Headers.Add("OCS-APIRequest", "true");

        HttpResponseMessage response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();

        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("ocs").GetProperty("data").GetProperty("id").GetString()!;
    }

    public async Task<List<RemoteFile>> ListFolder(string path)
    {
        XDocument xml = await Propfind(FileUrl(path), "<d:resourcetype/><oc:fileid/>");

        return xml.Descendants(Dav + "response")
            .Where(item => !item.Descendants(Dav + "collection").Any())
            .Select(item => new RemoteFile(
                Uri.UnescapeDataString(item.Element(Dav + "href")!.Value.TrimEnd('/').Split('/').Last()),
                item.Descendants(Oc + "fileid").First().Value))
            .ToList();
    }

    public Task<byte[]> Download(string path) => http.GetByteArrayAsync(FileUrl(path));

    public async Task MakeFolder(string path)
    {
        HttpResponseMessage response = await http.SendAsync(new HttpRequestMessage(new HttpMethod("MKCOL"), FileUrl(path)));

        if (response.StatusCode != HttpStatusCode.MethodNotAllowed)
        {
            response.EnsureSuccessStatusCode();
        }
    }

    public async Task Move(string from, string to)
    {
        HttpRequestMessage request = new(new HttpMethod("MOVE"), FileUrl(from));
        request.Headers.Add("Destination", FileUrl(to));
        request.Headers.Add("Overwrite", "F");

        HttpResponseMessage response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    public async Task<string> GetOrCreateTag(string name)
    {
        string? id = await FindTag(name);

        if (id != null)
        {
            return id;
        }

        string json = JsonSerializer.Serialize(new { name, userVisible = true, userAssignable = true });
        HttpResponseMessage response = await http.PostAsync($"{baseUrl}/remote.php/dav/systemtags/", new StringContent(json, Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();

        return await FindTag(name) ?? throw new InvalidOperationException($"Tag '{name}' was created but can't be found");
    }

    public async Task TagFile(string fileId, string tagId)
    {
        HttpResponseMessage response = await http.PutAsync($"{baseUrl}/remote.php/dav/systemtags-relations/files/{fileId}/{tagId}", null);

        if (response.StatusCode != HttpStatusCode.Conflict)
        {
            response.EnsureSuccessStatusCode();
        }
    }

    async Task<string?> FindTag(string name)
    {
        XDocument xml = await Propfind($"{baseUrl}/remote.php/dav/systemtags/", "<oc:id/><oc:display-name/>");

        return xml.Descendants(Oc + "display-name")
            .FirstOrDefault(element => element.Value == name)?
            .Parent?.Element(Oc + "id")?.Value;
    }

    async Task<XDocument> Propfind(string url, string properties)
    {
        string body = $"""<?xml version="1.0"?><d:propfind xmlns:d="DAV:" xmlns:oc="http://owncloud.org/ns"><d:prop>{properties}</d:prop></d:propfind>""";

        HttpRequestMessage request = new(new HttpMethod("PROPFIND"), url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/xml")
        };
        request.Headers.Add("Depth", "1");

        HttpResponseMessage response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();

        return XDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    public string FileLink(string fileId) => $"{baseUrl}/index.php/f/{fileId}";

    public async Task<int> GetOrCreateBoard(string title)
    {
        JsonElement boards = await Deck(HttpMethod.Get, "boards");

        foreach (JsonElement board in boards.EnumerateArray())
        {
            if (board.GetProperty("title").GetString() == title)
            {
                return board.GetProperty("id").GetInt32();
            }
        }

        JsonElement created = await Deck(HttpMethod.Post, "boards", new { title, color = "0082c9" });
        return created.GetProperty("id").GetInt32();
    }

    public async Task<int> GetOrCreateStack(int boardId, string title)
    {
        JsonElement stacks = await Deck(HttpMethod.Get, $"boards/{boardId}/stacks");

        foreach (JsonElement stack in stacks.EnumerateArray())
        {
            if (stack.GetProperty("title").GetString() == title)
            {
                return stack.GetProperty("id").GetInt32();
            }
        }

        JsonElement created = await Deck(HttpMethod.Post, $"boards/{boardId}/stacks", new { title, order = 0 });
        return created.GetProperty("id").GetInt32();
    }

    public async Task<int> GetOrCreateLabel(int boardId, string title, string colour)
    {
        JsonElement board = await Deck(HttpMethod.Get, $"boards/{boardId}");

        foreach (JsonElement label in board.GetProperty("labels").EnumerateArray())
        {
            if (label.GetProperty("title").GetString() == title)
            {
                return label.GetProperty("id").GetInt32();
            }
        }

        JsonElement created = await Deck(HttpMethod.Post, $"boards/{boardId}/labels", new { title, color = colour });
        return created.GetProperty("id").GetInt32();
    }

    public async Task AddCard(int boardId, string stackTitle, string title, string description, string colour, DateTime? dueDate = null)
    {
        int stackId = await GetOrCreateStack(boardId, stackTitle);
        int labelId = await GetOrCreateLabel(boardId, stackTitle, colour);

        JsonElement card = await Deck(HttpMethod.Post, $"boards/{boardId}/stacks/{stackId}/cards",
            new
            {
                title,
                type = "plain",
                order = 0,
                description,
                duedate = dueDate?.ToString("yyyy-MM-ddTHH:mm:ssZ")
            });

        int cardId = card.GetProperty("id").GetInt32();

        await Deck(HttpMethod.Put, $"boards/{boardId}/stacks/{stackId}/cards/{cardId}/assignLabel",
            new { labelId });
    }

    async Task<JsonElement> Deck(HttpMethod method, string path, object? body = null)
    {
        HttpRequestMessage request = new(method, $"{baseUrl}/index.php/apps/deck/api/v1.0/{path}");
        request.Headers.Add("OCS-APIRequest", "true");

        if (body != null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        }

        HttpResponseMessage response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();

        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.Clone();
    }

    string FileUrl(string path) => $"{filesRoot}/{string.Join("/", path.Split('/').Select(Uri.EscapeDataString))}";

    static string Require(string name) =>
        Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException($"Missing environment variable: {name}");
}
