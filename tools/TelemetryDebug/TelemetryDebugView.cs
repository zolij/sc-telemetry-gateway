using System.Collections;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Sc.Game.Telemetry.V1;
using VerseDataGateway.Core;

namespace VerseDataGateway.TelemetryDebug;

internal sealed class TelemetryDebugView
{
    private readonly object gate = new();
    private readonly Dictionary<string, (DateTimeOffset ReceivedAt, string Value)> sections = [];
    private string status = "Connecting";

    public void SetStatus(string value)
    {
        lock (gate) status = value;
    }

    public void Clear()
    {
        lock (gate) sections.Clear();
    }

    public void Ingest(TelemetryStreamResponse frame, ReadOnlyMemory<byte> payload)
    {
        var presence = TelemetryWirePresence.Parse(payload);
        var receivedAt = DateTimeOffset.UtcNow;
        lock (gate)
        {
            foreach (var field in TelemetryStreamResponse.Descriptor.Fields.InFieldNumberOrder())
            {
                if (!presence.Contains(field)) continue;
                var message = (IMessage)field.Accessor.GetValue(frame);
                var output = new StringBuilder();
                AppendMessage(output, message, presence.Child(field), "");
                sections[field.Name] = (receivedAt, output.ToString());
            }
        }
    }

    public string Render(DateTimeOffset now, string? sectionFilter = null)
    {
        lock (gate)
        {
            var output = new StringBuilder();
            output.AppendLine("GAME TELEMETRY / raw protobuf values / Ctrl+C to exit");
            output.AppendLine($"Status: {status} | UTC: {now:HH:mm:ss.fff}");
            foreach (var field in TelemetryStreamResponse.Descriptor.Fields.InFieldNumberOrder())
            {
                if (sectionFilter is not null && sectionFilter != field.Name) continue;
                if (!sections.TryGetValue(field.Name, out var section))
                {
                    output.AppendLine($"\n[{field.Name}] waiting");
                    continue;
                }
                var age = Math.Max(0, (now - section.ReceivedAt).TotalSeconds);
                output.AppendLine($"\n[{field.Name}] received {age:F1}s ago");
                output.Append(section.Value);
            }
            return output.ToString();
        }
    }

    private static void AppendMessage(StringBuilder output, IMessage message, TelemetryWirePresence? presence, string prefix)
    {
        var values = new List<string>();
        var children = new StringBuilder();
        foreach (var field in message.Descriptor.Fields.InFieldNumberOrder())
        {
            var path = prefix + field.Name;
            var value = field.Accessor.GetValue(message);
            if (field.IsRepeated)
            {
                var items = (IList)value;
                if (field.FieldType != FieldType.Message)
                    values.Add($"{field.Name}=[{string.Join(", ", items.Cast<object>().Select(item => FormatScalar(field, item)))}]");
                else if (items.Count == 0)
                    values.Add($"{field.Name}=[]");
                else
                    for (var index = 0; index < items.Count; index++)
                        AppendMessage(children, (IMessage)items[index]!, presence?.Child(field, index), $"{path}[{index}].");
            }
            else if (presence?.Contains(field) != true)
                values.Add($"{field.Name}=<omitted>");
            else if (field.FieldType == FieldType.Message)
                AppendMessage(children, (IMessage)value, presence.Child(field), path + ".");
            else
                values.Add($"{field.Name}={FormatScalar(field, value)}");
        }
        if (values.Count > 0) output.AppendLine($"{(prefix.Length == 0 ? "(root)" : prefix[..^1])}: {string.Join(" | ", values)}");
        output.Append(children);
    }

    private static string FormatScalar(FieldDescriptor field, object value) => field.FieldType switch
    {
        FieldType.Enum => FormatEnum(field, value),
        FieldType.String => JsonSerializer.Serialize((string)value),
        FieldType.Bool => (bool)value ? "true" : "false",
        FieldType.Float => ((float)value).ToString("R", CultureInfo.InvariantCulture),
        FieldType.Double => ((double)value).ToString("R", CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? throw new InvalidOperationException("Null scalar value.")
    };

    private static string FormatEnum(FieldDescriptor field, object value)
    {
        var number = Convert.ToInt32(value, CultureInfo.InvariantCulture);
        var name = field.EnumType.FindValueByNumber(number)?.Name;
        return name is null ? number.ToString(CultureInfo.InvariantCulture) : $"{name} ({number})";
    }
}
