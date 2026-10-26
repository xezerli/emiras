using DentaCore.Billing.Domain;
using DentaCore.BuildingBlocks.Application;
using DentaCore.BuildingBlocks.Domain;
using FluentValidation;
using Microsoft.Extensions.Options;

namespace DentaCore.Billing.Application;

public sealed record ServiceDto(Guid Id, string Code, string? ProcedureCode, string Name, string? Category, decimal Price, string Currency, decimal VatRate, bool IsActive, int RowVersion);

internal static class ServiceMapper
{
    public static ServiceDto ToDto(Service s) => new(s.Id, s.Code, s.ProcedureCode, s.Name, s.Category, s.Price, s.Currency, s.VatRate, s.IsActive, s.RowVersion);
}

public sealed record ListServicesQuery(string? Query, bool IncludeInactive) : IQuery<IReadOnlyList<ServiceDto>>, IRequiresAccess
{
    public string Permission => BillingPermissions.InvoiceRead;
}

internal sealed class ListServicesQueryHandler(IBillingRepository repository) : IRequestHandler<ListServicesQuery, IReadOnlyList<ServiceDto>>
{
    public async Task<Result<IReadOnlyList<ServiceDto>>> Handle(ListServicesQuery request, CancellationToken cancellationToken)
    {
        var services = await repository.ListServicesAsync(request.Query, request.IncludeInactive, cancellationToken);
        return services.Select(ServiceMapper.ToDto).ToList();
    }
}

public sealed record CreateServiceCommand(string Code, string? ProcedureCode, string Name, string? Category, decimal Price, string? Currency, decimal VatRate)
    : ICommand<ServiceDto>, IRequiresAccess, IAuditable<ServiceDto>
{
    public string Permission => BillingPermissions.Settings;

    public AuditDescriptor Describe(ServiceDto response) => new("service.create", "service", response?.Id);
}

public sealed class CreateServiceCommandValidator : AbstractValidator<CreateServiceCommand>
{
    public CreateServiceCommandValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(40);
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Price).GreaterThanOrEqualTo(0);
    }
}

internal sealed class CreateServiceCommandHandler(IBillingRepository repository, IBillingUnitOfWork unitOfWork, IOptions<BillingOptions> options)
    : IRequestHandler<CreateServiceCommand, ServiceDto>
{
    public async Task<Result<ServiceDto>> Handle(CreateServiceCommand request, CancellationToken cancellationToken)
    {
        var service = Service.Create(
            Guid.NewGuid(), request.Code, request.ProcedureCode, request.Name, request.Category, request.Price,
            request.Currency ?? options.Value.DefaultCurrency, request.VatRate);
        if (service.IsFailure)
        {
            return service.Error!;
        }

        repository.Add(service.Value);
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (ConstraintViolationException ex) when (ex.Kind == ConstraintKind.Unique)
        {
            unitOfWork.DiscardChanges();
            return Error.Conflict("service.code_taken", "A service with this code already exists.");
        }
        catch (ConstraintViolationException ex) when (ex.Kind == ConstraintKind.ForeignKey)
        {
            unitOfWork.DiscardChanges();
            return Error.Validation("service.unknown_procedure", "The procedure code does not exist.");
        }

        return ServiceMapper.ToDto(service.Value);
    }
}

public sealed record UpdateServiceCommand(Guid Id, int ExpectedVersion, string Name, string? Category, decimal Price, decimal VatRate, bool IsActive)
    : ICommand<ServiceDto>, IRequiresAccess, IAuditable<ServiceDto>
{
    public string Permission => BillingPermissions.Settings;

    public AuditDescriptor Describe(ServiceDto response) =>
        new("service.update", "service", Id, BillingText.ToJson(new { price = response?.Price, isActive = response?.IsActive }));
}

public sealed class UpdateServiceCommandValidator : AbstractValidator<UpdateServiceCommand>
{
    public UpdateServiceCommandValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Price).GreaterThanOrEqualTo(0);
    }
}

internal sealed class UpdateServiceCommandHandler(IBillingRepository repository, IBillingUnitOfWork unitOfWork) : IRequestHandler<UpdateServiceCommand, ServiceDto>
{
    public async Task<Result<ServiceDto>> Handle(UpdateServiceCommand request, CancellationToken cancellationToken)
    {
        var service = await repository.GetServiceAsync(request.Id, cancellationToken);
        if (service is null)
        {
            return Error.NotFound("service.not_found", "Service not found.");
        }

        if (service.RowVersion != request.ExpectedVersion)
        {
            return Error.PreconditionFailed("precondition.failed", "The service was changed by someone else. Reload and retry.");
        }

        var updated = service.Update(request.Name, request.Category, request.Price, request.VatRate, request.IsActive);
        if (updated.IsFailure)
        {
            return updated.Error!;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);   // yeni row_version DTO-ya düşsün
        return ServiceMapper.ToDto(service);
    }
}
