using Npgsql;
using SwiftAid.Application.Cases.Commands;

namespace SwiftAid.Infrastructure.Persistence.Processing;

internal static class PostgresCaseCommandErrorMapper
{
    public static bool TryMap(Exception exception, out CasePersistenceRaceException? mapped)
    {
        ArgumentNullException.ThrowIfNull(exception);
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgres)
            {
                if (postgres.SqlState == PostgresErrorCodes.SerializationFailure)
                {
                    mapped = new CasePersistenceRaceException(CasePersistenceRaceKind.SerializationFailure);
                    return true;
                }

                if (postgres.SqlState == PostgresErrorCodes.DeadlockDetected)
                {
                    mapped = new CasePersistenceRaceException(CasePersistenceRaceKind.Deadlock);
                    return true;
                }

                if (postgres.SqlState == PostgresErrorCodes.UniqueViolation &&
                    postgres.ConstraintName is "ux_command_receipts_actor_command" or "ak_command_receipts_actor_command_case")
                {
                    mapped = new CasePersistenceRaceException(CasePersistenceRaceKind.ReceiptKeyConflict);
                    return true;
                }

                break;
            }
        }

        mapped = null;
        return false;
    }
}
