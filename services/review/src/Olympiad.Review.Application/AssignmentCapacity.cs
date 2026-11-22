using Olympiad.Review.Domain;

namespace Olympiad.Review.Application;

internal static class AssignmentCapacity
{
    private const int Maximum = 2;

    internal static bool IsFull(ICheckRepository checks, string actor, DateTimeOffset now)
        => checks.CountAssigned(actor, now) >= Maximum;

    internal static void Require(ICheckRepository checks, string actor, DateTimeOffset now)
    {
        if (IsFull(checks, actor, now)) throw new DomainFailure("ASSIGNMENT_LIMIT");
    }
}
