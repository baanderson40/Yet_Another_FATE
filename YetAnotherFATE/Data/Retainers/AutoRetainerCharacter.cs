namespace YetAnotherFATE.Data.Retainers;

public sealed record AutoRetainerCharacter(ulong ContentId, string Name, string World);

public enum OceanFishingCharacterMode
{
    CurrentCharacter,
    SelectedCharacter,
}
