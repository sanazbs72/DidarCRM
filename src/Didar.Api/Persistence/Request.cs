namespace Didar.Api.Persistence;

public sealed class Request
{
    public Guid RequestId { get; set; }
    public required string RequestKey { get; set; }
    public Guid SpecialtyId { get; set; }
    public required string Priority { get; set; }
    public required string Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public Guid? ExpertId { get; set; }
}
