using System.Security.Cryptography;
using System.Text.Json;
using Deviant.Assets;
using Deviant.Core;

namespace Deviant.Browser;

/// <summary>Loads small, explicitly qualified type-evidence datasets.</summary>
public static class TypeRegistryDatasetReader
{
    public const string CheckpointFormat = "deviant-type-evidence-checkpoint-v1";
    private const int MaximumRevisionComponentLength = 4096;
    private const string ReservationCategory = "type registry dataset input and metadata";

    public static async Task<TypeRegistryDatasetLease> LoadAsync(Stream input,
        TypeRegistryDatasetExpectation expectation, AssetMemoryBudget budget,
        TypeRegistryDatasetLimits? limits = null, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(expectation);
        ArgumentNullException.ThrowIfNull(budget);
        if (!input.CanRead) throw new ArgumentException("The dataset stream must be readable.", nameof(input));
        ValidateExpectation(expectation);
        limits ??= new();
        limits.Validate();
        token.ThrowIfCancellationRequested();

        // Account for the bounded raw buffer, the JSON parser's retained copy, and all
        // materialized definitions before any input is parsed.
        var peakBytes = checked((long)limits.MaximumRawBytes + 1 + limits.MaximumRawBytes +
            2 * limits.MaximumMetadataBytes);
        AssetMemoryBudget.Reservation? reservation = null;
        byte[]? raw = null;
        var transferred = false;
        try
        {
            reservation = budget.Reserve(peakBytes, ReservationCategory);
            raw = new byte[checked(limits.MaximumRawBytes + 1)];
            var length = 0;
            while (length < raw.Length)
            {
                token.ThrowIfCancellationRequested();
                var read = await input.ReadAsync(raw.AsMemory(length), token).ConfigureAwait(false);
                if (read == 0) break;
                length += read;
            }
            if (length > limits.MaximumRawBytes)
                throw new InvalidDataException("The type-evidence dataset exceeds the raw input limit.");
            token.ThrowIfCancellationRequested();

            var datasetSha256 = Convert.ToHexString(SHA256.HashData(raw.AsSpan(0, length))).ToLowerInvariant();
            if (expectation.ExpectedDatasetSha256 is { } expectedDigest &&
                !FixedHashEquals(datasetSha256, expectedDigest))
                throw new InvalidDataException("The type-evidence dataset digest does not match the expected digest.");

            var retainedMetadataBytes = PreflightJson(raw.AsSpan(0, length), limits, token);
            token.ThrowIfCancellationRequested();
            var first = 0;
            while (first < length && (raw[first] == (byte)' ' || raw[first] == (byte)'\t' ||
                raw[first] == (byte)'\r' || raw[first] == (byte)'\n')) first++;
            if (first < length && raw[first] == (byte)'[' && expectation.ExpectedDatasetSha256 is null)
                throw new InvalidDataException("Legacy definition arrays require an expected dataset digest.");
            TypeDefinition[] definitions;
            using (var document = JsonDocument.Parse(raw.AsMemory(0, length), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = limits.MaximumDepth
            }))
            {
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Array)
                {
                    if (expectation.ExpectedDatasetSha256 is null)
                        throw new InvalidDataException("Legacy definition arrays require an expected dataset digest.");
                    definitions = ReadDefinitions(root, limits, token);
                }
                else if (root.ValueKind == JsonValueKind.Object)
                {
                    if (!StringEquals(RequiredString(root, "Format"), CheckpointFormat))
                        throw new InvalidDataException("The checkpoint format is not supported.");
                    var checkpointRevision = ReadRevision(Required(root, "SourceRevision"));
                    var checkpointIndexContentId = RequiredString(root, "IndexContentId");
                    var checkpointExecutable = ValidateHash(RequiredString(root, "ExecutableSha256"), "checkpoint executable SHA-256");
                    if (!StringEquals(checkpointIndexContentId, expectation.CurrentRevision.IndexContentId) ||
                        checkpointRevision != expectation.CurrentRevision ||
                        !FixedHashEquals(checkpointExecutable, expectation.SelectedExecutableSha256))
                        throw new InvalidDataException("The type-evidence checkpoint does not match the selected source revision and executable.");
                    definitions = ReadDefinitions(Required(root, "Definitions"), limits, token);
                }
                else throw new InvalidDataException("The type-evidence dataset root must be a checkpoint object or legacy definition array.");
            }

            token.ThrowIfCancellationRequested();
            var registry = new TypeRegistry(definitions);
            var definitionCount = definitions.Length;
            Array.Clear(definitions, 0, definitions.Length);
            definitions = [];
            token.ThrowIfCancellationRequested();
            // Parsing scratch and intermediate definitions are no longer retained by the lease.
            Array.Clear(raw, 0, raw.Length);
            raw = null;
            reservation.ReduceTo(retainedMetadataBytes);
            var lease = new TypeRegistryDatasetLease(expectation.CurrentRevision,
                expectation.SelectedExecutableSha256.ToLowerInvariant(), datasetSha256,
                definitionCount, registry, reservation);
            reservation = null;
            transferred = true;
            return lease;
        }
        finally
        {
            if (raw is not null) Array.Clear(raw, 0, raw.Length);
            if (!transferred) reservation?.Dispose();
        }
    }

    private static void ValidateExpectation(TypeRegistryDatasetExpectation expectation)
    {
        ArgumentNullException.ThrowIfNull(expectation.CurrentRevision);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectation.CurrentRevision.IndexContentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectation.CurrentRevision.PhysicalSourceStamp);
        if (expectation.CurrentRevision.IndexContentId.Length > MaximumRevisionComponentLength)
            throw new ArgumentOutOfRangeException(nameof(expectation), "The index content id exceeds the revision length limit.");
        if (expectation.CurrentRevision.PhysicalSourceStamp.Length > MaximumRevisionComponentLength)
            throw new ArgumentOutOfRangeException(nameof(expectation), "The physical source stamp exceeds the revision length limit.");
        _ = ValidateHash(expectation.SelectedExecutableSha256, "selected executable SHA-256");
        if (expectation.ExpectedDatasetSha256 is { } digest) _ = ValidateHash(digest, "expected dataset SHA-256");
    }

    private static string ValidateHash(string value, string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException($"The {label} must contain 64 hexadecimal characters.", nameof(value));
        return value.ToLowerInvariant();
    }

    private static bool FixedHashEquals(string left, string right)
    {
        var leftBytes = Convert.FromHexString(left);
        var rightBytes = Convert.FromHexString(right);
        return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static bool StringEquals(string left, string right) =>
        string.Equals(left, right, StringComparison.Ordinal);

    private static long PreflightJson(ReadOnlySpan<byte> bytes, TypeRegistryDatasetLimits limits,
        CancellationToken token)
    {
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = limits.MaximumDepth });
        var propertyNames = new Dictionary<int, string>();
        var objectKinds = new Dictionary<int, SchemaObjectKind>();
        var arrays = new Stack<ArrayFrame>();
        var tokenCount = 0;
        var definitionCount = 0;
        var fieldAndEvidenceCount = 0;
        long stringBytes = 0;
        while (reader.Read())
        {
            if ((++tokenCount & 0x3ff) == 0) token.ThrowIfCancellationRequested();
            if (tokenCount > limits.MaximumJsonTokens)
                throw new InvalidDataException("The type-evidence dataset exceeds the JSON token limit.");
            if (reader.CurrentDepth > limits.MaximumDepth)
                throw new InvalidDataException("The type-evidence dataset exceeds the JSON nesting limit.");
            if (reader.TokenType is JsonTokenType.String or JsonTokenType.PropertyName)
            {
                if (reader.ValueSpan.Length > limits.MaximumStringBytes)
                    throw new InvalidDataException("A type-evidence JSON string exceeds the string limit.");
                stringBytes = checked(stringBytes + reader.ValueSpan.Length);
            }

            if (reader.TokenType == JsonTokenType.PropertyName)
            {
                propertyNames[reader.CurrentDepth] = reader.GetString()!;
                continue;
            }

            if (reader.TokenType == JsonTokenType.EndArray)
            {
                if (arrays.Count == 0 || arrays.Peek().Depth != reader.CurrentDepth)
                    throw new InvalidDataException("The type-evidence dataset contains an invalid array structure.");
                arrays.Pop();
                RemoveDeeperProperties(propertyNames, reader.CurrentDepth);
                continue;
            }

            if (reader.TokenType == JsonTokenType.EndObject)
            {
                objectKinds.Remove(reader.CurrentDepth);
                RemoveDeeperProperties(propertyNames, reader.CurrentDepth);
                continue;
            }

            if (reader.TokenType == JsonTokenType.StartObject)
            {
                RecordArrayItem(arrays, reader.CurrentDepth, limits, ref definitionCount, ref fieldAndEvidenceCount);
                objectKinds[reader.CurrentDepth] = GetObjectKind(reader.CurrentDepth, propertyNames,
                    objectKinds, arrays);
                propertyNames.Remove(reader.CurrentDepth);
                continue;
            }

            if (reader.TokenType == JsonTokenType.StartArray)
            {
                RecordArrayItem(arrays, reader.CurrentDepth, limits, ref definitionCount, ref fieldAndEvidenceCount);
                var name = propertyNames.TryGetValue(reader.CurrentDepth, out var arrayName) ? arrayName : "";
                var role = GetArrayRole(reader.CurrentDepth, name, objectKinds);
                arrays.Push(new(reader.CurrentDepth, role));
                propertyNames.Remove(reader.CurrentDepth);
                continue;
            }

            ValidateSchemaEnum(reader, propertyNames, objectKinds);
            propertyNames.Remove(reader.CurrentDepth);
            RecordArrayItem(arrays, reader.CurrentDepth, limits, ref definitionCount, ref fieldAndEvidenceCount);
        }
        token.ThrowIfCancellationRequested();
        var estimatedMetadataBytes = checked(4096L + (long)tokenCount * 128 + stringBytes * 2);
        if (estimatedMetadataBytes > limits.MaximumMetadataBytes)
            throw new InvalidDataException("The type-evidence metadata exceeds the materialized metadata limit.");
        return estimatedMetadataBytes;
    }

    private static TypeDefinition[] ReadDefinitions(JsonElement element, TypeRegistryDatasetLimits limits,
        CancellationToken token)
    {
        if (element.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Definitions must be an array.");
        var definitions = new List<TypeDefinition>();
        foreach (var item in element.EnumerateArray())
        {
            token.ThrowIfCancellationRequested();
            if (definitions.Count >= limits.MaximumDefinitions)
                throw new InvalidDataException("The type-evidence definition count exceeds the limit.");
            definitions.Add(ReadDefinition(item, token));
        }
        return definitions.ToArray();
    }

    private static TypeDefinition ReadDefinition(JsonElement element, CancellationToken token)
    {
        EnsureObject(element, "definition");
        var key = Required(element, "Type");
        EnsureObject(key, "type key");
        var domain = ParseDomain(RequiredString(key, "Domain"));
        var id = OptionalUInt32(key, "Id");
        var tag = OptionalString(key, "Tag");
        var version = OptionalUInt32(key, "Version");
        if ((domain is TypeDomain.Archive or TypeDomain.Native) && id is null)
            throw new InvalidDataException("Archive and Native type keys require an unsigned 32-bit id.");
        if (domain == TypeDomain.SerializedClass && (id is not null || string.IsNullOrWhiteSpace(tag)))
            throw new InvalidDataException("SerializedClass type keys require a tag and cannot use an ordinal id.");
        var type = new TypeKey(domain, id, tag, version);

        var supportElement = Optional(element, "Support");
        if (supportElement.HasValue && supportElement.Value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null))
            throw new InvalidDataException("Support must be an object or null.");
        var support = supportElement.HasValue && supportElement.Value.ValueKind == JsonValueKind.Object
            ? new TypeSupport(OptionalString(supportElement.Value, "Structure") ?? "Unknown",
                OptionalString(supportElement.Value, "Properties") ?? "Unknown",
                OptionalString(supportElement.Value, "References") ?? "Unknown",
                OptionalString(supportElement.Value, "Placement") ?? "Unknown",
                OptionalString(supportElement.Value, "Visualization") ?? "None",
                OptionalString(supportElement.Value, "Export") ?? "Original")
            : new TypeSupport();

        var evidence = ReadArray(element, "Evidence", item =>
        {
            EnsureObject(item, "type evidence");
            var levelText = RequiredString(item, "Level");
            if (!Enum.TryParse<EvidenceLevel>(levelText, ignoreCase: false, out var level) || !Enum.IsDefined(level))
                throw new InvalidDataException("A type evidence level is not recognized.");
            var rva = OptionalInt64(item, "FunctionRva");
            if (rva is < 0) throw new InvalidDataException("A function RVA cannot be negative.");
            return new TypeEvidence(RequiredString(item, "Source"), RequiredString(item, "Statement"), level,
                OptionalString(item, "ExecutableSha256"), rva);
        }, token);

        var fields = ReadArray(element, "Fields", item =>
        {
            EnsureObject(item, "type field");
            var levelText = RequiredString(item, "Evidence");
            if (!Enum.TryParse<EvidenceLevel>(levelText, ignoreCase: false, out var level) || !Enum.IsDefined(level))
                throw new InvalidDataException("A type field evidence level is not recognized.");
            return new TypeFieldSchema(RequiredString(item, "Name"), RequiredString(item, "DataType"),
                RequiredString(item, "Offset"), OptionalString(item, "Units"), level,
                OptionalString(item, "Meaning"));
        }, token);

        var questions = ReadStringArray(element, "Questions", token);
        var names = ReadStringArray(element, "CompetingNames", token);
        var tags = ReadStringArray(element, "Tags", token);
        return new(type, RequiredString(element, "Name"), RequiredString(element, "Category"),
            evidence, fields, support, questions, OptionalBoolean(element, "Registered"), names, tags);
    }

    private static IReadOnlyList<T> ReadArray<T>(JsonElement owner, string property,
        Func<JsonElement, T> read, CancellationToken token)
    {
        var value = Optional(owner, property);
        if (value is null) return [];
        if (value.Value.ValueKind != JsonValueKind.Array) throw new InvalidDataException($"{property} must be an array.");
        var items = new List<T>();
        foreach (var item in value.Value.EnumerateArray())
        {
            token.ThrowIfCancellationRequested();
            items.Add(read(item));
        }
        return items;
    }

    private static void RecordArrayItem(Stack<ArrayFrame> arrays, int tokenDepth,
        TypeRegistryDatasetLimits limits, ref int definitionCount, ref int fieldAndEvidenceCount)
    {
        if (!arrays.TryPeek(out var parent) || tokenDepth != parent.Depth + 1) return;
        var count = parent.Count + 1;
        if (count > limits.MaximumArrayItems)
            throw new InvalidDataException("A type-evidence array exceeds the item limit.");
        arrays.Pop(); arrays.Push(parent with { Count = count });
        if (parent.Role == ArrayRole.Definitions)
        {
            if (++definitionCount > limits.MaximumDefinitions)
                throw new InvalidDataException("The type-evidence definition count exceeds the limit.");
        }
        if (parent.Role is ArrayRole.Fields or ArrayRole.Evidence)
        {
            if (++fieldAndEvidenceCount > limits.MaximumFieldsAndEvidence)
                throw new InvalidDataException("The combined type-field and evidence count exceeds the limit.");
        }
    }

    private static SchemaObjectKind GetObjectKind(int depth, IReadOnlyDictionary<int, string> propertyNames,
        IReadOnlyDictionary<int, SchemaObjectKind> objectKinds, Stack<ArrayFrame> arrays)
    {
        if (depth == 0) return SchemaObjectKind.Root;
        if (arrays.TryPeek(out var array) && depth == array.Depth + 1)
            return array.Role switch
            {
                ArrayRole.Definitions => SchemaObjectKind.Definition,
                ArrayRole.Evidence => SchemaObjectKind.EvidenceItem,
                ArrayRole.Fields => SchemaObjectKind.Field,
                _ => SchemaObjectKind.Unknown
            };
        var parentDepth = depth - 1;
        if (objectKinds.TryGetValue(parentDepth, out var parentKind) && parentKind == SchemaObjectKind.Definition &&
            propertyNames.TryGetValue(depth, out var property) && property == "Type")
            return SchemaObjectKind.TypeKey;
        return SchemaObjectKind.Unknown;
    }

    private static ArrayRole GetArrayRole(int depth, string property,
        IReadOnlyDictionary<int, SchemaObjectKind> objectKinds)
    {
        if (depth == 0) return ArrayRole.Definitions;
        var parentDepth = depth - 1;
        if (objectKinds.TryGetValue(parentDepth, out var owner))
        {
            if (owner == SchemaObjectKind.Root && property == "Definitions") return ArrayRole.Definitions;
            if (owner == SchemaObjectKind.Definition && property == "Evidence") return ArrayRole.Evidence;
            if (owner == SchemaObjectKind.Definition && property == "Fields") return ArrayRole.Fields;
        }
        return ArrayRole.Other;
    }

    private static void RemoveDeeperProperties(Dictionary<int, string> propertyNames, int depth)
    {
        foreach (var key in propertyNames.Keys.Where(key => key > depth).ToArray())
            propertyNames.Remove(key);
    }

    private static void ValidateSchemaEnum(Utf8JsonReader reader,
        IReadOnlyDictionary<int, string> propertyNames,
        IReadOnlyDictionary<int, SchemaObjectKind> objectKinds)
    {
        var valueDepth = reader.CurrentDepth;
        if (!propertyNames.TryGetValue(valueDepth, out var property) ||
            !objectKinds.TryGetValue(valueDepth - 1, out var owner)) return;
        if (owner == SchemaObjectKind.TypeKey && property == "Domain")
        {
            if (reader.TokenType != JsonTokenType.String ||
                reader.GetString() is not ("Archive" or "Native" or "SerializedClass"))
                throw new InvalidDataException("A type key must use the Archive, Native, or SerializedClass domain.");
        }
        if (owner == SchemaObjectKind.Field && property == "Evidence" && !IsEvidenceLevel(reader))
            throw new InvalidDataException("A type field evidence level is not recognized.");
        if (owner == SchemaObjectKind.EvidenceItem && property == "Level" && !IsEvidenceLevel(reader))
            throw new InvalidDataException("A type evidence level is not recognized.");
    }

    private static bool IsEvidenceLevel(Utf8JsonReader reader) => reader.TokenType == JsonTokenType.String &&
        reader.GetString() is "ObservedStructure" or "ConsumerConfirmed" or "RuntimeValidated" or "Inferred";

    private static IReadOnlyList<string> ReadStringArray(JsonElement owner, string property, CancellationToken token) =>
        ReadArray(owner, property, item => item.ValueKind == JsonValueKind.String
            ? item.GetString()! : throw new InvalidDataException($"{property} must contain strings."), token);

    private static SourceRevision ReadRevision(JsonElement element)
    {
        EnsureObject(element, "source revision");
        return new(RequiredString(element, "IndexContentId"), RequiredString(element, "PhysicalSourceStamp"));
    }

    private static TypeDomain ParseDomain(string value) => value switch
    {
        "Archive" => TypeDomain.Archive,
        "Native" => TypeDomain.Native,
        "SerializedClass" => TypeDomain.SerializedClass,
        _ => throw new InvalidDataException("A type key must use the Archive, Native, or SerializedClass domain.")
    };

    private static void EnsureObject(JsonElement element, string label)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"A {label} must be an object.");
    }

    private static JsonElement Required(JsonElement owner, string property) =>
        owner.ValueKind == JsonValueKind.Object && owner.TryGetProperty(property, out var value)
            ? value : throw new InvalidDataException($"The type-evidence dataset is missing {property}.");

    private static JsonElement? Optional(JsonElement owner, string property) =>
        owner.ValueKind == JsonValueKind.Object && owner.TryGetProperty(property, out var value) ? value : null;

    private static string RequiredString(JsonElement owner, string property)
    {
        var value = Required(owner, property);
        if (value.ValueKind != JsonValueKind.String) throw new InvalidDataException($"{property} must be a string.");
        return value.GetString()!;
    }

    private static string? OptionalString(JsonElement owner, string property)
    {
        var value = Optional(owner, property);
        if (value is null || value.Value.ValueKind == JsonValueKind.Null) return null;
        if (value.Value.ValueKind != JsonValueKind.String) throw new InvalidDataException($"{property} must be a string or null.");
        return value.Value.GetString();
    }

    private static uint? OptionalUInt32(JsonElement owner, string property)
    {
        var value = Optional(owner, property);
        if (value is null || value.Value.ValueKind == JsonValueKind.Null) return null;
        if (value.Value.ValueKind != JsonValueKind.Number || !value.Value.TryGetUInt32(out var number))
            throw new InvalidDataException($"{property} must be an unsigned 32-bit integer or null.");
        return number;
    }

    private static long? OptionalInt64(JsonElement owner, string property)
    {
        var value = Optional(owner, property);
        if (value is null || value.Value.ValueKind == JsonValueKind.Null) return null;
        if (value.Value.ValueKind != JsonValueKind.Number || !value.Value.TryGetInt64(out var number))
            throw new InvalidDataException($"{property} must be a signed 64-bit integer or null.");
        return number;
    }

    private static bool OptionalBoolean(JsonElement owner, string property)
    {
        var value = Optional(owner, property);
        if (value is null) return false;
        return value.Value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new InvalidDataException($"{property} must be a boolean.")
        };
    }

    private enum SchemaObjectKind { Unknown, Root, Definition, TypeKey, Field, EvidenceItem }
    private enum ArrayRole { Other, Definitions, Fields, Evidence }
    private readonly record struct ArrayFrame(int Depth, ArrayRole Role, int Count = 0);
}
