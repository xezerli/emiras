using DentaCore.BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DentaCore.BuildingBlocks.Infrastructure.Outbox;

public static class PostgresErrors
{
    /// <summary>PostgreSQL constraint xətasını Application qatının başa düşdüyü tipə çevirir. Başqa xətalar üçün null.</summary>
    public static ConstraintViolationException? Translate(DbUpdateException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception.InnerException is not PostgresException pg)
        {
            return null;
        }

        return pg.SqlState switch
        {
            PostgresErrorCodes.ExclusionViolation => new ConstraintViolationException(ConstraintKind.Exclusion, pg.ConstraintName, exception),
            PostgresErrorCodes.UniqueViolation => new ConstraintViolationException(ConstraintKind.Unique, pg.ConstraintName, exception),
            PostgresErrorCodes.ForeignKeyViolation => new ConstraintViolationException(ConstraintKind.ForeignKey, pg.ConstraintName, exception),
            _ => null,
        };
    }
}
