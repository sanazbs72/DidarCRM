using Didar.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Didar.Api.Features.Assignment;

public sealed class AssignmentWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AssignmentWorker> _logger;
    private readonly IAssignmentNotifier _notifier;
    private readonly Guid? _specialtyFilter;
    private readonly TimeSpan _interval;
    private readonly int _maxAttempts;

    public AssignmentWorker(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<AssignmentWorker> logger,
        IAssignmentNotifier notifier)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _notifier = notifier;

        var filter = configuration["Assignment:SpecialtyId"];
        if (Guid.TryParse(filter, out var specialtyId))
        {
            _specialtyFilter = specialtyId;
        }

        _interval = TimeSpan.FromSeconds(Math.Max(0.1, configuration.GetValue("Assignment:IntervalSeconds", 1)));
        _maxAttempts = configuration.GetValue("Assignment:MaxAttemptsPerSpecialty", 3);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessRoundAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Assignment worker round failed.");
            }

            try
            {
                await _notifier.WaitAsync(_interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task ProcessRoundAsync(CancellationToken cancellationToken)
    {
        var specialties = await GetPendingSpecialtiesAsync(cancellationToken);
        foreach (var specialtyId in specialties)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            for (var attempt = 0; attempt < _maxAttempts; attempt++)
            {
                var outcome = await RunAttemptAsync(specialtyId, cancellationToken);
                if (outcome != AssignmentOutcome.Retry)
                {
                    break;
                }
            }
        }
    }

    private async Task<AssignmentOutcome> RunAttemptAsync(Guid specialtyId, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<AssignmentService>();
        return await service.TryAssignNextAsync(specialtyId, cancellationToken);
    }

    private async Task<List<Guid>> GetPendingSpecialtiesAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DidarDbContext>();
        IQueryable<Request> query = db.Requests.Where(x => x.Status == "Pending");
        if (_specialtyFilter is not null)
        {
            query = query.Where(x => x.SpecialtyId == _specialtyFilter);
        }

        return await query
            .Select(x => x.SpecialtyId)
            .Distinct()
            .OrderBy(x => x)
            .ToListAsync(cancellationToken);
    }
}
