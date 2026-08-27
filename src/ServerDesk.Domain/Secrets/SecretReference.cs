namespace ServerDesk.Domain.Secrets;

public readonly record struct SecretReference
{
    private const string Prefix = "serverdesk:";

    private SecretReference(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static SecretReference Create(string purpose)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);

        var normalizedPurpose = purpose.Trim().ToLowerInvariant();
        if (normalizedPurpose.Any(character =>
                !char.IsLetterOrDigit(character) && character is not '-' and not '_' and not '.'))
        {
            throw new ArgumentException(
                "Secret reference purpose may contain only letters, digits, '-', '_' and '.'.",
                nameof(purpose));
        }

        return new SecretReference($"{Prefix}{normalizedPurpose}:{Guid.NewGuid():N}");
    }

    public static SecretReference ForServerProfile(Guid profileId)
    {
        if (profileId == Guid.Empty)
        {
            throw new ArgumentException("Server profile id cannot be empty.", nameof(profileId));
        }

        return new SecretReference($"{Prefix}ssh-profile:{profileId:N}");
    }

    public static SecretReference ForServerProxy(Guid profileId)
    {
        if (profileId == Guid.Empty)
        {
            throw new ArgumentException("Server profile id cannot be empty.", nameof(profileId));
        }

        return new SecretReference($"{Prefix}ssh-proxy:{profileId:N}");
    }

    public static SecretReference Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        var normalized = value.Trim();
        if (!normalized.StartsWith(Prefix, StringComparison.Ordinal) || normalized.Length > 256)
        {
            throw new FormatException("Secret reference is not a valid ServerDesk reference.");
        }

        return new SecretReference(normalized);
    }

    public override string ToString() => "[secret-reference]";
}
