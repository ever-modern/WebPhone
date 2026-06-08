namespace WebPhone.Backend;

public abstract class ApiAction
{
    public abstract string Route { get; }
}

public abstract class ApiActionConcrete<TIn, TOut> : ApiAction
{
    public abstract Task<TOut> ExecuteAsync(TIn input, CancellationToken cancellationToken = default);
}
