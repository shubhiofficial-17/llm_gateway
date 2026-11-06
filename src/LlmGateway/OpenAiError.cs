namespace LlmGateway;

/// <summary>Errors in OpenAI's shape, so existing client SDKs surface them properly.</summary>
public static class OpenAiError
{
    public static Task WriteAsync(HttpContext ctx, int statusCode, string message, string type, string code)
    {
        ctx.Response.StatusCode = statusCode;
        return ctx.Response.WriteAsJsonAsync(new ErrorEnvelope(new ErrorBody(message, type, code)));
    }

    public sealed record ErrorEnvelope(ErrorBody Error);

    public sealed record ErrorBody(string Message, string Type, string Code);
}
