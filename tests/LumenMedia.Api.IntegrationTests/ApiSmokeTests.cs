using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace LumenMedia.Api.IntegrationTests;

public class ApiSmokeTests(LumenMediaApiFactory factory) : IClassFixture<LumenMediaApiFactory>
{
    private readonly LumenMediaApiFactory _factory = factory;

    [Fact]
    public async Task Health_reports_database_healthy()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("status").GetString().Should().Be("Healthy");
        doc.RootElement.GetProperty("checks").GetProperty("database").GetString().Should().Be("Healthy");
    }

    [Fact]
    public async Task Protected_endpoint_requires_authentication()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/v1/libraries");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Full_setup_login_and_library_flow_works_end_to_end()
    {
        var client = _factory.CreateClient();

        var infoBefore = await client.GetFromJsonAsync<JsonElement>("/api/v1/server/info");
        infoBefore.GetProperty("setupCompleted").GetBoolean().Should().BeFalse();

        // First-run setup creates the admin.
        var setup = await client.PostAsJsonAsync("/api/v1/setup", new { username = "root", password = "password123" });
        setup.StatusCode.Should().Be(HttpStatusCode.Created);

        // Second setup is rejected.
        var secondSetup = await client.PostAsJsonAsync("/api/v1/setup", new { username = "root2", password = "password123" });
        secondSetup.StatusCode.Should().Be(HttpStatusCode.Conflict);

        // Login yields a bearer token.
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { username = "root", password = "password123" });
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        var loginBody = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        var accessToken = loginBody.RootElement.GetProperty("accessToken").GetString();
        accessToken.Should().NotBeNullOrEmpty();

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        // /auth/me works with the token.
        var me = await client.GetAsync("/api/v1/auth/me");
        me.StatusCode.Should().Be(HttpStatusCode.OK);

        // Admin creates a library.
        var createLibrary = await client.PostAsJsonAsync("/api/v1/libraries", new
        {
            name = "Movies",
            type = "Movies",
            paths = new[] { "/tmp/media/movies" },
        });
        createLibrary.StatusCode.Should().Be(HttpStatusCode.Created);
        var libraryBody = JsonDocument.Parse(await createLibrary.Content.ReadAsStringAsync());
        var libraryId = libraryBody.RootElement.GetProperty("id").GetString();
        libraryId.Should().NotBeNullOrEmpty();

        // Listing items returns an empty paged envelope.
        var items = await client.GetAsync($"/api/v1/libraries/{libraryId}/items");
        items.StatusCode.Should().Be(HttpStatusCode.OK);
        var itemsBody = JsonDocument.Parse(await items.Content.ReadAsStringAsync());
        itemsBody.RootElement.GetProperty("total").GetInt32().Should().Be(0);
        itemsBody.RootElement.GetProperty("items").GetArrayLength().Should().Be(0);

        // Enqueue a scan job.
        var scan = await client.PostAsync($"/api/v1/libraries/{libraryId}/scan", null);
        scan.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }
}
