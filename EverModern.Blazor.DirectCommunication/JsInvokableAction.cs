using Microsoft.JSInterop;

namespace EverModern.Blazor.DirectCommunication;

public class JsInvokableAction<TIn>(
    Action<TIn> func
)
{
    [JSInvokable("invoke")]
    public void Invoke(TIn p1) => func(p1);
}
