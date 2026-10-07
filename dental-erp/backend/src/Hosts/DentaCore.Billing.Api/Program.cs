using System.Globalization;
using DentaCore.Audit.Infrastructure;
using DentaCore.Billing.Application;
using DentaCore.Billing.Infrastructure;
using DentaCore.BuildingBlocks.Application;
using DentaCore.BuildingBlocks.Infrastructure.Auth;
using DentaCore.BuildingBlocks.Infrastructure.Http;
using DentaCore.BuildingBlocks.Infrastructure.Tenancy;
using DentaCore.Patient.Infrastructure;
using DentaCore.Scheduling.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// Patient (IPatientDirectory) və Scheduling (ICareRelationships: həkimin "öz pasiyenti") Billing-in oxuduğu müqavilələri təmin edir
builder.Services.AddPatientModule(builder.Configuration);
builder.Services.AddSchedulingModule(builder.Configuration);
builder.Services.AddBillingModule(builder.Configuration);
builder.Services.AddAuditModule(builder.Configuration);
builder.Services.AddJwtValidation(builder.Configuration);

var app = builder.Build();

app.UseMiddleware<ConcurrencyExceptionMiddleware>();
app.UseMiddleware<TenantResolutionMiddleware>();
app.UseAuthentication();
app.UseMiddleware<TenantClaimGuardMiddleware>();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

var billing = app.MapGroup("/v1").RequireAuthorization();

// Qiymət siyahısı
billing.MapGet("/services", async (string? q, bool? includeInactive, ISender sender, CancellationToken ct) =>
    (await sender.Send(new ListServicesQuery(q, includeInactive ?? false), ct)).ToHttpResult(Results.Ok));
billing.MapPost("/services", async (ServiceRequest body, ISender sender, CancellationToken ct) =>
{
    var result = await sender.Send(new CreateServiceCommand(body.Code, body.ProcedureCode, body.Name, body.Category, body.Price, body.Currency, body.VatRate ?? 0m), ct);
    return result.ToHttpResult(s => Results.Created($"/v1/services/{s.Id}", s));
});
billing.MapPut("/services/{id:guid}", async (Guid id, ServiceRequest body, HttpRequest request, ISender sender, CancellationToken ct) =>
{
    if (!TryParseIfMatch(request.Headers.IfMatch, out var version))
    {
        return PreconditionRequired();
    }

    var result = await sender.Send(new UpdateServiceCommand(id, version, body.Name, body.Category, body.Price, body.VatRate ?? 0m, body.IsActive ?? true), ct);
    return result.ToHttpResult(Results.Ok);
});

// Fakturalar
billing.MapGet("/invoices", async (Guid? patientId, string? status, bool? overdue, string? cursor, int? limit, HttpContext http, ISender sender, CancellationToken ct) =>
{
    http.Response.Headers.CacheControl = "no-store";
    return (await sender.Send(new ListInvoicesQuery(patientId, status, overdue ?? false, cursor, limit ?? 25), ct)).ToHttpResult(Results.Ok);
});
billing.MapPost("/invoices", async (InvoiceRequest body, ISender sender, CancellationToken ct) =>
{
    var result = await sender.Send(
        new CreateInvoiceCommand(body.Kind, body.PatientId, body.BranchId, body.VisitId, body.PlanId, body.PromoCode, body.InsurancePolicyId, body.DueDate, body.Items ?? []), ct);
    return result.ToHttpResult(i => Results.Created($"/v1/invoices/{i.Id}", i));
});
billing.MapGet("/invoices/{id:guid}", async (Guid id, HttpContext http, ISender sender, CancellationToken ct) =>
{
    var result = await sender.Send(new GetInvoiceQuery(id), ct);
    if (result.IsSuccess)
    {
        http.Response.Headers.ETag = $"\"{result.Value.RowVersion}\"";
        http.Response.Headers.CacheControl = "no-store";
    }

    return result.ToHttpResult(Results.Ok);
});
billing.MapPost("/invoices/{id:guid}/issue", async (Guid id, ISender sender, CancellationToken ct) =>
    (await sender.Send(new IssueInvoiceCommand(id), ct)).ToHttpResult(Results.Ok));
billing.MapPost("/invoices/{id:guid}/void", async (Guid id, ISender sender, CancellationToken ct) =>
    (await sender.Send(new VoidInvoiceCommand(id), ct)).ToHttpResult(Results.Ok));

// Ödəniş və geri qaytarma: Idempotency-Key məcburidir (şəbəkə xətasında təkrar göndərmə pulu ikiqat çıxmasın)
billing.MapPost("/invoices/{id:guid}/payments", async (Guid id, PaymentRequest body, HttpRequest request, ISender sender, CancellationToken ct) =>
{
    if (!TryGetIdempotencyKey(request, out var key))
    {
        return IdempotencyRequired();
    }

    var result = await sender.Send(new RecordPaymentCommand(id, body.Method, body.Amount, body.Reference, key), ct);
    return result.ToHttpResult(p => Results.Created($"/v1/invoices/{id}", p));
});
billing.MapPost("/payments/{id:guid}/refund", async (Guid id, RefundRequest body, HttpRequest request, ISender sender, CancellationToken ct) =>
{
    if (!TryGetIdempotencyKey(request, out var key))
    {
        return IdempotencyRequired();
    }

    var result = await sender.Send(new RefundPaymentCommand(id, body.Amount, body.Reason ?? string.Empty, key), ct);
    return result.ToHttpResult(p => Results.Created($"/v1/payments/{p.Id}", p));
});

// Kassa smeni
billing.MapPost("/cash-shifts/open", async (OpenShiftRequest body, ISender sender, CancellationToken ct) =>
{
    var result = await sender.Send(new OpenCashShiftCommand(body.BranchId, body.OpeningCash), ct);
    return result.ToHttpResult(s => Results.Created($"/v1/cash-shifts/{s.Id}", s));
});
billing.MapGet("/cash-shifts/current", async (ISender sender, CancellationToken ct) =>
    (await sender.Send(new GetCurrentShiftQuery(), ct)).ToHttpResult(Results.Ok));
billing.MapPost("/cash-shifts/{id:guid}/close", async (Guid id, CloseShiftRequest body, ISender sender, CancellationToken ct) =>
    (await sender.Send(new CloseCashShiftCommand(id, body.ClosingCash), ct)).ToHttpResult(Results.Ok));

await app.RunAsync();

static bool TryParseIfMatch(Microsoft.Extensions.Primitives.StringValues header, out int version)
{
    version = 0;
    return header.Count == 1 && int.TryParse(header[0]?.Trim('"'), NumberStyles.None, CultureInfo.InvariantCulture, out version);
}

static bool TryGetIdempotencyKey(HttpRequest request, out string key)
{
    key = request.Headers["Idempotency-Key"].ToString().Trim();
    return key.Length is > 0 and <= 64;
}

static IResult PreconditionRequired() =>
    Results.Problem(statusCode: StatusCodes.Status428PreconditionRequired, title: "If-Match header with the current row version is required.",
        type: "https://errors.dentacore.app/precondition.required", extensions: new Dictionary<string, object?> { ["code"] = "precondition.required" });

static IResult IdempotencyRequired() =>
    Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Idempotency-Key header (max 64 characters) is required.",
        type: "https://errors.dentacore.app/idempotency.key_required", extensions: new Dictionary<string, object?> { ["code"] = "idempotency.key_required" });

internal sealed record ServiceRequest(string Code, string? ProcedureCode, string Name, string? Category, decimal Price, string? Currency, decimal? VatRate, bool? IsActive);

internal sealed record InvoiceRequest(
    string Kind, Guid PatientId, Guid BranchId, Guid? VisitId, Guid? PlanId, string? PromoCode, Guid? InsurancePolicyId, DateOnly? DueDate, IReadOnlyList<InvoiceItemInput>? Items);

internal sealed record PaymentRequest(string Method, decimal Amount, string? Reference);

internal sealed record RefundRequest(decimal Amount, string? Reason);

internal sealed record OpenShiftRequest(Guid BranchId, decimal OpeningCash);

internal sealed record CloseShiftRequest(decimal ClosingCash);

public partial class Program;
