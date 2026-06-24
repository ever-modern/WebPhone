namespace WebPhone.Data;

public interface ILocalStore
{
    Task<T> GetAsync<T>(string key, CancellationToken cancellationToken = default);

    Task SetAsync<T>(string key, T value, CancellationToken cancellationToken = default);
}
