using Microsoft.EntityFrameworkCore;

namespace DentaCore.BuildingBlocks.Infrastructure.Messaging;

public static class InboxExtensions
{
    /// <summary>
    /// "Bir dəfəlik təsir": inbox sətri və consumer-in dəyişiklikləri EYNİ tranzaksiyada saxlanılır.
    /// Mesaj artıq işlənibsə (inbox-da var) action çağırılmır və false qaytarılır. Proses ortada ölərsə hər ikisi geri qaytarılır
    /// və təkrar çatdırılmada mesaj yenidən işlənir, yarımçıq vəziyyət qalmır.
    /// </summary>
    public static async Task<bool> ExecuteOnceAsync(this DbContext db, Guid messageId, string consumer, Func<CancellationToken, Task> action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(action);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var inserted = await db.Database.ExecuteSqlAsync(
            $"INSERT INTO inbox_messages(message_id, consumer) VALUES ({messageId}, {consumer}) ON CONFLICT DO NOTHING",
            cancellationToken);
        if (inserted == 0)
        {
            return false;
        }

        await action(cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }
}
