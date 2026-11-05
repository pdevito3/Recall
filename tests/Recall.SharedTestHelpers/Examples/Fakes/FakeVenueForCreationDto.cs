namespace Recall.SharedTestHelpers.Examples.Fakes;

using AutoBogus;
using Recall.Server.Examples.Venues.Dtos;

public sealed class FakeVenueForCreationDto : AutoFaker<VenueForCreationDto>
{
    public FakeVenueForCreationDto()
    {
        RuleFor(x => x.Name, VenueRules.Name);
        RuleFor(x => x.TimeZone, VenueRules.TimeZone);
    }
}
