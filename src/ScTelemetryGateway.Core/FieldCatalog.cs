using System.Collections;
using System.Globalization;
using System.Text.Json.Nodes;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Sc.Game.Telemetry.V1;

namespace ScTelemetryGateway.Core;

public sealed record TelemetryField(string Path, IReadOnlyList<FieldDescriptor> Chain, bool Repeated)
{
    public bool SupportsStatistics => !Repeated &&
        Chain[^1].FieldType is FieldType.Float or FieldType.Double or FieldType.Int32 or FieldType.UInt32 &&
        !Path.EndsWith(".heading", StringComparison.Ordinal);
}

public static class FieldCatalog
{
    public static IReadOnlyDictionary<string, TelemetryField> All { get; } = Build();

    private static Dictionary<string, TelemetryField> Build()
    {
        var result = new Dictionary<string, TelemetryField>(StringComparer.Ordinal);
        Add(TelemetryStreamResponse.Descriptor, "", [], false, result);
        return result;
    }

    private static void Add(MessageDescriptor descriptor, string prefix, FieldDescriptor[] chain,
        bool repeated, Dictionary<string, TelemetryField> result)
    {
        foreach (var field in descriptor.Fields.InFieldNumberOrder())
        {
            var path = prefix + field.Name;
            FieldDescriptor[] next = [.. chain, field];
            if (field.FieldType == FieldType.Message)
                Add(field.MessageType, path + ".", next, repeated || field.IsRepeated, result);
            else
                result.Add(path, new(path, next, repeated || field.IsRepeated));
        }
    }

    public static (JsonNode? Value, int Invalid) Read(IMessage message, TelemetryField field) =>
        Read(message, field.Chain, 0);

    private static (JsonNode?, int) Read(IMessage message, IReadOnlyList<FieldDescriptor> chain, int index)
    {
        var field = chain[index];
        if (field.HasPresence && !field.Accessor.HasValue(message))
            return (null, 0);
        var value = field.Accessor.GetValue(message);
        if (field.IsRepeated)
        {
            var array = new JsonArray();
            var invalid = 0;
            foreach (var item in (IEnumerable)value)
            {
                var (node, count) = index + 1 == chain.Count
                    ? Scalar(item, field)
                    : Read((IMessage)item, chain, index + 1);
                array.Add(node);
                invalid += count;
            }
            return (array, invalid);
        }
        return index + 1 == chain.Count
            ? Scalar(value, field)
            : Read((IMessage)value, chain, index + 1);
    }

    private static (JsonNode?, int) Scalar(object value, FieldDescriptor field)
    {
        if (value is float f && (!float.IsFinite(f) || Math.Abs(f) == float.MaxValue) ||
            value is double d && (!double.IsFinite(d) || Math.Abs(d) == float.MaxValue))
            return (null, 1);
        if (field.FieldType == FieldType.Enum)
        {
            var number = Convert.ToInt32(value, CultureInfo.InvariantCulture);
            return (JsonValue.Create(field.EnumType.FindValueByNumber(number)?.Name ?? number.ToString(CultureInfo.InvariantCulture)), 0);
        }
        if (value is long or ulong)
            return (JsonValue.Create(Convert.ToString(value, CultureInfo.InvariantCulture)), 0);
        return (System.Text.Json.JsonSerializer.SerializeToNode(value), 0);
    }
}
