namespace Didar.Api.Persistence;

public sealed class ExpertSpecialty
{
    public Guid ExpertId { get; set; }
    public Guid SpecialtyId { get; set; }
    public required string Status { get; set; }
}
