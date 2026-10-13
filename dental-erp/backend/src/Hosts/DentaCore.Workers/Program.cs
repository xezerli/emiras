using DentaCore.Billing.Infrastructure;
using DentaCore.BuildingBlocks.Infrastructure.Messaging;
using DentaCore.Patient.Infrastructure;
using DentaCore.Search.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddPatientModule(builder.Configuration);
builder.Services.AddBillingModule(builder.Configuration);
builder.Services.AddMessaging(builder.Configuration);
builder.Services.AddPatientIntegrationConsumers();
builder.Services.AddBillingIntegrationConsumers();
builder.Services.AddSearchModule(builder.Configuration);
builder.Services.AddSearchIndexing();      // audit.recorded consumer-i və pasiyent indexer-i
builder.Services.AddOutboxPublisher();      // consumer-lərdən SONRA: topologiya publisher-dən əvvəl elan olunsun
builder.Services.AddTenantMaintenance();

var app = builder.Build();
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.Run();

public partial class Program;
