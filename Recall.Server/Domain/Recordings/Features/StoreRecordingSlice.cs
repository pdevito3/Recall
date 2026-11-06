namespace Recall.Server.Domain.Recordings.Features;

using System.Diagnostics;
using System.Security.Cryptography;
using Databases;
using Dtos;
using Exceptions;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Resources;
using Resources.Entrypoints.Http;
using Resources.Storage;
using Serilog;

public static class StoreRecordingSlice
{
    public sealed record Command(string Id, int Index, byte[] Body) : IRequest<RecordingSliceDto>, IRunsAgainWhenRaced;

    public sealed class Handler(AppDbContext dbContext, FileStore fileStore) : IRequestHandler<Command, RecordingSliceDto>
    {
        private readonly ILogger _logger = Log.ForContext<Handler>();

        public async Task<RecordingSliceDto> Handle(Command request, CancellationToken cancellationToken)
        {
            Activity.Current?.SetTag("app.recording_id", request.Id);
            Recording.ValidateSliceIndex(request.Index);
            ValidationException.Must(request.Body.Length > 0, "body", "A slice must have bytes.");

            var recording = await dbContext.Recordings
                .TagWithOperationCallSite("recordings.slice.load")
                .GetById(request.Id, cancellationToken);

            var sha256 = Convert.ToHexStringLower(SHA256.HashData(request.Body));
            var stored = await dbContext.RecordingSlices
                .TagWithOperationCallSite("recordings.slice.existing")
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.RecordingId == recording.Id && s.Index == request.Index, cancellationToken);
            if (stored is not null)
            {
                if (stored.Sha256 != sha256)
                    throw new ConflictException("index", "This slice is stored with other bytes. A stored slice does not change.");

                return new RecordingSliceDto
                {
                    Index = stored.Index,
                    ByteLength = stored.ByteLength,
                    Sha256 = stored.Sha256,
                    HighestStoredSlice = recording.HighestStoredSlice,
                };
            }

            if (request.Index == 0)
                ValidationException.Must(RecordingFiles.StartsAsFormat(recording.Format, request.Body), "body",
                    "The first slice is not audio in the format that this Recording names.");

            recording.StoreSlice(request.Index, request.Body.Length);
            dbContext.RecordingSlices.Add(RecordingSlice.Of(recording, request.Index, request.Body.Length, sha256));

            using var content = new MemoryStream(request.Body, writable: false);
            await fileStore.WriteAsync(RecordingFiles.Slice(recording, request.Index, sha256), content, cancellationToken);
            await SaveOrRemoveLateFilesAsync(recording, cancellationToken);

            Activity.Current?.SetTag("app.recording.highest_slice", recording.HighestStoredSlice);
            _logger.Here().Information("Stored slice {SliceIndex} of recording {RecordingId}", request.Index, recording.Id);
            return new RecordingSliceDto
            {
                Index = request.Index,
                ByteLength = request.Body.Length,
                Sha256 = sha256,
                HighestStoredSlice = recording.HighestStoredSlice,
            };
        }

        // A delete of the Recording, its Session, its owner, or its tenant can run while the file goes to disk. That delete
        // removed its folder first, so this write is the one that can leave a file with no row.
        private async Task SaveOrRemoveLateFilesAsync(Recording recording, CancellationToken cancellationToken)
        {
            try
            {
                if (!await IsStillLiveAsync(recording, cancellationToken))
                    throw new NotFoundException("The recording was not found.");

                await dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (Exception exception) when (exception is NotFoundException or StaleVersionException or DbUpdateException)
            {
                dbContext.DiscardLostRace();
                if (await IsStillLiveAsync(recording, cancellationToken))
                    throw;

                fileStore.DeleteUnder(RecordingFiles.Folder(recording));
                _logger.Here().Information("Removed a late file of deleted recording {RecordingId}", recording.Id);
                throw new NotFoundException("The recording was not found.");
            }
        }

        private async Task<bool> IsStillLiveAsync(Recording recording, CancellationToken cancellationToken)
            => await dbContext.Recordings
                .TagWithOperationCallSite("recordings.slice.live")
                .AsNoTracking()
                .AnyAsync(r => r.Id == recording.Id, cancellationToken)
               && await dbContext.Tenants
                   .TagWithOperationCallSite("recordings.slice.tenant_live")
                   .AsNoTracking()
                   .AnyAsync(t => t.Id == recording.TenantId, cancellationToken);
    }
}

public sealed class StoreRecordingSliceEndpoint : IEndpoint
{
    public const string BodyType = "application/octet-stream";

    public void Map(IEndpointRouteBuilder versionedGroup) => versionedGroup
        .MapPut("/recordings/{id}/slices/{index}", Handle)
        .WithName("StoreRecordingSlice")
        .WithTags("Recordings")
        .MapToApiVersion(ApiVersions.V1)
        .RequireAuthorization()
        .WithMetadata(new RequestSizeLimitAttribute(Recording.MaxSliceBytes))
        .Accepts<byte[]>(BodyType)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
        .ProducesValidationProblem(StatusCodes.Status409Conflict)
        .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

    /// <summary>
    /// Stores one slice of the audio. A repeat with the same bytes gives the same answer, and slices can come in any order.
    /// A slice that has other bytes than the stored one gives 409, because a stored slice does not change.
    /// </summary>
    public static async Task<Ok<RecordingSliceDto>> Handle(
        string id, int index, HttpRequest request, IMediator mediator, CancellationToken cancellationToken)
    {
        var body = await ReadBoundedAsync(request, cancellationToken);
        return TypedResults.Ok(await mediator.Send(new StoreRecordingSlice.Command(id, index, body), cancellationToken));
    }

    // Kestrel applies the size limit of the route, and a host that has no such limit still stops here.
    private static async Task<byte[]> ReadBoundedAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (request.ContentLength > Recording.MaxSliceBytes)
            throw new BadHttpRequestException("The slice is too large.", StatusCodes.Status413PayloadTooLarge);

        using var body = new MemoryStream();
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = await request.Body.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (body.Length + read > Recording.MaxSliceBytes)
                throw new BadHttpRequestException("The slice is too large.", StatusCodes.Status413PayloadTooLarge);

            body.Write(buffer, 0, read);
        }

        return body.ToArray();
    }
}
