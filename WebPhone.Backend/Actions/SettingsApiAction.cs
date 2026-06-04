using WebPhone.Backend.Storage;
using WebPhone.Contract;

namespace WebPhone.Backend.Actions;

public sealed record GetProfileSettingsInput(string OwnerId);
public sealed record UpsertProfileSettingsInput(string OwnerId, UserSettingsDto Settings);

public sealed class GetProfileSettingsApiAction(ProfileSettingsRepository userSettingsRepository)
    : ApiActionConcrete<GetProfileSettingsInput, UserSettingsDto>
{
    public override string Route => "/profiles:get";

    public override async Task<UserSettingsDto> ExecuteAsync(GetProfileSettingsInput input, CancellationToken cancellationToken = default)
        => await userSettingsRepository.GetAsync(input.OwnerId, cancellationToken);
}

public sealed class UpsertProfileSettingsApiAction(ProfileSettingsRepository userSettingsRepository)
    : ApiActionConcrete<UpsertProfileSettingsInput, bool>
{
    public override string Route => "/profiles:post";

    public override async Task<bool> ExecuteAsync(UpsertProfileSettingsInput input, CancellationToken cancellationToken = default)
    {
        await userSettingsRepository.UpsertAsync(input.OwnerId, input.Settings, cancellationToken);
        return true;
    }
}

public sealed record GetContactSettingsInput(string OwnerId, string? ContactId);
public sealed record UpsertContactSettingsInput(string OwnerId, ContactSettingsDto Settings);

public sealed class GetContactSettingsApiAction(ContactSettingsRepository contactSettingsRepository)
    : ApiActionConcrete<GetContactSettingsInput, object>
{
    public override string Route => "/contacts:get";

    public override async Task<object> ExecuteAsync(GetContactSettingsInput input, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(input.ContactId))
        {
            return await contactSettingsRepository.GetByOwnerAsync(input.OwnerId, cancellationToken);
        }

        return await contactSettingsRepository.GetAsync(input.OwnerId, input.ContactId, cancellationToken);
    }
}

public sealed class UpsertContactSettingsApiAction(ContactSettingsRepository contactSettingsRepository)
    : ApiActionConcrete<UpsertContactSettingsInput, bool>
{
    public override string Route => "/contacts:post";

    public override async Task<bool> ExecuteAsync(UpsertContactSettingsInput input, CancellationToken cancellationToken = default)
    {
        var normalized = input.Settings with { OwnerId = input.OwnerId };
        await contactSettingsRepository.UpsertAsync(normalized, cancellationToken);
        return true;
    }
}
