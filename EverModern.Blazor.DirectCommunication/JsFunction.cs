namespace EverModern.Blazor.DirectCommunication;

public static class JsFunction
{
    public static JsInvokableFunc<TOut> Create<TOut>(Func<TOut> func) => new(func);

    public static JsInvokableFunc<TIn, TOut> Create<TIn, TOut>(Func<TIn, TOut> func) => new(func);

    public static JsInvokableAction<TIn> Create<TIn>(Action<TIn> func) => new(func);
}
