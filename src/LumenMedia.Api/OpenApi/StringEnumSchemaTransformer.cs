using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace LumenMedia.Api.OpenApi;

/// <summary>
/// ASP.NET OpenAPI defaults enums to integer; the API serializes them as strings
/// (<see cref="System.Text.Json.Serialization.JsonStringEnumConverter"/>). Align the
/// published schema so generated client SDKs get string unions, not numbers.
/// </summary>
internal sealed class StringEnumSchemaTransformer : IOpenApiSchemaTransformer
{
    public Task TransformAsync(
        OpenApiSchema schema,
        OpenApiSchemaTransformerContext context,
        CancellationToken cancellationToken)
    {
        var type = context.JsonTypeInfo.Type;
        var enumType = Nullable.GetUnderlyingType(type) ?? type;
        if (!enumType.IsEnum)
            return Task.CompletedTask;

        schema.Type = JsonSchemaType.String;
        schema.Format = null;
        schema.Enum = Enum.GetNames(enumType)
            .Select(name => (JsonNode)JsonValue.Create(ToWireName(enumType, name))!)
            .ToList();
        return Task.CompletedTask;
    }

    private static string ToWireName(Type enumType, string memberName)
    {
        var field = enumType.GetField(memberName);
        if (field is null)
            return memberName;

        foreach (var attr in field.GetCustomAttributes(false))
        {
            if (attr is System.Text.Json.Serialization.JsonStringEnumMemberNameAttribute jsonName)
                return jsonName.Name;
        }

        return memberName;
    }
}
