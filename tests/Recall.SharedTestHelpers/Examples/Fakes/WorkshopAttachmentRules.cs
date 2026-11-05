namespace Recall.SharedTestHelpers.Examples.Fakes;

using Bogus;
using Recall.Server.Examples.Workshops;
using Recall.SharedTestHelpers.Fakes.Attachments;

public static class WorkshopAttachmentRules
{
    public static string FileName(Faker f) => $"{f.Hacker.Noun()} notes.pdf";

    public static string ClaimedMediaType() => "application/pdf";

    public static byte[] Head() => SampleFiles.Pdf;

    public static long SizeBytes(Faker f) => f.Random.Long(1, WorkshopAttachment.UploadRules.MaxSizeBytes);
}
