using Didar.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Didar.Api.Features.Requests;

public sealed class GetRequest(DidarDbContext dbContext)
{
    public Task<RequestReadResult?> ExecuteAsync(Guid requestId, CancellationToken cancellationToken) =>
        dbContext.Requests
            .AsNoTracking()
            .Where(x => x.RequestId == requestId)
            .Select(x => new RequestReadResult(x.RequestId, x.Status))
            .SingleOrDefaultAsync(cancellationToken);
}

public sealed record RequestReadResult(Guid RequestId, string Status);
