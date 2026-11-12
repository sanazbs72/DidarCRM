namespace Didar.Api.Features.Assignment;


public sealed class QueuePolicy(IConfiguration configuration)
{
    public TimeSpan AgingThreshold { get; } =
        TimeSpan.FromSeconds(Math.Max(0, configuration.GetValue("Assignment:AgingThresholdSeconds", 300)));

    public const string EffectivePrioritySql =
        "CASE WHEN priority = 'Urgent' OR (priority = 'Normal' AND created_at <= @now - @aging) THEN 0 ELSE 1 END";
}
