using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TenantFlow.Core;
using Xunit;

namespace TenantFlow.Tests;

public sealed class ApiTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Development_cors_allows_the_local_flutter_preflight_contract()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = new TenantFlowFactory("Development");
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/work-items");
        request.Headers.Add("Origin", "http://127.0.0.1:5090");
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "content-type,x-api-key,idempotency-key,if-match");

        using var response = await client.SendAsync(request, ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("http://127.0.0.1:5090", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        var methods = response.Headers.GetValues("Access-Control-Allow-Methods").Single();
        Assert.Contains("GET", methods, StringComparison.Ordinal);
        Assert.Contains("POST", methods, StringComparison.Ordinal);
        Assert.Contains("PUT", methods, StringComparison.Ordinal);
        var headers = response.Headers.GetValues("Access-Control-Allow-Headers").Single();
        Assert.Contains("content-type", headers, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("x-api-key", headers, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("idempotency-key", headers, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("if-match", headers, StringComparison.OrdinalIgnoreCase);

        using var actualRequest = new HttpRequestMessage(HttpMethod.Get, "/api/work-items");
        actualRequest.Headers.Add("Origin", "http://localhost:5090");
        actualRequest.Headers.Add("X-Api-Key", "alpha-test-key");
        using var actualResponse = await client.SendAsync(actualRequest, ct);
        actualResponse.EnsureSuccessStatusCode();
        var exposed = actualResponse.Headers.GetValues("Access-Control-Expose-Headers").Single();
        Assert.Contains("ETag", exposed, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Idempotency-Replayed", exposed, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Location", exposed, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Development_cors_does_not_authorize_an_unrelated_origin()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = new TenantFlowFactory("Development");
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/work-items");
        request.Headers.Add("Origin", "https://unrelated.example");
        request.Headers.Add("Access-Control-Request-Method", "GET");

        using var response = await client.SendAsync(request, ct);

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Tenant_scope_hides_reads_and_updates_from_other_tenants()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = new TenantFlowFactory();
        using var alpha = Client(factory, "alpha-test-key");
        using var beta = Client(factory, "beta-test-key");
        var created = await Create(alpha, "tenant-isolated", ct);

        Assert.Equal(HttpStatusCode.NotFound, (await beta.GetAsync($"/api/work-items/{created.Id}", ct)).StatusCode);
        using var update = new HttpRequestMessage(HttpMethod.Put, $"/api/work-items/{created.Id}")
        {
            Content = JsonContent.Create(new UpdateWorkItem("intrusion", null, WorkItemStatus.Done))
        };
        update.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
        Assert.Equal(HttpStatusCode.NotFound, (await beta.SendAsync(update, ct)).StatusCode);
        Assert.Empty((await beta.GetFromJsonAsync<List<WorkItemDto>>("/api/work-items", ct))!);
    }

    [Fact]
    public async Task Idempotency_is_atomic_under_concurrency_and_rejects_payload_mismatch()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = new TenantFlowFactory();
        using var client = Client(factory, "alpha-test-key");
        var requests = Enumerable.Range(0, 12).Select(_ => Post(client, "same-key", "same-payload", ct));
        var responses = await Task.WhenAll(requests);
        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Created, response.StatusCode));

        var bodies = await Task.WhenAll(responses.Select(response => response.Content.ReadFromJsonAsync<WorkItemDto>(cancellationToken: ct)));
        Assert.Single(bodies.Select(x => x!.Id).Distinct());
        Assert.All(bodies, Assert.NotNull);
        Assert.Contains(responses, response => response.Headers.GetValues("Idempotency-Replayed").Single() == "false");
        Assert.Equal(11, responses.Count(response => response.Headers.GetValues("Idempotency-Replayed").Single() == "true"));

        using var mismatch = await Post(client, "same-key", "different-payload", ct);
        Assert.Equal(HttpStatusCode.Conflict, mismatch.StatusCode);
        Assert.Single((await client.GetFromJsonAsync<List<WorkItemDto>>("/api/work-items", ct))!);

        using var admin = Client(factory, "admin-test-key");
        Assert.Single((await admin.GetFromJsonAsync<List<OutboxMessage>>("/internal/outbox", ct))!);
    }

    [Fact]
    public async Task Stale_etag_is_rejected_without_writing_an_outbox_event()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = new TenantFlowFactory();
        using var client = Client(factory, "alpha-test-key");
        var created = await Create(client, "original", ct);

        using var first = Put(created.Id, "\"1\"", "accepted");
        using var accepted = await client.SendAsync(first, ct);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal("\"2\"", accepted.Headers.ETag!.Tag);

        using var stale = Put(created.Id, "\"1\"", "stale");
        using var rejected = await client.SendAsync(stale, ct);
        Assert.Equal(HttpStatusCode.PreconditionFailed, rejected.StatusCode);

        using var admin = Client(factory, "admin-test-key");
        var events = (await admin.GetFromJsonAsync<List<OutboxMessage>>("/internal/outbox", ct))!;
        Assert.Equal(2, events.Count);
        Assert.DoesNotContain(events, x => x.Payload.Contains("stale", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Outbox_failure_is_recorded_and_due_message_can_be_retried()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = new TenantFlowFactory();
        factory.Publisher.Fail = true;
        using var tenant = Client(factory, "alpha-test-key");
        using var admin = Client(factory, "admin-test-key");
        await Create(tenant, "retry-me", ct);

        var failed = await (await admin.PostAsync("/internal/outbox/dispatch?maximum=10", null, ct))
            .Content.ReadFromJsonAsync<DispatchSummary>(cancellationToken: ct);
        Assert.Equal(new DispatchSummary(0, 1), failed);
        var pending = (await admin.GetFromJsonAsync<List<OutboxMessage>>("/internal/outbox?state=pending", ct))!;
        Assert.Single(pending);
        Assert.Equal(1, pending[0].Attempts);
        Assert.Null(pending[0].ProcessedAt);
        Assert.Contains("Synthetic transport failure", pending[0].LastError);

        factory.Publisher.Fail = false;
        factory.Clock.Advance(TimeSpan.FromSeconds(3));
        var retried = await (await admin.PostAsync("/internal/outbox/dispatch?maximum=10", null, ct))
            .Content.ReadFromJsonAsync<DispatchSummary>(cancellationToken: ct);
        Assert.Equal(new DispatchSummary(1, 0), retried);
        Assert.Single(factory.Publisher.Published);
        Assert.Single((await admin.GetFromJsonAsync<List<OutboxMessage>>("/internal/outbox?state=processed", ct))!);
    }

    private static HttpClient Client(TenantFlowFactory factory, string key)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", key);
        return client;
    }

    private static async Task<WorkItemDto> Create(HttpClient client, string title, CancellationToken cancellationToken)
    {
        using var response = await Post(client, $"key-{Guid.NewGuid():N}", title, cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<WorkItemDto>(JsonOptions, cancellationToken))!;
    }

    private static Task<HttpResponseMessage> Post(HttpClient client, string key, string title, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/work-items")
        {
            Content = JsonContent.Create(new CreateWorkItem(title, "test"))
        };
        request.Headers.Add("Idempotency-Key", key);
        return client.SendAsync(request, cancellationToken);
    }

    private static HttpRequestMessage Put(Guid id, string etag, string title)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/work-items/{id}")
        {
            Content = JsonContent.Create(new UpdateWorkItem(title, null, WorkItemStatus.Done))
        };
        request.Headers.TryAddWithoutValidation("If-Match", etag);
        return request;
    }
}
