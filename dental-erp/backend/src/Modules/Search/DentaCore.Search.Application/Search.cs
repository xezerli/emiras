using DentaCore.BuildingBlocks.Application;
using DentaCore.BuildingBlocks.Domain;
using FluentValidation;

namespace DentaCore.Search.Application;

public static class SearchPermissions
{
    public const string PatientRead = "patient:read";
    public const string AuditRead = "audit:read";
}

public sealed record PatientHit(Guid PatientId, long ChartNo, string FirstName, string LastName, string? FatherName, Guid BranchId, double Score);

public sealed record AuditHit(long AuditId, DateTimeOffset OccurredAt, string Action, string? Entity, Guid? EntityId, Guid? UserId);

public sealed record AuditPage(IReadOnlyList<AuditHit> Items, string? NextCursor);

public sealed record AuditFilter(string? Entity, Guid? EntityId, Guid? UserId, string? Action, DateTimeOffset? From, DateTimeOffset? To);

/// <summary>Axtarış mühərrikinin əlçatmazlığı. Application bunu <see cref="Error.Unavailable"/> kimi qaytarır, 500 kimi yox.</summary>
public sealed class SearchUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

public interface IPatientSearch
{
    /// <summary>Branches null = məhdudiyyətsiz (tenant scope). Boş olmayan siyahı = yalnız bu filiallar.</summary>
    Task<IReadOnlyList<PatientHit>> FindAsync(string query, IReadOnlyCollection<Guid>? branches, int limit, CancellationToken cancellationToken);
}

public interface IAuditSearch
{
    Task<AuditPage> SearchAsync(AuditFilter filter, string? cursor, int limit, CancellationToken cancellationToken);
}

// ---------------- Pasiyent axtarışı ----------------
public sealed record FindPatientsQuery(string Query, Guid? BranchId, int Limit)
    : IQuery<IReadOnlyList<PatientHit>>, IRequiresAccess, IAuditable<IReadOnlyList<PatientHit>>
{
    public string Permission => SearchPermissions.PatientRead;

    // Axtarış mətni ad ola bilər (PHI): auditə yalnız nəticə sayı yazılır
    public AuditDescriptor Describe(IReadOnlyList<PatientHit> response) =>
        new("patient.search", "patient", null, System.Text.Json.JsonSerializer.Serialize(new { results = response?.Count ?? 0, engine = "elasticsearch" }));
}

public sealed class FindPatientsQueryValidator : AbstractValidator<FindPatientsQuery>
{
    internal static bool IsValidQuery(string query)
    {
        var text = query.Trim();
        return text.Length is >= 2 and <= 100 || (text.Length == 1 && char.IsDigit(text[0]));
    }

    public FindPatientsQueryValidator()
    {
        // Kart nömrəsi bir rəqəm ola bilər ("7"); ad axtarışı üçün ən azı 2 simvol (1 hərf bütün pasiyentlərə uyğun gələrdi)
        RuleFor(x => x.Query).NotNull().Must(IsValidQuery)
            .WithMessage("The query must be 2-100 characters (or a single digit for a chart number).");
        RuleFor(x => x.Limit).InclusiveBetween(1, 50);
    }
}

/// <summary>
/// Scope: tenant hamısını (və ya seçilmiş filialı), branch yalnız öz filiallarını axtarır; filtr SORĞUYA yazılır, nəticəni sonradan süzmək yox.
/// own scope axtarış mühərrikində dəstəklənmir (baxan həkim əlaqəsi indeksdə yoxdur): adi pasiyent axtarışından istifadə olunmalıdır.
/// </summary>
internal sealed class FindPatientsQueryHandler(IPatientSearch search, ICurrentUser user) : IRequestHandler<FindPatientsQuery, IReadOnlyList<PatientHit>>
{
    public async Task<Result<IReadOnlyList<PatientHit>>> Handle(FindPatientsQuery request, CancellationToken cancellationToken)
    {
        var scope = user.ScopeOf(SearchPermissions.PatientRead);
        if (scope is null or PermissionScope.Own)
        {
            return Error.Forbidden("search.scope_unsupported", "Full-text patient search needs branch or tenant scope. Use the standard patient search.");
        }

        var allowed = user.BranchFilterFor(SearchPermissions.PatientRead);
        IReadOnlyCollection<Guid>? branches = allowed;
        if (request.BranchId is { } requested)
        {
            if (allowed is not null && !allowed.Contains(requested))
            {
                return Error.NotFound("branch.not_found", "Branch not found.");
            }

            branches = [requested];
        }
        else if (allowed is { Count: 0 })
        {
            return Error.Forbidden("search.no_branches", "Your account has no branches.");
        }

        try
        {
            return Result.Success(await search.FindAsync(request.Query.Trim(), branches, request.Limit, cancellationToken));
        }
        catch (SearchUnavailableException)
        {
            return Error.Unavailable("search.unavailable", "Search is temporarily unavailable. Use the standard patient search.");
        }
    }
}

// ---------------- Audit axtarışı ----------------
public sealed record SearchAuditQuery(string? Entity, Guid? EntityId, Guid? UserId, string? Action, DateTimeOffset? From, DateTimeOffset? To, string? Cursor, int Limit)
    : IQuery<AuditPage>, IRequiresAccess, IAuditable<AuditPage>
{
    public string Permission => SearchPermissions.AuditRead;

    // Audit-i oxumaq özü audit olunur ("kim kimin izinə baxdı"); filtr dəyərləri yox, yalnız sayı
    public AuditDescriptor Describe(AuditPage response) =>
        new("audit.search", "audit", null, System.Text.Json.JsonSerializer.Serialize(new { results = response?.Items.Count ?? 0 }));
}

public sealed class SearchAuditQueryValidator : AbstractValidator<SearchAuditQuery>
{
    public SearchAuditQueryValidator()
    {
        RuleFor(x => x.Limit).InclusiveBetween(1, 100);
        RuleFor(x => x.Entity).MaximumLength(64);
        RuleFor(x => x.Action).MaximumLength(64);
        RuleFor(x => x).Must(x => x.From is null || x.To is null || x.From <= x.To).WithMessage("'from' must not be after 'to'.");
    }
}

/// <summary>Audit axtarışı yalnız tenant scope-lu <c>audit:read</c> ilə: audit bütün tenant-ın izidir, filial üzrə bölünmür.</summary>
internal sealed class SearchAuditQueryHandler(IAuditSearch search, ICurrentUser user) : IRequestHandler<SearchAuditQuery, AuditPage>
{
    public async Task<Result<AuditPage>> Handle(SearchAuditQuery request, CancellationToken cancellationToken)
    {
        if (user.ScopeOf(SearchPermissions.AuditRead) != PermissionScope.Tenant)
        {
            return Error.Forbidden("audit.scope_insufficient", "Audit search needs tenant scope.");
        }

        try
        {
            return await search.SearchAsync(new AuditFilter(request.Entity, request.EntityId, request.UserId, request.Action, request.From, request.To), request.Cursor, request.Limit, cancellationToken);
        }
        catch (SearchUnavailableException)
        {
            return Error.Unavailable("search.unavailable", "Audit search is temporarily unavailable.");
        }
        catch (FormatException)
        {
            return Error.Validation("audit.invalid_cursor", "The cursor is not valid.");
        }
    }
}
