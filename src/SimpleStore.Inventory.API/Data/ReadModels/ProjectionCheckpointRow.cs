namespace SimpleStore.Inventory.API.Data.ReadModels;

// Bookmark for the inventory projector. KurrentDB's $all subscription resumes
// from a (commit, prepare) position pair — both 64-bit unsigned ints, stored
// here as signed bigints since Postgres has no native ulong.
public class ProjectionCheckpointRow
{
    public string ProjectionName { get; set; } = string.Empty;
    public long CommitPosition { get; set; }
    public long PreparePosition { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
