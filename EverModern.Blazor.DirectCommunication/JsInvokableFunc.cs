using Microsoft.JSInterop;

namespace EverModern.Blazor.DirectCommunication;

public class JsInvokableFunc<TIn, TOut>(
    Func<TIn, TOut> func
)
{
    [JSInvokable("invoke")]
    public TOut Invoke(TIn p) => func(p);
}

public class JsInvokableFunc<Tout>(
    Func<Tout> func
)
{
    [JSInvokable("invoke")]
    public Tout Invoke() => func();
}
