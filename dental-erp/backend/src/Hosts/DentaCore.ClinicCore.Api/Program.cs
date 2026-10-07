using System.Globalization;
using System.Text.Json;
using DentaCore.Audit.Infrastructure;
using DentaCore.BuildingBlocks.Application;
using DentaCore.BuildingBlocks.Infrastructure.Auth;
using DentaCore.BuildingBlocks.Infrastructure.Http;
using DentaCore.BuildingBlocks.Infrastructure.Tenancy;
using DentaCore.Patient.Application;
using DentaCore.Patient.Infrastructure;
using DentaCore.Scheduling.Application;
using DentaCore.Scheduling.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddPatientModule(builder.Configuration);
builder.Services.AddSchedulingModule(builder.Configuration);
builder.Services.AddAuditModule(builder.Configuration);
builder.Services.AddJwtValidation(builder.Configuration);

var app = builder.Build();

app.UseMiddleware<ConcurrencyExceptionMiddleware>();
app.UseMiddleware<TenantResolutionMiddleware>();
app.UseAuthentication();
app.UseMiddleware<TenantClaimGuardMiddleware>();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

var patients = app.MapGroup("/v1/patients").RequireAuthorization();

patients.MapGet("/", async (string? q, Guid? branchId, string? cursor, int? limit, ISender sender, CancellationToken ct) =>
{
    var result = await sender.Send(new SearchPatientsQuery(q, branchId, cursor, limit ?? 25), ct);
    return result.ToHttpResult(Results.Ok);
});

patients.MapPost("/", async (RegisterPatientRequest body, bool? confirmDuplicate, ISender sender, CancellationToken ct) =>
{
    var result = await sender.Send(
        new RegisterPatientCommand(
            body.BranchId, body.FirstName, body.LastName, body.FatherName, body.BirthDate, body.Gender, body.Phone, body.Email,
            body.NationalId, body.Address, body.PreferredChannel, body.MarketingOptIn ?? false, body.ReferralSource, confirmDuplicate ?? false),
        ct);
    return result.ToHttpResult(p => Results.Created($"/v1/patients/{p.Id}", p));
});

patients.MapGet("/{id:guid}", async (Guid id, bool? reveal, HttpContext http, ISender sender, CancellationToken ct) =>
{
    var result = await sender.Send(new GetPatientQuery(id, reveal ?? false), ct);
    if (result.IsSuccess)
    {
        http.Response.Headers.ETag = $"\"{result.Value.RowVersion}\"";
        http.Response.Headers.CacheControl = "no-store";   // PHI brauzer/proxy keşinə düşməsin
    }

    return result.ToHttpResult(Results.Ok);
});

patients.MapPatch("/{id:guid}", async (Guid id, bool? confirmDuplicate, HttpRequest request, HttpContext http, ISender sender, CancellationToken ct) =>
{
    if (!TryParseIfMatch(request.Headers.IfMatch, out var version))
    {
        return Results.Problem(statusCode: StatusCodes.Status428PreconditionRequired, title: "If-Match header with the current row version is required.",
            type: "https://errors.dentacore.app/precondition.required", extensions: new Dictionary<string, object?> { ["code"] = "precondition.required" });
    }

    Dictionary<string, JsonElement>? patch;
    try
    {
        patch = await JsonSerializer.DeserializeAsync<Dictionary<string, JsonElement>>(request.Body, cancellationToken: ct);
    }
    catch (JsonException)
    {
        patch = null;
    }

    if (patch is null || patch.Count == 0)
    {
        return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "A non-empty JSON object is required.",
            type: "https://errors.dentacore.app/request.invalid_body", extensions: new Dictionary<string, object?> { ["code"] = "request.invalid_body" });
    }

    var result = await sender.Send(new UpdatePatientCommand(id, version, patch, confirmDuplicate ?? false), ct);
    if (result.IsSuccess)
    {
        http.Response.Headers.ETag = $"\"{result.Value.Patient.RowVersion}\"";
    }

    return result.ToHttpResult(r => Results.Ok(r.Patient));
});

patients.MapDelete("/{id:guid}", async (Guid id, ISender sender, CancellationToken ct) =>
{
    var result = await sender.Send(new DeletePatientCommand(id), ct);
    return result.ToHttpResult(_ => Results.NoContent());
});

patients.MapGet("/{id:guid}/medical-profile", async (Guid id, ISender sender, CancellationToken ct) =>
{
    var result = await sender.Send(new GetMedicalProfileQuery(id), ct);
    return result.ToHttpResult(Results.Ok);
});

patients.MapPost("/{id:guid}/allergies", async (Guid id, AllergyRequest body, ISender sender, CancellationToken ct) =>
{
    var result = await sender.Send(new AddAllergyCommand(id, body.Substance, body.Reaction, body.Severity ?? "moderate"), ct);
    return result.ToHttpResult(a => Results.Created($"/v1/patients/{id}/medical-profile", a));
});

// ---------------- Scheduling ----------------
var appointments = app.MapGroup("/v1/appointments").RequireAuthorization();

appointments.MapGet("/", async (DateTimeOffset from, DateTimeOffset to, Guid? providerId, Guid? roomId, Guid? branchId, string[]? status, ISender sender, CancellationToken ct) =>
{
    var result = await sender.Send(new ListAppointmentsQuery(from, to, providerId, roomId, branchId, status), ct);
    return result.ToHttpResult(Results.Ok);
});

appointments.MapGet("/availability", async (Guid providerId, DateOnly date, int durationMin, ISender sender, CancellationToken ct) =>
{
    var result = await sender.Send(new GetAvailabilityQuery(providerId, date, durationMin), ct);
    return result.ToHttpResult(Results.Ok);
});

appointments.MapPost("/", async (BookAppointmentRequest body, ISender sender, CancellationToken ct) =>
{
    var result = await sender.Send(
        new BookAppointmentCommand(body.BranchId, body.PatientId, body.ProviderId, body.RoomId, body.Start, body.End, body.Reason, body.Source, body.ReminderMinutes), ct);
    return result.ToHttpResult(a => Results.Created($"/v1/appointments/{a.Id}", a));
});

appointments.MapGet("/{id:guid}", async (Guid id, HttpContext http, ISender sender, CancellationToken ct) =>
{
    var result = await sender.Send(new GetAppointmentQuery(id), ct);
    if (result.IsSuccess)
    {
        http.Response.Headers.ETag = $"\"{result.Value.RowVersion}\"";
    }

    return result.ToHttpResult(Results.Ok);
});

appointments.MapPatch("/{id:guid}", async (Guid id, HttpRequest request, HttpContext http, ISender sender, CancellationToken ct) =>
{
    if (!TryParseIfMatch(request.Headers.IfMatch, out var version))
    {
        return Results.Problem(statusCode: StatusCodes.Status428PreconditionRequired, title: "If-Match header with the current row version is required.",
            type: "https://errors.dentacore.app/precondition.required", extensions: new Dictionary<string, object?> { ["code"] = "precondition.required" });
    }

    Dictionary<string, JsonElement>? fields;
    try
    {
        fields = await JsonSerializer.DeserializeAsync<Dictionary<string, JsonElement>>(request.Body, cancellationToken: ct);
    }
    catch (JsonException)
    {
        fields = null;
    }

    if (fields is null || fields.Count == 0)
    {
        return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "A non-empty JSON object is required.",
            type: "https://errors.dentacore.app/request.invalid_body", extensions: new Dictionary<string, object?> { ["code"] = "request.invalid_body" });
    }

    var result = await sender.Send(new RescheduleAppointmentCommand(id, version, fields), ct);
    if (result.IsSuccess)
    {
        http.Response.Headers.ETag = $"\"{result.Value.RowVersion}\"";
    }

    return result.ToHttpResult(Results.Ok);
});

appointments.MapPost("/{id:guid}/check-in", async (Guid id, ISender sender, CancellationToken ct) =>
    (await sender.Send(new CheckInCommand(id), ct)).ToHttpResult(Results.Ok));

appointments.MapPost("/{id:guid}/cancel", async (Guid id, CancelRequest? body, ISender sender, CancellationToken ct) =>
    (await sender.Send(new CancelAppointmentCommand(id, body?.Reason), ct)).ToHttpResult(Results.Ok));

appointments.MapPost("/{id:guid}/no-show", async (Guid id, ISender sender, CancellationToken ct) =>
    (await sender.Send(new MarkNoShowCommand(id), ct)).ToHttpResult(Results.Ok));

app.MapGet("/v1/queue", async (Guid branchId, ISender sender, CancellationToken ct) =>
    (await sender.Send(new GetQueueQuery(branchId), ct)).ToHttpResult(Results.Ok)).RequireAuthorization();

app.MapPost("/v1/queue/{ticketId:guid}/call", async (Guid ticketId, ISender sender, CancellationToken ct) =>
    (await sender.Send(new CallTicketCommand(ticketId), ct)).ToHttpResult(Results.Ok)).RequireAuthorization();

app.MapPost("/v1/waitlist", async (WaitlistRequest body, ISender sender, CancellationToken ct) =>
{
    var result = await sender.Send(new AddToWaitlistCommand(body.PatientId, body.BranchId, body.ProviderId, body.Earliest, body.Latest, body.Priority ?? 5), ct);
    return result.ToHttpResult(w => Results.Created($"/v1/waitlist/{w.Id}", w));
}).RequireAuthorization();

await app.RunAsync();

// If-Match: "3" (ETag) və ya 3
static bool TryParseIfMatch(Microsoft.Extensions.Primitives.StringValues header, out int version)
{
    version = 0;
    return header.Count == 1 && int.TryParse(header[0]?.Trim('"'), NumberStyles.None, CultureInfo.InvariantCulture, out version);
}

internal sealed record RegisterPatientRequest(
    Guid BranchId,
    string FirstName,
    string LastName,
    string? FatherName,
    DateOnly? BirthDate,
    string? Gender,
    string? Phone,
    string? Email,
    string? NationalId,
    JsonElement? Address,
    string? PreferredChannel,
    bool? MarketingOptIn,
    string? ReferralSource);

internal sealed record AllergyRequest(string Substance, string? Reaction, string? Severity);

internal sealed record BookAppointmentRequest(
    Guid BranchId, Guid PatientId, Guid ProviderId, Guid? RoomId, DateTimeOffset Start, DateTimeOffset End, string? Reason, string? Source, int[]? ReminderMinutes);

internal sealed record CancelRequest(string? Reason);

internal sealed record WaitlistRequest(Guid PatientId, Guid BranchId, Guid? ProviderId, DateTimeOffset? Earliest, DateTimeOffset? Latest, int? Priority);

#pragma warning disable CA1050
/// <summary>WebApplicationFactory testləri üçün.</summary>
public partial class Program;
#pragma warning restore CA1050
