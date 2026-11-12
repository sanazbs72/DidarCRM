using System.Globalization;
using Didar.Api.Features.Assignment;
using Didar.Api.Features.Experts;
using Didar.Api.Features.Requests;
using Didar.Api.Persistence;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var fixedNow = builder.Configuration["Assignment:FixedNow"];
if (DateTimeOffset.TryParse(fixedNow, CultureInfo.InvariantCulture, DateTimeStyles.None, out var fixedTime))
{
    builder.Services.AddSingleton<TimeProvider>(new FixedTimeProvider(fixedTime));
}
else
{
    builder.Services.AddSingleton(TimeProvider.System);
}

builder.Services.AddDbContext<DidarDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Didar")));
builder.Services.AddScoped<RegisterRequest>();
builder.Services.AddScoped<GetRequest>();
builder.Services.AddScoped<ChangeRequestStatus>();
builder.Services.AddScoped<ChangeExpertAvailability>();
builder.Services.AddScoped<AssignmentService>();
builder.Services.AddSingleton<QueuePolicy>();
builder.Services.AddSingleton<IAssignmentNotifier, AssignmentNotifier>();
builder.Services.AddSingleton(BuildAssignmentBarrier(builder.Configuration));

if (builder.Configuration.GetValue("Assignment:Enabled", true))
{
    builder.Services.AddHostedService<AssignmentWorker>();
}

var app = builder.Build();
app.UseExceptionHandler();
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapRequestEndpoints();
app.MapExpertEndpoints();
app.Run();

static IAssignmentBarrier BuildAssignmentBarrier(IConfiguration configuration)
{
    var directory = configuration["Assignment:BarrierDirectory"];
    if (string.IsNullOrWhiteSpace(directory))
    {
        return NoopAssignmentBarrier.Instance;
    }

    var parties = configuration.GetValue("Assignment:BarrierParties", 2);
    var timeoutSeconds = configuration.GetValue("Assignment:BarrierTimeoutSeconds", 15);
    return new DirectoryAssignmentBarrier(directory, parties, TimeSpan.FromSeconds(timeoutSeconds));
}

public partial class Program;
