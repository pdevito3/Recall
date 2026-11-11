namespace Recall.FunctionalTests.FunctionalTests.Recordings;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Recall.Server.Databases;
using Recall.Server.Domain.Recordings;
using Recall.Server.Domain.Recordings.Dtos;
using Recall.Server.Domain.Tenants;
using Recall.Server.Domain.Users;
using Recall.Server.Resources.Storage;
using Shouldly;
using TestUtilities;

internal static class RecordingScenario
{
    public static readonly byte[] WebmStart = [0x1A, 0x45, 0xDF, 0xA3, 1, 2, 3, 4, 5];

    public static byte[] Bytes(int length, byte value = 7) => Enumerable.Repeat(value, length).ToArray();

    public static async Task<RecordingDto> AddRecordingAsync(this HttpClient client, string sessionId, string format = "Webm", string? clientRecordingId = null)
    {
        var response = await client.PostAsJsonAsync(ApiRoutes.Recordings.OfSession(sessionId), new
        {
            clientRecordingId = clientRecordingId ?? Guid.NewGuid().ToString(),
            format,
        });
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<RecordingDto>()).ShouldNotBeNull();
    }

    public static async Task<HttpResponseMessage> PutSliceAsync(this HttpClient client, string id, int index, byte[] body, string contentType = "application/octet-stream")
    {
        using var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return await client.PutAsync(ApiRoutes.Recordings.Slice(id, index), content);
    }

    public static async Task<HttpResponseMessage> FinishAsync(this HttpClient client, string id, int sliceCount, long byteLength)
        => await client.PostAsJsonAsync(ApiRoutes.Recordings.Finish(id), new { sliceCount, byteLength });

    public static async Task<RecordingDto> GetRecordingAsync(this HttpClient client, string id)
        => (await client.GetFromJsonAsync<RecordingDto>(ApiRoutes.Recordings.GetRecord(id))).ShouldNotBeNull();

    public static async Task<RecordingDto> AddFinishedRecordingAsync(this HttpClient client, string sessionId)
    {
        var recording = await client.AddRecordingAsync(sessionId);
        (await client.PutSliceAsync(recording.Id, 0, WebmStart)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.PutSliceAsync(recording.Id, 1, Bytes(4))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.FinishAsync(recording.Id, 2, WebmStart.Length + 4)).StatusCode.ShouldBe(HttpStatusCode.OK);
        return await client.GetRecordingAsync(recording.Id);
    }

    /// <summary>The row of the Recording. Its audio file is the one that the row names.</summary>
    public static Recording RowOf(string recordingId, User owner)
    {
        using var scope = TestingWebApplicationFactory.Instance.Services.GetRequiredService<IServiceScopeFactory>()
            .CreateJournalScope(new JournalOwner(owner.Id, owner.TenantId));
        return scope.ServiceProvider.GetRequiredService<AppDbContext>().Recordings.AsNoTracking().Single(r => r.Id == recordingId);
    }

    public static byte[] StoredAudio(string recordingId, User owner)
    {
        var store = TestingWebApplicationFactory.Instance.Services.GetRequiredService<FileStore>();
        using var file = store.OpenRead(RecordingFiles.Audio(RowOf(recordingId, owner)));
        using var copy = new MemoryStream();
        file.CopyTo(copy);
        return copy.ToArray();
    }
}
