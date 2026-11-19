using System.Net;
using System.Text;
using FinTrack.Budget.Contracts.V1.Buckets;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Web.Api;
using FinTrack.Web.Buckets;

namespace FinTrack.Web.Tests.Buckets;

/// <summary>The bucket calls go to the Api's routes (plan 1a, API surface) with the contract's bodies.</summary>
public class BucketsClientTests
{
    private const string Base = "http://localhost:7071/api/";
    private const string IdText = "0190a1b2-c3d4-7e5f-8a6b-7c8d9e0f1a2b";
    private static readonly Guid Id = Guid.Parse(IdText);

    private readonly List<(HttpMethod Method, string Uri, string? Body)> sent = [];

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false, Base + "v1/buckets")]
    [InlineData(true, Base + "v1/buckets?includeArchived=true")]
    public async Task List_AsksForArchivedBucketsOnlyWhenWanted(bool includeArchived, string expected)
    {
        var client = Client("[{\"id\":\"" + IdText + "\",\"name\":\"Cushion\",\"currency\":\"PLN\",\"kind\":\"Savings\",\"profile\":null,\"status\":\"Active\",\"systemRole\":\"None\"}]");

        var result = await client.ListAsync(includeArchived, Cancellation);

        result.Value!.ShouldHaveSingleItem().Name.ShouldBe("Cushion");
        sent.ShouldHaveSingleItem().ShouldBe((HttpMethod.Get, expected, null));
    }

    [Fact]
    public async Task Create_PostsTheRequest()
    {
        await Client().CreateAsync(new CreateBucketRequest(Id, "Cushion", Currency.Pln, BucketKind.Savings), Cancellation);

        sent.ShouldHaveSingleItem().ShouldBe((HttpMethod.Post, Base + "v1/buckets",
            "{\"id\":\"" + IdText + "\",\"name\":\"Cushion\",\"currency\":\"PLN\",\"kind\":\"Savings\",\"profile\":null}"));
    }

    [Fact]
    public async Task Rename_PostsTheNewName_ToTheBucket()
    {
        await Client().RenameAsync(Id, new RenameBucketRequest("Safety net"), Cancellation);

        sent.ShouldHaveSingleItem().ShouldBe((HttpMethod.Post, Base + "v1/buckets/" + IdText + "/rename", "{\"name\":\"Safety net\"}"));
    }

    [Fact]
    public async Task Archive_PostsWithoutABody()
    {
        var result = await Client().ArchiveAsync(Id, Cancellation);

        result.IsSuccess.ShouldBeTrue();
        sent.ShouldHaveSingleItem().ShouldBe((HttpMethod.Post, Base + "v1/buckets/" + IdText + "/archive", null));
    }

    private BucketsClient Client(string? answer = null) =>
        new(new ApiClient(new HttpClient(new RecordingHandler(sent, answer)) { BaseAddress = new Uri(Base) }));

    private sealed class RecordingHandler(List<(HttpMethod, string, string?)> sent, string? answer) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            sent.Add((request.Method, request.RequestUri!.ToString(), body));
            return answer is null
                ? new HttpResponseMessage(HttpStatusCode.NoContent)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
        }
    }
}
