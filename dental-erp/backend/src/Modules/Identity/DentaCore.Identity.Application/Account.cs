using DentaCore.BuildingBlocks.Application;
using DentaCore.BuildingBlocks.Domain;
using DentaCore.Identity.Domain;
using FluentValidation;

namespace DentaCore.Identity.Application;

/// <summary>İstifadəçinin öz parolunu dəyişməsi. Bütün sessiyalar (o cümlədən cari) bağlanır: klient yenidən daxil olmalıdır.</summary>
public sealed record ChangeOwnPasswordCommand(string CurrentPassword, string NewPassword)
    : ICommand<Unit>, IAuditable<Unit>
{
    // Xüsusi icazə yoxdur: hər autentifikasiya olunmuş istifadəçi (endpoint RequireAuthorization ilə qorunur) öz parolunu dəyişə bilər.
    // Aktyor audit qeydinin user_id-sindədir.
    public AuditDescriptor Describe(Unit response) => new("user.password.change", "user", null);
}

public sealed class ChangeOwnPasswordCommandValidator : AbstractValidator<ChangeOwnPasswordCommand>
{
    public ChangeOwnPasswordCommandValidator()
    {
        RuleFor(x => x.CurrentPassword).NotEmpty().MaximumLength(256);
        RuleFor(x => x.NewPassword).MeetsPolicy();
        RuleFor(x => x.NewPassword).NotEqual(x => x.CurrentPassword).WithMessage("New password must differ from the current one.");
    }
}

internal sealed class ChangeOwnPasswordCommandHandler(
    IUserRepository users,
    IPasswordHasher hasher,
    IRefreshTokenRepository refreshTokens,
    ICurrentUser current,
    IIdentityUnitOfWork unitOfWork,
    IClock clock) : IRequestHandler<ChangeOwnPasswordCommand, Unit>
{
    public async Task<Result<Unit>> Handle(ChangeOwnPasswordCommand request, CancellationToken cancellationToken)
    {
        if (!current.IsAuthenticated)
        {
            return Error.Unauthorized("auth.required", "Authentication is required.");
        }

        var now = clock.UtcNow;
        var user = await users.GetAsync(current.UserId, cancellationToken);
        if (user is null || user.Status == UserStatus.Disabled)
        {
            return Error.NotFound("user.not_found", "User not found.");
        }

        var canTry = user.CanAttemptSignIn(now);
        if (canTry.IsFailure)
        {
            return canTry.Error!;
        }

        if (!hasher.Verify(request.CurrentPassword, user.PasswordHash))
        {
            // Oğurlanmış access token ilə cari parolu təxmin etmək giriş kimi sayılır: eyni bloklama siyasəti
            user.RegisterFailedLogin(now, LockoutPolicy.Default);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            // 401 deyil: klient 401-də token yeniləməyə çalışır, burada isə token düzgündür
            return Error.Validation("auth.current_password_incorrect", "Current password is incorrect.");
        }

        user.ChangePassword(hasher.Hash(request.NewPassword));

        await using (var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken))
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
            await refreshTokens.RevokeAllForUserAsync(user.Id, now, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        return new Unit();
    }
}
