using Google.Protobuf;
using Google.Protobuf.Reflection;
using Sc.Game.Telemetry.V1;

namespace VerseDataGateway.Core;

internal sealed class TelemetryWirePresence
{
    private readonly HashSet<int> fields = [];
    private readonly Dictionary<int, List<TelemetryWirePresence>> messages = [];

    public bool Contains(FieldDescriptor field) => fields.Contains(field.FieldNumber);

    public TelemetryWirePresence? Child(FieldDescriptor field, int index = 0) =>
        messages.TryGetValue(field.FieldNumber, out var children) && index < children.Count
            ? children[index] : null;

    public static TelemetryWirePresence Parse(ReadOnlyMemory<byte> payload)
    {
        var presence = new TelemetryWirePresence();
        presence.Read(payload.ToArray(), TelemetryStreamResponse.Descriptor);
        return presence;
    }

    public static bool IsNumber(FieldDescriptor field) => field.FieldType is
        FieldType.Float or FieldType.Double or FieldType.Int32 or FieldType.UInt32 or
        FieldType.Int64 or FieldType.UInt64 or FieldType.SInt32 or FieldType.SInt64 or
        FieldType.Fixed32 or FieldType.Fixed64 or FieldType.SFixed32 or FieldType.SFixed64;

    private void Read(byte[] payload, MessageDescriptor descriptor)
    {
        using var input = new CodedInputStream(payload);
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            var field = descriptor.FindFieldByNumber(WireFormat.GetTagFieldNumber(tag));
            if (field is null || WireFormat.GetTagWireType(tag) != WireType(field))
            {
                input.SkipLastField();
                continue;
            }
            fields.Add(field.FieldNumber);
            if (field.FieldType != FieldType.Message)
            {
                input.SkipLastField();
                continue;
            }
            if (!messages.TryGetValue(field.FieldNumber, out var children))
                messages[field.FieldNumber] = children = [];
            // Singular submessages can occur more than once on the wire and merge.
            if (field.IsRepeated || children.Count == 0)
                children.Add(new());
            children[^1].Read(input.ReadBytes().ToByteArray(), field.MessageType);
        }
    }

    private static WireFormat.WireType WireType(FieldDescriptor field) => field.FieldType switch
    {
        FieldType.Float or FieldType.Fixed32 or FieldType.SFixed32 => WireFormat.WireType.Fixed32,
        FieldType.Double or FieldType.Fixed64 or FieldType.SFixed64 => WireFormat.WireType.Fixed64,
        FieldType.Message or FieldType.String or FieldType.Bytes => WireFormat.WireType.LengthDelimited,
        _ => WireFormat.WireType.Varint
    };
}
