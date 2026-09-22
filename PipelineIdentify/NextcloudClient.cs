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
    readonly string filesRoot;

    public NextcloudClient(string url, string user, string password)
    {
        baseUrl = url.TrimEnd('/');
        filesRoot = $"{baseUrl}/remote.php/dav/files/{Uri.EscapeDataString(user)}";

        string token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"));
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
    }

    public static NextcloudClient FromEnvironment()
    {
        string url = Require("NC_URL");

        if (!url.StartsWith("https://"))
        {
            throw new InvalidOperationException("NC_URL must start with https://");
        }

        return new NextcloudClient(url, Require("NC_USER"), Require("NC_APP_PASSWORD"));
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

    string FileUrl(string path) => $"{filesRoot}/{string.Join("/", path.Split('/').Select(Uri.EscapeDataString))}";

    static string Require(string name) =>
        Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException($"Missing environment variable: {name}");
}
