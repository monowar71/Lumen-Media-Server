using LumenMedia.Domain.Enums;

namespace LumenMedia.Domain.Media;

/// <summary>Credit linking a <see cref="Person"/> to a <see cref="MediaItem"/> with a role.</summary>
public class MediaPerson
{
    private MediaPerson() { }

    public MediaPerson(Guid mediaItemId, Guid personId, PersonType type, string? role = null, int sortOrder = 0)
    {
        MediaItemId = mediaItemId;
        PersonId = personId;
        Type = type;
        Role = role;
        SortOrder = sortOrder;
    }

    public Guid MediaItemId { get; private set; }
    public Guid PersonId { get; private set; }
    public PersonType Type { get; private set; }
    public string? Role { get; private set; }
    public int SortOrder { get; private set; }
    public Person Person { get; private set; } = null!;
}
