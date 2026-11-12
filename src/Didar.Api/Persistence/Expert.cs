namespace Didar.Api.Persistence;

public sealed class Expert
{
    public Guid ExpertId { get; set; }
    public required string Status { get; set; }
    public int Capacity { get; set; }
    public DateTime? LastAssignedAt { get; set; }
}
